using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Seamline.Desktop.Video;

/// <summary>
/// Mostra um fluxo de vídeo do celular num <see cref="Image"/> do WPF. Uma thread lê e decodifica
/// o vídeo; a cada quadro desenhado pelo WPF (CompositionTarget.Rendering) copiamos o quadro mais
/// recente para o bitmap. Assim, se o PC atrasar, quadros velhos são descartados em vez de acumular
/// atraso. Se o vídeo mudar de tamanho no meio (janela de app redimensionada), o decodificador e o
/// bitmap são recriados.
/// </summary>
public sealed class VideoPresenter : IDisposable
{
    private readonly Image _image;
    private readonly VideoStream _stream;
    private readonly Dispatcher _ui;
    private readonly Action<string> _log;

    private readonly Lock _frameLock = new();
    private int _frameWidth;
    private int _frameHeight;
    private byte[] _front;
    private byte[] _back;
    private bool _frameReady;
    private WriteableBitmap? _bitmap;

    private volatile bool _closing;

    /// <summary>Tamanho atual do vídeo em pixels (atualizado na thread da interface).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>O primeiro quadro apareceu (na thread da interface).</summary>
    public event Action? FirstFrame;

    /// <summary>O vídeo acabou: a conexão caiu ou o celular fechou (na thread da interface).</summary>
    public event Action? Ended;

    public VideoPresenter(Image image, VideoStream stream, int width, int height, Action<string> log)
    {
        _image = image;
        _stream = stream;
        _ui = image.Dispatcher;
        _log = log;
        Width = _frameWidth = width;
        Height = _frameHeight = height;
        _front = new byte[width * height * 4];
        _back = new byte[width * height * 4];

        CompositionTarget.Rendering += OnRendering;
        new Thread(DecodeLoop) { IsBackground = true, Name = "Decodificação de vídeo" }.Start();
    }

    private void DecodeLoop()
    {
        H264Decoder? decoder = null;
        try
        {
            decoder = new H264Decoder(_frameWidth, _frameHeight);
            while (!_closing)
            {
                var packet = _stream.ReadPacket();
                if (packet is null)
                    break;
                if (packet.IsSizeChange)
                {
                    var (width, height) = packet.NewSize;
                    decoder.Dispose();
                    decoder = null;
                    decoder = new H264Decoder(width, height);
                    lock (_frameLock)
                    {
                        _frameWidth = width;
                        _frameHeight = height;
                        _front = new byte[width * height * 4];
                        _frameReady = false;
                    }
                    _back = new byte[width * height * 4];
                    continue;
                }
                if (!decoder.Decode(packet, _back))
                    continue;
                lock (_frameLock)
                {
                    (_front, _back) = (_back, _front);
                    _frameReady = true;
                }
            }
        }
        catch (Exception e) when (!_closing)
        {
            _ui.BeginInvoke(() => _log($"Erro no vídeo: {e.Message}"));
        }
        catch
        {
            // A janela foi fechada: a conexão cai e a leitura falha, é esperado.
        }
        finally
        {
            decoder?.Dispose();
        }

        if (!_closing)
            _ui.BeginInvoke(() => Ended?.Invoke());
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var first = _bitmap is null;
        lock (_frameLock)
        {
            if (!_frameReady)
                return;
            if (_bitmap is null || _bitmap.PixelWidth != _frameWidth || _bitmap.PixelHeight != _frameHeight)
            {
                _bitmap = new WriteableBitmap(_frameWidth, _frameHeight, 96, 96, PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
                Width = _frameWidth;
                Height = _frameHeight;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, _frameWidth, _frameHeight), _front, _frameWidth * 4, 0);
            _frameReady = false;
        }
        if (first)
            FirstFrame?.Invoke();
    }

    public void Dispose()
    {
        _closing = true;
        CompositionTarget.Rendering -= OnRendering;
    }
}
