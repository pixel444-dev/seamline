using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using Seamline.Desktop.Adb;
using Seamline.Desktop.PcWindows;
using Seamline.Desktop.Protocol;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// O painel gamer na tela do celular. Mesmo caminho da janela do PC no celular (Fase 5): túnel
/// "adb reverse" + o PC abre a tela GamerPanelActivity, que conecta aqui. Só que em vez de vídeo
/// vai uma mensagem pequena (JSON) por segundo com os números: quase nada de rede e de CPU,
/// o que importa enquanto se joga. O celular desenha o painel sozinho.
/// Termina quando o celular fecha a tela (Voltar) ou a conexão cai.
/// </summary>
public sealed class PhonePanelSession
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly int _port;
    private readonly TcpClient _client;
    private readonly MessageChannel _channel;
    private readonly Dispatcher _ui;
    private IDisposable? _subscription;
    private int _sending;
    private int _ended;

    /// <summary>O celular fechou o painel (na thread da interface).</summary>
    public event Action? Ended;

    private PhonePanelSession(AdbClient adb, string serial, int port, TcpClient client)
    {
        _adb = adb;
        _serial = serial;
        _port = port;
        _client = client;
        _channel = new MessageChannel(client.GetStream());
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Precisa ser chamado na thread da interface.</summary>
    public static async Task<PhonePanelSession> StartAsync(DeviceSession device, Action<string> log)
    {
        var adb = device.Adb;
        var serial = device.Serial;
        await PcWindowSession.EnsureAppInstalledAsync(adb, serial, log);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await adb.RunAsync(serial, ["reverse", $"tcp:{port}", $"tcp:{port}"]);
            await adb.RunAsync(serial,
                ["shell", "am", "start", "-n", "com.seamline.app/.GamerPanelActivity", "--ei", "port", port.ToString()]);
            var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            client.NoDelay = true;
            var session = new PhonePanelSession(adb, serial, port, client);
            session.Run();
            return session;
        }
        catch
        {
            try { await adb.RunAsync(serial, ["reverse", "--remove", $"tcp:{port}"]); } catch (AdbException) { }
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void Run()
    {
        _subscription = MetricsCollector.Shared.Subscribe(Send);
        _ = Task.Run(async () =>
        {
            try
            {
                // O celular não manda nada; a leitura só serve para saber quando ele fecha.
                while (await _channel.ReceiveAsync() is not null)
                {
                }
            }
            catch (Exception)
            {
            }
            End();
        });
    }

    private async void Send(PcMetrics metrics)
    {
        // Se o envio anterior ainda não terminou (Wi-Fi lento), pula esta leitura.
        if (Interlocked.Exchange(ref _sending, 1) != 0)
            return;
        try
        {
            await _channel.SendAsync(metrics.ToJson());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            End();
        }
        finally
        {
            Volatile.Write(ref _sending, 0);
        }
    }

    /// <summary>Fecha o painel no celular.</summary>
    public void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        _subscription?.Dispose();
        _client.Dispose();
        _ui.BeginInvoke(async () =>
        {
            try { await _adb.RunAsync(_serial, ["reverse", "--remove", $"tcp:{_port}"]); } catch (AdbException) { }
            Ended?.Invoke();
        });
    }
}
