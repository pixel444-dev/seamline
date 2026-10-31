using System.Windows;
using Seamline.Desktop.Adb;
using Seamline.Desktop.Input;
using Seamline.Desktop.PcWindows;

namespace Seamline.Desktop;

public partial class MainWindow : Window
{
    private readonly AdbClient _adb = AdbClient.Locate();
    private DeviceSession? _session;
    private MirrorWindow? _mirror;
    private CursorBridge? _bridge;
    private readonly Dictionary<int, AppWindow> _apps = [];
    private PcWindowSession? _pcWindow;
    private bool _sendingWindow;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshDevicesAsync();
        Closing += async (_, _) => await DisconnectAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new PairingWindow(_adb, Log) { Owner = this };
        if (window.ShowDialog() == true)
            await RefreshDevicesAsync(window.ConnectedSerial);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            await DisconnectAsync();
            return;
        }
        if (DeviceList.SelectedItem is not AdbDevice { IsReady: true } device)
        {
            Log("Escolha um celular conectado (estado \"device\").");
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Conectando...";
        try
        {
            _session = await DeviceSession.StartAsync(_adb, device.Serial, line => Dispatcher.Invoke(() => Log(line)));
            StatusText.Text = $"Conectado a {_session.Info}";
            ConnectButton.Content = "Desconectar";
            PingButton.IsEnabled = true;
            BringAppButton.IsEnabled = true;
            SendWindowButton.IsEnabled = true;
            _session.AppMessage += message => Dispatcher.BeginInvoke(() =>
            {
                if ((int?)message["id"] is { } id && _apps.TryGetValue(id, out var app))
                    app.HandleMessage(message);
            });

            _mirror = new MirrorWindow(_session, Log);
            _mirror.Closed += async (_, _) => await DisconnectAsync();
            _mirror.Show();
            _session.ConnectionLost += () => Dispatcher.BeginInvoke(async () =>
            {
                Log("A conexão com o celular caiu.");
                await DisconnectAsync();
            });
            UpdateBridge();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao conectar.";
            Log($"Erro: {ex.Message}");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void BringAppButton_Click(object sender, RoutedEventArgs e) => OpenAppWindow(atCursor: false);

    /// <summary>Traz o app da frente no celular para uma janela do PC (Fase 3).</summary>
    private void OpenAppWindow(bool atCursor)
    {
        if (_session is null)
            return;
        var window = new AppWindow(_session, Log);
        _apps[window.Id] = window;
        window.Closed += (_, _) => _apps.Remove(window.Id);
        if (atCursor && _bridge is { } bridge)
            window.ShowAtCursor(() => bridge.DragHeld);
        else
            window.Show();
    }

    /// <summary>O usuário saiu do celular arrastando a tela com o botão apertado.</summary>
    private void OnDraggedOut(string kind)
    {
        if (kind == "app")
            OpenAppWindow(atCursor: true);
        else if (kind == "pc_window" && _pcWindow is { } pcWindow)
            pcWindow.ReturnAtCursor(_bridge?.DragHeld ?? false);
    }

    /// <summary>Uma janela foi arrastada e empurrada contra a borda do celular.</summary>
    private void OnWindowPushed(IntPtr hwnd)
    {
        var app = _apps.Values.FirstOrDefault(a => a.Handle == hwnd);
        if (app is not null)
        {
            app.Close(); // o app volta para a tela do celular
            return;
        }
        // As outras janelas do próprio Seamline (como o espelho) não vão para o celular.
        WindowNative.GetWindowThreadProcessId(hwnd, out var process);
        if (process != Environment.ProcessId)
            _ = SendWindowToPhoneAsync(hwnd);
    }

    /// <summary>Lista as janelas abertas no PC (as que aparecem na barra de tarefas).</summary>
    private void WindowList_DropDownOpened(object? sender, EventArgs e)
    {
        var own = Environment.ProcessId;
        var windows = new List<WindowItem>();
        WindowNative.EnumWindows((hwnd, _) =>
        {
            if (WindowNative.IsWindowVisible(hwnd) && WindowNative.GetWindow(hwnd, WindowNative.GW_OWNER) == 0
                && (WindowNative.GetWindowLongPtr(hwnd, WindowNative.GWL_EXSTYLE) & WindowNative.WS_EX_TOOLWINDOW) == 0
                && !(WindowNative.DwmGetWindowAttribute(hwnd, WindowNative.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                && WindowNative.GetWindowThreadProcessId(hwnd, out var process) != 0 && process != own
                && WindowNative.GetTitle(hwnd) is { Length: > 0 } title)
            {
                windows.Add(new WindowItem(hwnd, title));
            }
            return true;
        }, 0);
        WindowList.ItemsSource = windows;
    }

    private sealed record WindowItem(IntPtr Handle, string Title)
    {
        public override string ToString() => Title;
    }

    private async void SendWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is WindowItem item)
            await SendWindowToPhoneAsync(item.Handle);
        else
            Log("Escolha uma janela na lista.");
    }

    /// <summary>Mostra uma janela do PC no celular (Fase 5). Uma por vez: a anterior volta para o PC.</summary>
    private async Task SendWindowToPhoneAsync(IntPtr hwnd)
    {
        if (_session is null || _sendingWindow)
            return;
        _sendingWindow = true;
        SendWindowButton.IsEnabled = false;
        try
        {
            _pcWindow?.End();
            var title = WindowNative.GetTitle(hwnd);
            Log($"Mandando \"{title}\" para o celular...");
            var pcWindow = await PcWindowSession.StartAsync(_session, hwnd, Log);
            _pcWindow = pcWindow;
            pcWindow.Ended += () =>
            {
                if (_pcWindow == pcWindow)
                    _pcWindow = null;
            };
            Log($"\"{title}\" está no celular. Voltar no celular (ou arrastar para fora pela borda) traz de volta.");
        }
        catch (Exception ex)
        {
            Log($"Não consegui mandar a janela: {ex.Message}");
        }
        finally
        {
            _sendingWindow = false;
            SendWindowButton.IsEnabled = _session is not null;
        }
    }

    private async void PingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;
        try
        {
            var time = await _session.PingAsync();
            Log($"Resposta do celular em {time.TotalMilliseconds:0.0} ms");
        }
        catch (Exception ex)
        {
            Log($"Erro no ping: {ex.Message}");
            await DisconnectAsync();
        }
    }

    private async Task RefreshDevicesAsync(string? select = null)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            select ??= (DeviceList.SelectedItem as AdbDevice)?.Serial;
            var devices = await _adb.GetDevicesAsync();
            try
            {
                // Celulares já pareados com a Depuração sem fio ligada: conecta se o adb ainda não conectou.
                await _adb.ConnectKnownDevicesAsync(devices);
                devices = await _adb.GetDevicesAsync();
            }
            catch (AdbException ex)
            {
                Log($"Não consegui procurar celulares no Wi-Fi: {ex.Message}");
            }
            DeviceList.ItemsSource = devices;
            DeviceList.SelectedItem = devices.FirstOrDefault(d => d.Serial == select && d.IsReady)
                ?? devices.FirstOrDefault(d => d.IsReady) ?? devices.FirstOrDefault();
            Log(devices.Count == 0
                ? "Nenhum celular encontrado. Ligue o cabo USB (com a Depuração USB ativa) ou clique em \"Parear pelo Wi-Fi...\"."
                : $"{devices.Count} aparelho(s) encontrado(s).");
        }
        catch (Exception ex)
        {
            Log($"Não consegui rodar o adb ({_adb.AdbPath}): {ex.Message}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_session is null)
            return;
        var session = _session;
        _session = null;
        _bridge?.Dispose();
        _bridge = null;
        foreach (var app in _apps.Values.ToList())
            app.Close();
        _pcWindow?.End();
        _pcWindow = null;
        var mirror = _mirror;
        _mirror = null;
        mirror?.Close();
        await session.DisposeAsync();
        StatusText.Text = "Desconectado.";
        ConnectButton.Content = "Conectar";
        PingButton.IsEnabled = false;
        BringAppButton.IsEnabled = false;
        SendWindowButton.IsEnabled = false;
        Log("Desconectado.");
    }

    private void BridgeCheck_Changed(object sender, RoutedEventArgs e) => UpdateBridge();

    /// <summary>Liga ou desliga a passagem do mouse pela borda, conforme a caixa de seleção.</summary>
    private void UpdateBridge()
    {
        var wanted = _session is not null && BridgeCheck.IsChecked == true;
        if (wanted && _bridge is null)
        {
            try
            {
                _bridge = new CursorBridge(_session!, Log);
                _bridge.DraggedOut += OnDraggedOut;
                _bridge.WindowPushed += OnWindowPushed;
                Log("Leve o cursor até a borda esquerda do monitor para controlar o celular.");
            }
            catch (Exception ex)
            {
                Log($"Não consegui ativar o mouse no celular: {ex.Message}");
            }
        }
        else if (!wanted && _bridge is not null)
        {
            _bridge.Dispose();
            _bridge = null;
        }
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
