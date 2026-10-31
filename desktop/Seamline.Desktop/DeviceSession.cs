using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Seamline.Desktop.Adb;
using Seamline.Desktop.Protocol;
using Seamline.Desktop.Video;

namespace Seamline.Desktop;

public sealed record DeviceInfo(string Manufacturer, string Model, string AndroidVersion, int Sdk)
{
    public override string ToString() => $"{Manufacturer} {Model} — Android {AndroidVersion} (API {Sdk})";
}

/// <summary>
/// Uma conexão com um celular. Para abrir:
/// 1. envia o server (seamline-server.apk) para o celular;
/// 2. cria um túnel do adb: uma porta do PC → o socket "seamline" no celular;
/// 3. inicia o server com app_process (ele roda com permissão de "shell", como no scrcpy);
/// 4. conecta no túnel e espera a mensagem "hello" (conexão de controle);
/// 5. abre a segunda conexão, a do vídeo, e lê o tamanho do vídeo.
/// </summary>
public sealed class DeviceSession : IAsyncDisposable
{
    public const int ProtocolVersion = 2;
    /// <summary>Maior lado do vídeo, em pixels. A tela do celular é reduzida para caber.</summary>
    public const int MaxVideoSize = 1600;
    private const string RemoteApkPath = "/data/local/tmp/seamline-server.apk";
    private const string SocketName = "seamline";
    private const string ServerClass = "com.seamline.server.Server";

    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly int _localPort;
    private readonly Process _serverProcess;
    private readonly MessageChannel _channel;
    private readonly TcpClient _videoClient;

    public DeviceInfo Info { get; }
    public VideoStream Video { get; }
    public int VideoWidth { get; }
    public int VideoHeight { get; }

    private DeviceSession(AdbClient adb, string serial, int localPort, Process serverProcess, MessageChannel channel,
        DeviceInfo info, TcpClient videoClient, VideoStream video, int videoWidth, int videoHeight)
    {
        _adb = adb;
        _serial = serial;
        _localPort = localPort;
        _serverProcess = serverProcess;
        _channel = channel;
        Info = info;
        _videoClient = videoClient;
        Video = video;
        VideoWidth = videoWidth;
        VideoHeight = videoHeight;
    }

    public static string LocalServerApk => Path.Combine(AppContext.BaseDirectory, "seamline-server.apk");

    public static async Task<DeviceSession> StartAsync(AdbClient adb, string serial, Action<string> log, CancellationToken ct = default)
    {
        if (!File.Exists(LocalServerApk))
            throw new FileNotFoundException("seamline-server.apk não encontrado. Compile o projeto android (módulo server) antes do app do PC.", LocalServerApk);

        log("Enviando o server para o celular...");
        await adb.RunAsync(serial, ["push", LocalServerApk, RemoteApkPath], ct);

        var localPort = FindFreePort();
        await adb.RunAsync(serial, ["forward", $"tcp:{localPort}", $"localabstract:{SocketName}"], ct);

        log("Iniciando o server...");
        var process = adb.StartLongRunning(serial,
            ["shell", $"CLASSPATH={RemoteApkPath}", "app_process", "/", ServerClass,
                ProtocolVersion.ToString(), MaxVideoSize.ToString()],
            line => log($"[celular] {line}"));

        MessageChannel? channel = null;
        TcpClient? videoClient = null;
        try
        {
            (channel, var info) = await ConnectAsync(localPort, process, ct);
            log($"Conectado: {info}");

            videoClient = new TcpClient { NoDelay = true };
            await videoClient.ConnectAsync(IPAddress.Loopback, localPort, ct);
            var video = new VideoStream(videoClient.GetStream());
            var (width, height) = await Task.Run(video.ReadHeader, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            log($"Vídeo: {width}x{height}");

            return new DeviceSession(adb, serial, localPort, process, channel, info, videoClient, video, width, height);
        }
        catch
        {
            channel?.Dispose();
            videoClient?.Dispose();
            KillQuietly(process);
            await RemoveForwardQuietly(adb, serial, localPort);
            throw;
        }
    }

    /// <summary>
    /// O túnel do adb aceita a conexão mesmo antes do server estar pronto (e fecha logo em seguida),
    /// então tentamos de novo até receber o "hello".
    /// </summary>
    private static async Task<(MessageChannel, DeviceInfo)> ConnectAsync(int port, Process server, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new IOException($"O server fechou antes de conectar (código {server.ExitCode}). Veja o registro.");

            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                var channel = new MessageChannel(client.GetStream());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var hello = await channel.ReceiveAsync(timeout.Token);
                if (hello is not null && (string?)hello["type"] == "hello")
                    return (channel, ParseHello(hello));
                channel.Dispose();
            }
            catch (Exception e) when (e is IOException or SocketException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                client.Dispose();
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("O celular não respondeu a tempo.");
            await Task.Delay(200, ct);
        }
    }

    private static DeviceInfo ParseHello(JsonObject hello)
    {
        var protocol = (int?)hello["protocol"] ?? 0;
        if (protocol != ProtocolVersion)
            throw new InvalidDataException($"Versão do protocolo diferente: PC {ProtocolVersion}, celular {protocol}.");
        return new DeviceInfo(
            (string?)hello["manufacturer"] ?? "?",
            (string?)hello["model"] ?? "?",
            (string?)hello["android"] ?? "?",
            (int?)hello["sdk"] ?? 0);
    }

    /// <summary>Envia uma mensagem de controle (toque, rolagem, tecla...). Veja docs/protocolo.md.</summary>
    public Task SendAsync(JsonObject message, CancellationToken ct = default) => _channel.SendAsync(message, ct);

    /// <summary>Mede o tempo de ida e volta de uma mensagem até o celular.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        await _channel.SendAsync(new JsonObject { ["type"] = "ping" }, ct);
        var reply = await _channel.ReceiveAsync(ct) ?? throw new IOException("O celular fechou a conexão.");
        if ((string?)reply["type"] != "pong")
            throw new InvalidDataException($"Resposta inesperada: {reply.ToJsonString()}");
        return watch.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.SendAsync(new JsonObject { ["type"] = "bye" });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        _channel.Dispose();
        _videoClient.Dispose();
        if (!_serverProcess.WaitForExit(1000))
            KillQuietly(_serverProcess);
        _serverProcess.Dispose();
        await RemoveForwardQuietly(_adb, _serial, _localPort);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static async Task RemoveForwardQuietly(AdbClient adb, string serial, int port)
    {
        try { await adb.RunAsync(serial, ["forward", "--remove", $"tcp:{port}"]); } catch (AdbException) { }
    }
}
