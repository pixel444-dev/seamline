using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seamline.Desktop.Input;
using Seamline.Desktop.Video;

namespace Seamline.Desktop;

/// <summary>
/// Mostra a tela do celular. Uma thread lê e decodifica o vídeo; a cada quadro desenhado pelo WPF
/// (CompositionTarget.Rendering) copiamos o quadro mais recente para o bitmap. Assim, se o PC
/// atrasar, quadros velhos são descartados em vez de acumular atraso.
/// </summary>
public partial class MirrorWindow : Window
{
    private readonly DeviceSession _session;
    private readonly Action<string> _log;
    private readonly int _width;
    private readonly int _height;
    private readonly WriteableBitmap _bitmap;

    private readonly Lock _frameLock = new();
    private byte[] _front;
    private byte[] _back;
    private bool _frameReady;

    private volatile bool _closing;
    private bool _touching;
    private readonly HidKeyboardState _keys = new();

    public MirrorWindow(DeviceSession session, Action<string> log)
    {
        InitializeComponent();
        _session = session;
        _log = log;
        _width = session.VideoWidth;
        _height = session.VideoHeight;
        _front = new byte[_width * _height * 4];
        _back = new byte[_width * _height * 4];
        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        Screen.Source = _bitmap;
        Title = $"Seamline — {session.Info.Model}";

        // Tamanho inicial: 85% da altura útil da tela do PC, mantendo a proporção do celular.
        var scale = Math.Min(1.0, SystemParameters.WorkArea.Height * 0.85 / _height);
        Screen.Width = _width * scale;
        Screen.Height = _height * scale;
        ContentRendered += (_, _) =>
        {
            // Depois de abrir, a janela pode ser redimensionada à vontade.
            SizeToContent = SizeToContent.Manual;
            Screen.Width = double.NaN;
            Screen.Height = double.NaN;
        };

        // Com a janela em foco, o teclado do PC digita no celular.
        Activated += (_, _) => _session.SetKeyboardActive(this, true);
        Deactivated += (_, _) => ReleaseKeys();
        PreviewKeyDown += (_, e) => OnKey(e, down: true);
        PreviewKeyUp += (_, e) => OnKey(e, down: false);

        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            _closing = true;
            ReleaseKeys();
            CompositionTarget.Rendering -= OnRendering;
        };

        new Thread(DecodeLoop) { IsBackground = true, Name = "Decodificação de vídeo" }.Start();
    }

    private void DecodeLoop()
    {
        try
        {
            using var decoder = new H264Decoder(_width, _height);
            while (!_closing)
            {
                var packet = _session.Video.ReadPacket();
                if (packet is null)
                    break;
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
            Dispatcher.BeginInvoke(() => _log($"Erro no vídeo: {e.Message}"));
        }
        catch
        {
            // A janela foi fechada: a conexão cai e a leitura falha, é esperado.
        }

        if (!_closing)
            Dispatcher.BeginInvoke(Close);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        lock (_frameLock)
        {
            if (!_frameReady)
                return;
            _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _front, _width * 4, 0);
            _frameReady = false;
        }
    }

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                Screen.CaptureMouse();
                _touching = true;
                SendTouch("down", e);
                break;
            case MouseButton.Right:
            case MouseButton.XButton1:
                SendKey("back");
                break;
            case MouseButton.Middle:
                SendKey("home");
                break;
            case MouseButton.XButton2:
                SendKey("app_switch");
                break;
        }
        e.Handled = true;
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (_touching)
            SendTouch("move", e);
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_touching)
            return;
        SendTouch("up", e);
        _touching = false;
        Screen.ReleaseMouseCapture();
    }

    private void Screen_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Ex.: alt+tab no meio de um arraste. Solta o dedo para o celular não ficar "pressionado".
        if (!_touching)
            return;
        _touching = false;
        SendTouch("up", e);
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var (x, y) = Normalize(e);
        Send(new JsonObject { ["type"] = "scroll", ["x"] = x, ["y"] = y, ["dx"] = 0, ["dy"] = e.Delta / 120.0 });
    }

    private void SendTouch(string action, MouseEventArgs e)
    {
        var (x, y) = Normalize(e);
        Send(new JsonObject { ["type"] = "touch", ["action"] = action, ["x"] = x, ["y"] = y });
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        // Alt+F4 continua fechando a janela.
        if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var scan = (int)Native.MapVirtualKey((uint)virtualKey, Native.MAPVK_VK_TO_VSC_EX);
        var hid = HidKeys.FromScanCode(scan & 0xFF, (scan & 0xFF00) == 0xE000, virtualKey);
        if (hid == 0)
            return;
        e.Handled = true;
        if (down ? _keys.Press(hid) : _keys.Release(hid))
            Send(_keys.ToMessage());
    }

    private void ReleaseKeys()
    {
        if (!_keys.IsEmpty)
        {
            _keys.Clear();
            Send(_keys.ToMessage());
        }
        _session.SetKeyboardActive(this, false);
    }

    private void SendKey(string key) => Send(new JsonObject { ["type"] = "key", ["key"] = key });

    /// <summary>Posição do mouse de 0 a 1 dentro da imagem, independente do tamanho da janela.</summary>
    private (double X, double Y) Normalize(MouseEventArgs e)
    {
        var p = e.GetPosition(Screen);
        return (Math.Clamp(p.X / Screen.ActualWidth, 0, 1), Math.Clamp(p.Y / Screen.ActualHeight, 0, 1));
    }

    private async void Send(JsonObject message)
    {
        try
        {
            await _session.SendAsync(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Conexão caiu; o DecodeLoop percebe e fecha a janela.
        }
    }
}
