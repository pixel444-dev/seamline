using System.IO;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Seamline.Desktop.Input;
using Seamline.Desktop.Video;
using static Seamline.Desktop.Input.Native;

namespace Seamline.Desktop;

/// <summary>
/// Fase 3 (F2): um app do celular numa janela normal do Windows. O app continua rodando no celular,
/// numa tela virtual do tamanho desta janela (veja AppWindows.kt): redimensionar a janela
/// redimensiona essa tela. Fechar a janela, ou arrastá-la de volta para a borda do celular, devolve
/// o app para a tela do celular.
/// </summary>
public partial class AppWindow : Window
{
    private static int s_nextId;

    private readonly DeviceSession _session;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _resizeTimer;
    private TcpClient? _videoClient;
    private VideoPresenter? _video;
    private bool _opened;
    private bool _failed;
    private bool _closedByPhone;
    private (int Width, int Height) _sentSize;

    /// <summary>Identifica a janela nas mensagens com o celular.</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextId);

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public AppWindow(DeviceSession session, Action<string> log)
    {
        InitializeComponent();
        _session = session;
        _log = log;
        _ = new ScreenInput(this, Screen, session, Id);

        // Tamanho inicial "de celular" (1 : 2), 75% da altura útil da tela do PC.
        Area.Height = SystemParameters.WorkArea.Height * 0.75;
        Area.Width = Area.Height / 2;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Redimensionar manda o novo tamanho só quando o usuário para de arrastar a borda.
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            SendSize();
        };
        Area.SizeChanged += (_, _) =>
        {
            if (!_opened)
                return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };

        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) => Cleanup();
    }

    /// <summary>
    /// Abre a janela embaixo do cursor. Se o usuário ainda está segurando o botão (veio arrastando o
    /// app do celular), "pega" a janela pela barra de título para ela continuar seguindo o mouse.
    /// </summary>
    public void ShowAtCursor(Func<bool> dragHeld)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => MoveUnderCursor();
        ContentRendered += (_, _) =>
        {
            if (!dragHeld())
                return;
            MoveUnderCursor();
            // Um clique na barra de título começa o arraste normal do Windows; o usuário solta o
            // botão de verdade quando quiser.
            InjectLeftButton(down: true);
        };
        Show();
    }

    private void MoveUnderCursor()
    {
        GetCursorPos(out var cursor);
        // 80 px para a direita da borda (longe do ícone) e 16 px abaixo do topo: dentro da barra de título.
        SetWindowPos(Handle, 0, cursor.X - 80, cursor.Y - 16, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private async Task OpenAsync()
    {
        var size = PixelSize();
        var dpi = (int)Math.Round(160 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        try
        {
            await _session.SendAsync(new JsonObject
            {
                ["type"] = "app_open",
                ["id"] = Id,
                ["width"] = size.Width,
                ["height"] = size.Height,
                ["dpi"] = dpi,
            });
            _sentSize = size;
            var (client, video, width, height) = await _session.OpenAppVideoAsync(Id);
            _videoClient = client;
            if (!IsVisible)
            {
                client.Dispose(); // fechada enquanto abria
                return;
            }
            _video = new VideoPresenter(Screen, video, width, height, _log);
            _video.FirstFrame += () => StatusText.Visibility = Visibility.Collapsed;
            _video.Ended += () =>
            {
                if (!_failed)
                    Close();
            };
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or ObjectDisposedException)
        {
            // Normalmente o celular também manda um "app_error" explicando melhor.
            Fail($"Não consegui trazer o app: {e.Message}");
        }

        // Daqui em diante a janela pode ser redimensionada à vontade.
        SizeToContent = SizeToContent.Manual;
        Area.Width = double.NaN;
        Area.Height = double.NaN;
        _opened = true;
    }

    /// <summary>Mensagens do celular sobre esta janela (app_opened, app_error, app_closed).</summary>
    public void HandleMessage(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "app_opened":
                var label = (string?)message["label"] ?? (string?)message["package"] ?? "App";
                Title = $"{label} — Seamline";
                _log($"{label} foi para uma janela do PC.");
                break;
            case "app_error":
                Fail((string?)message["message"] ?? "O celular recusou.");
                break;
            case "app_closed":
                _closedByPhone = true;
                Close();
                break;
        }
    }

    private void Fail(string text)
    {
        if (!_failed)
            _log(text);
        _failed = true;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    private (int Width, int Height) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Round(Area.ActualWidth * dpi.DpiScaleX), (int)Math.Round(Area.ActualHeight * dpi.DpiScaleY));
    }

    private async void SendSize()
    {
        var size = PixelSize();
        // O celular arredonda para múltiplos de 16; mudanças menores não mudam nada.
        if (_failed || (Math.Abs(size.Width - _sentSize.Width) < 16 && Math.Abs(size.Height - _sentSize.Height) < 16))
            return;
        _sentSize = size;
        try
        {
            await _session.SendAsync(new JsonObject
            {
                ["type"] = "app_resize",
                ["id"] = Id,
                ["width"] = size.Width,
                ["height"] = size.Height,
            });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private async void Cleanup()
    {
        _resizeTimer.Stop();
        _video?.Dispose();
        _videoClient?.Dispose();
        if (_closedByPhone)
            return;
        try
        {
            await _session.SendAsync(new JsonObject { ["type"] = "app_close", ["id"] = Id });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Já desconectado: o celular devolve o app sozinho.
        }
    }
}
