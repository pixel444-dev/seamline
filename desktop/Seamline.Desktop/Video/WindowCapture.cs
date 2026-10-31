using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Seamline.Desktop.Video;

/// <summary>
/// Captura a imagem de uma janela do Windows com o Windows.Graphics.Capture (o mesmo do Recorte e
/// do Teams). Funciona mesmo com a janela coberta ou fora da tela, mas não minimizada.
/// Cada quadro novo é copiado da placa de vídeo para a memória (BGRA) e fica em <see cref="TryCopyLatest"/>.
/// O Windows só manda quadro quando a janela muda.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;

    private readonly Lock _lock = new();
    private readonly Lock _d3dLock = new();
    private byte[] _latest = [];
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Chegou um quadro novo (numa thread qualquer).</summary>
    public event Action? FrameArrived;

    /// <summary>Erro ao copiar um quadro (numa thread qualquer).</summary>
    public event Action<Exception>? Error;

    /// <summary>A janela foi fechada (numa thread qualquer).</summary>
    public event Action? Closed;

    public WindowCapture(IntPtr hwnd)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0], out _device!, out _context!).CheckError();

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var raw));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(raw);
            Marshal.Release(raw);
        }

        var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")
            .AsInterface<IGraphicsCaptureItemInterop>();
        var itemIid = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForWindow(hwnd, ref itemIid);
        _item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);
        _item.Closed += (_, _) => Closed?.Invoke();

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    /// <summary>
    /// Copia o quadro mais recente para <paramref name="buffer"/> (aumentando se precisar).
    /// Retorna false se ainda não chegou nenhum.
    /// </summary>
    public bool TryCopyLatest(ref byte[] buffer, out int width, out int height)
    {
        lock (_lock)
        {
            width = _width;
            height = _height;
            if (width == 0)
                return false;
            if (buffer.Length < _latest.Length)
                buffer = new byte[_latest.Length];
            Buffer.BlockCopy(_latest, 0, buffer, 0, width * height * 4);
            return true;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Não deixa o Dispose liberar o Direct3D no meio de uma cópia.
        lock (_d3dLock)
        {
            if (!_disposed)
                HandleFrame(sender);
        }
    }

    private void HandleFrame(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;
        var size = frame.ContentSize;
        try
        {
            if (size.Width > 0 && size.Height > 0)
                CopyFrame(frame, size.Width, size.Height);
        }
        catch (Exception e)
        {
            Error?.Invoke(e);
            return;
        }
        finally
        {
            // A janela mudou de tamanho: o "pool" precisa ser recriado para os próximos quadros.
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
        FrameArrived?.Invoke();
    }

    private unsafe void CopyFrame(Direct3D11CaptureFrame frame, int width, int height)
    {
        using var texture = new ID3D11Texture2D(GetTexture(frame.Surface));
        var desc = texture.Description;
        width = Math.Min(width, (int)desc.Width);
        height = Math.Min(height, (int)desc.Height);

        if (_staging is null || _staging.Description.Width < width || _staging.Description.Height < height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        var map = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            lock (_lock)
            {
                if (_latest.Length < width * height * 4)
                    _latest = new byte[width * height * 4];
                fixed (byte* destination = _latest)
                {
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch,
                            destination + (long)y * width * 4, width * 4, width * 4);
                }
                _width = width;
                _height = height;
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    /// <summary>
    /// A textura do Direct3D por trás de um quadro: IDirect3DDxgiInterfaceAccess::GetInterface,
    /// chamada direto pela tabela de funções do objeto COM.
    /// </summary>
    private static unsafe IntPtr GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = ((IWinRTObject)surface).NativeObject.GetRef();
        try
        {
            var accessIid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessIid, out var access));
            try
            {
                var textureIid = typeof(ID3D11Texture2D).GUID;
                IntPtr texture;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    public void Dispose()
    {
        lock (_d3dLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _staging?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }
}
