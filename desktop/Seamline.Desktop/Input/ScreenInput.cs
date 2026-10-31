using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Seamline.Desktop.Input;

/// <summary>
/// Liga o mouse e o teclado de uma janela que mostra uma tela do celular (o espelho ou a janela de
/// um app) aos toques e teclas no celular.
/// Clique esquerdo = toque/arrastar, direito = Voltar, roda = rolar, botões laterais = Voltar/Recentes,
/// do meio = Início. Com a janela em foco, o teclado do PC digita no celular.
/// </summary>
internal sealed class ScreenInput
{
    private readonly Window _window;
    private readonly Image _screen;
    private readonly DeviceSession _session;
    private readonly int? _appWindow;
    private readonly HidKeyboardState _keys = new();
    private bool _touching;

    /// <param name="appWindow">Id da janela de app no celular; null = a tela do celular (espelho).</param>
    public ScreenInput(Window window, Image screen, DeviceSession session, int? appWindow)
    {
        _window = window;
        _screen = screen;
        _session = session;
        _appWindow = appWindow;

        screen.MouseDown += OnMouseDown;
        screen.MouseMove += OnMouseMove;
        screen.MouseUp += OnMouseUp;
        screen.MouseWheel += OnMouseWheel;
        screen.LostMouseCapture += OnLostMouseCapture;

        window.Activated += (_, _) => _session.SetKeyboardActive(this, true);
        window.Deactivated += (_, _) => ReleaseKeys();
        window.PreviewKeyDown += (_, e) => OnKey(e, down: true);
        window.PreviewKeyUp += (_, e) => OnKey(e, down: false);
        window.Closed += (_, _) => ReleaseKeys();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                _screen.CaptureMouse();
                _touching = true;
                SendTouch("down", e);
                break;
            case MouseButton.Right:
            case MouseButton.XButton1:
                SendKey("back");
                break;
            // Início e Recentes são da tela do celular; numa janela de app não fazem sentido.
            case MouseButton.Middle when _appWindow is null:
                SendKey("home");
                break;
            case MouseButton.XButton2 when _appWindow is null:
                SendKey("app_switch");
                break;
        }
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_touching)
            SendTouch("move", e);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_touching)
            return;
        SendTouch("up", e);
        _touching = false;
        _screen.ReleaseMouseCapture();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Ex.: alt+tab no meio de um arraste. Solta o dedo para o celular não ficar "pressionado".
        if (!_touching)
            return;
        _touching = false;
        SendTouch("up", e);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
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
        // As teclas vão pelo teclado virtual do celular, que digita na tela que recebeu o último toque.
        if (down ? _keys.Press(hid) : _keys.Release(hid))
            Send(_keys.ToMessage(), toWindow: false);
    }

    private void ReleaseKeys()
    {
        if (!_keys.IsEmpty)
        {
            _keys.Clear();
            Send(_keys.ToMessage(), toWindow: false);
        }
        _session.SetKeyboardActive(this, false);
    }

    private void SendKey(string key) => Send(new JsonObject { ["type"] = "key", ["key"] = key });

    /// <summary>Posição do mouse de 0 a 1 dentro da imagem, independente do tamanho da janela.</summary>
    private (double X, double Y) Normalize(MouseEventArgs e)
    {
        var p = e.GetPosition(_screen);
        return (Math.Clamp(p.X / _screen.ActualWidth, 0, 1), Math.Clamp(p.Y / _screen.ActualHeight, 0, 1));
    }

    private async void Send(JsonObject message, bool toWindow = true)
    {
        if (toWindow && _appWindow is { } id)
            message["window"] = id;
        try
        {
            await _session.SendAsync(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Conexão caiu; quem mostra o vídeo percebe e fecha a janela.
        }
    }
}
