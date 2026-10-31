using System.Windows;
using Seamline.Desktop.Adb;

namespace Seamline.Desktop;

public partial class MainWindow : Window
{
    private readonly AdbClient _adb = AdbClient.Locate();
    private DeviceSession? _session;
    private MirrorWindow? _mirror;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshDevicesAsync();
        Closing += async (_, _) => await DisconnectAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

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

            _mirror = new MirrorWindow(_session, Log);
            _mirror.Closed += async (_, _) => await DisconnectAsync();
            _mirror.Show();
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

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await _adb.GetDevicesAsync();
            DeviceList.ItemsSource = devices;
            DeviceList.SelectedItem = devices.FirstOrDefault(d => d.IsReady) ?? devices.FirstOrDefault();
            Log(devices.Count == 0
                ? "Nenhum celular encontrado. Ligue o cabo USB (com a Depuração USB ativa) ou use a Depuração sem fio."
                : $"{devices.Count} aparelho(s) encontrado(s).");
        }
        catch (Exception ex)
        {
            Log($"Não consegui rodar o adb ({_adb.AdbPath}): {ex.Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        if (_session is null)
            return;
        var session = _session;
        _session = null;
        var mirror = _mirror;
        _mirror = null;
        mirror?.Close();
        await session.DisposeAsync();
        StatusText.Text = "Desconectado.";
        ConnectButton.Content = "Conectar";
        PingButton.IsEnabled = false;
        Log("Desconectado.");
    }

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
