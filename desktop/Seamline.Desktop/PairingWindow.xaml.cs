using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using Seamline.Desktop.Adb;

namespace Seamline.Desktop;

/// <summary>
/// Mostra o QR code da Depuração sem fio e fica procurando o celular na rede até parear e conectar.
/// Veja <see cref="WirelessPairing"/> para o passo a passo.
/// </summary>
public partial class PairingWindow : Window
{
    private readonly AdbClient _adb;
    private readonly Action<string> _log;
    private readonly string _name;
    private readonly string _password;
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<string> _failedAddresses = [];
    private string? _autoAddress;
    private bool _busy;

    /// <summary>O serial (no adb) do celular conectado, quando a janela fecha com sucesso.</summary>
    public string? ConnectedSerial { get; private set; }

    public PairingWindow(AdbClient adb, Action<string> log)
    {
        InitializeComponent();
        _adb = adb;
        _log = log;
        (_name, _password, var qrText) = WirelessPairing.CreateQrPairing();
        QrImage.Source = CreateQrImage(qrText);
        Loaded += async (_, _) => await WatchLoopAsync(_closing.Token);
        Closed += (_, _) => _closing.Cancel();
    }

    private static BitmapImage CreateQrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>A cada segundo, olha os serviços anunciados na rede.</summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        var warnedNoMdns = false;
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var services = await _adb.GetServicesAsync(ct);
                if (!_busy)
                {
                    // Um endereço que já falhou não é tentado de novo (o anúncio antigo pode ficar um tempo na rede).
                    var ours = services.FirstOrDefault(s => s.IsPairing && s.Name == _name && !_failedAddresses.Contains(s.Address));
                    if (ours is not null)
                        await PairAndConnectAsync(ours.Address, _password, ct);
                    else if (services.FirstOrDefault(s => s.IsPairing && s.Name != _name) is { } waitingForCode
                             && AddressBox.Text != waitingForCode.Address
                             && (AddressBox.Text.Length == 0 || AddressBox.Text == _autoAddress))
                    {
                        // O celular está na tela "Parear com código": o endereço já vem pronto, só falta o código.
                        AddressBox.Text = _autoAddress = waitingForCode.Address;
                        StatusText.Text = $"Achei um celular esperando o código de pareamento ({waitingForCode.Address}). Digite o código abaixo.";
                    }
                }
                if (!warnedNoMdns && services.Count == 0 && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                {
                    warnedNoMdns = true;
                    _log("Ainda não achei nenhum celular na rede. Confira se os dois estão no mesmo Wi-Fi " +
                         "e se o Firewall do Windows não bloqueou o adb (rede privada).");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Erro procurando o celular na rede: {ex.Message}");
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async void CodeButton_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        var code = CodeBox.Text.Trim();
        if (!address.Contains(':') || code.Length != 6)
        {
            StatusText.Text = "Preencha o endereço (IP:porta, por exemplo 192.168.0.10:37145) e o código de 6 dígitos.";
            return;
        }
        if (!_busy)
            await PairAndConnectAsync(address, code, _closing.Token);
    }

    private async Task PairAndConnectAsync(string address, string password, CancellationToken ct)
    {
        _busy = true;
        CodeButton.IsEnabled = false;
        try
        {
            StatusText.Text = $"Celular encontrado ({address}). Pareando...";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var guid = await _adb.PairAsync(address, password, timeout.Token);
            _log($"Pareado com {address}.");

            StatusText.Text = "Pareado! Conectando...";
            var serial = await WaitForConnectionAsync(guid, address.Split(':')[0], ct);
            if (serial is null)
            {
                StatusText.Text = "Pareou, mas não consegui conectar. Confira se a Depuração sem fio continua ligada e clique em Atualizar na janela principal.";
                return;
            }
            _log($"Conectado pelo Wi-Fi ({serial}).");
            ConnectedSerial = serial;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _failedAddresses.Add(address);
            StatusText.Text = $"{ex.Message} Tente ler o QR code de novo.";
            _log(ex.Message);
        }
        finally
        {
            _busy = false;
            CodeButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Depois do pareamento o adb costuma conectar sozinho (pelo mDNS). Se ele demorar, conectamos
    /// nós mesmos no serviço de conexão do celular. Devolve o serial do aparelho no adb.
    /// </summary>
    private async Task<string?> WaitForConnectionAsync(string? guid, string ip, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var devices = await _adb.GetDevicesAsync(ct);
            var services = await _adb.GetServicesAsync(ct);
            var connect = services.FirstOrDefault(s => s.IsConnect && (s.Name == guid || s.Address.StartsWith(ip + ":")));
            var device = devices.FirstOrDefault(d => d.IsReady &&
                ((guid is not null && d.Serial.StartsWith(guid)) || (connect is not null && d.Serial == connect.Address)));
            if (device is not null)
                return device.Serial;
            // Dá uns segundos para o adb conectar sozinho antes de tentar por conta própria.
            if (connect is not null && attempt >= 3 && await _adb.ConnectAsync(connect.Address, ct))
                return connect.Address;
            await Task.Delay(1000, ct);
        }
        return null;
    }
}
