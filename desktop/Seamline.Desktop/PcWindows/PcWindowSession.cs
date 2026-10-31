using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Seamline.Desktop.Adb;
using Seamline.Desktop.Input;
using Seamline.Desktop.Protocol;
using Seamline.Desktop.Video;
using static Seamline.Desktop.Input.WindowNative;

namespace Seamline.Desktop.PcWindows;

/// <summary>
/// Fase 5 (F3): uma janela do PC mostrada no celular. O programa continua rodando no PC.
///
/// 1. instala/atualiza o app Seamline no celular, se precisar;
/// 2. abre uma porta no PC e um túnel "adb reverse" (a mesma porta no celular chega aqui);
/// 3. abre a tela RemoteWindowActivity no celular, que conecta nessa porta;
/// 4. esconde a janela no PC (<see cref="WindowHider"/>), captura a imagem dela
///    (<see cref="WindowCapture"/>), codifica em H.264 e manda;
/// 5. toques e teclas que voltam do celular vão para a janela (<see cref="WindowInjector"/>).
///
/// Termina quando o celular fecha a tela (Voltar), a janela fecha, a conexão cai ou o PC pede.
/// </summary>
public sealed class PcWindowSession
{
    private const string AppPackage = "com.seamline.app";
    private const string RemoteApkVersionPath = "/data/local/tmp/seamline-app.version";
    private const int MaxVideoSize = 1920;
    private const int Fps = 30;

    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly int _port;
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly MessageChannel _input;
    private readonly WindowCapture _capture;
    private readonly WindowHider _hider;
    private readonly WindowInjector _injector;
    private readonly Action<string> _log;
    private readonly Dispatcher _ui;

    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _keyFrameRequested;
    private volatile bool _ending;
    private int _ended;
    private bool _keepHidden; // a janela já foi devolvida pelo arraste: não mexe nela de novo

    public IntPtr Window { get; }
    public string Title { get; }

    /// <summary>A janela voltou para o PC (na thread da interface).</summary>
    public event Action? Ended;

    private PcWindowSession(AdbClient adb, string serial, int port, IntPtr window, TcpClient client,
        WindowCapture capture, WindowHider hider, Action<string> log)
    {
        _adb = adb;
        _serial = serial;
        _port = port;
        Window = window;
        Title = GetTitle(window);
        _client = client;
        _stream = client.GetStream();
        _input = new MessageChannel(_stream);
        _capture = capture;
        _hider = hider;
        _injector = new WindowInjector(window);
        _log = log;
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Precisa ser chamado na thread da interface.</summary>
    public static async Task<PcWindowSession> StartAsync(DeviceSession device, IntPtr window, Action<string> log)
    {
        var adb = device.Adb;
        var serial = device.Serial;
        await EnsureAppInstalledAsync(adb, serial, log);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WindowCapture? capture = null;
        var hider = new WindowHider(window);
        try
        {
            await adb.RunAsync(serial, ["reverse", $"tcp:{port}", $"tcp:{port}"]);
            // A captura começa antes de esconder, para já ter o primeiro quadro.
            if (IsIconic(window))
                ShowWindow(window, SW_SHOWNOACTIVATE);
            capture = new WindowCapture(window);
            await adb.RunAsync(serial,
                ["shell", "am", "start", "-n", $"{AppPackage}/.RemoteWindowActivity", "--ei", "port", port.ToString()]);
            var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            client.NoDelay = true;
            if (!hider.Hide())
                log("Esta janela não pode ficar invisível no PC; ela continua aparecendo nos dois.");

            var session = new PcWindowSession(adb, serial, port, window, client, capture, hider, log);
            session.Run();
            return session;
        }
        catch
        {
            hider.Show();
            capture?.Dispose();
            await RemoveReverseQuietly(adb, serial, port);
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void Run()
    {
        _capture.FrameArrived += () => _signal.Set();
        _capture.Closed += () => End(); // o programa fechou a janela
        _capture.Error += e => _ui.BeginInvoke(() => _log($"Erro ao capturar a janela: {e.Message}"));
        new Thread(VideoLoop) { IsBackground = true, Name = "Janela do PC → celular" }.Start();
        _ = Task.Run(InputLoop);
    }

    /// <summary>Pega o quadro mais recente, no máximo <see cref="Fps"/> por segundo, codifica e manda.</summary>
    private void VideoLoop()
    {
        H264Encoder? encoder = null;
        var frame = Array.Empty<byte>();
        var output = new BufferedStream(_stream, 1 << 16);
        var header = new byte[12];
        var clock = Stopwatch.StartNew();
        var lastFrame = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(1.0 / Fps);
        var sentHeader = false;
        try
        {
            while (!_ending)
            {
                _signal.WaitOne(1000);
                if (_ending)
                    break;
                var wait = interval - (clock.Elapsed - lastFrame);
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                if (!_capture.TryCopyLatest(ref frame, out var sourceWidth, out var sourceHeight))
                    continue;
                lastFrame = clock.Elapsed;

                var (width, height) = VideoSize(sourceWidth, sourceHeight);
                if (encoder is null || encoder.Width != width || encoder.Height != height || _keyFrameRequested)
                {
                    // Codificador novo = quadro completo + configuração (o celular começa do zero).
                    _keyFrameRequested = false;
                    var sizeChanged = encoder is not null && (encoder.Width != width || encoder.Height != height);
                    encoder?.Dispose();
                    encoder = new H264Encoder(width, height, Fps);
                    if (!sentHeader)
                    {
                        BinaryPrimitives.WriteInt32BigEndian(header, width);
                        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
                        output.Write(header, 0, 8);
                        sentHeader = true;
                    }
                    else if (sizeChanged)
                    {
                        var size = new byte[8];
                        BinaryPrimitives.WriteInt32BigEndian(size, width);
                        BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(4), height);
                        WritePacket(output, header, new VideoPacket(size, 0, false, false, IsSizeChange: true));
                    }
                }

                foreach (var packet in encoder.Encode(frame, sourceWidth, sourceHeight, (long)clock.Elapsed.TotalMicroseconds))
                    WritePacket(output, header, packet);
                output.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // O celular desconectou.
        }
        catch (Exception e)
        {
            _ui.BeginInvoke(() => _log($"Erro no vídeo da janela: {e.Message}"));
        }
        finally
        {
            encoder?.Dispose();
        }
        End();
    }

    private static void WritePacket(Stream output, byte[] header, VideoPacket packet)
    {
        var flags = (packet.IsConfig ? 1L << 63 : 0) | (packet.IsKeyFrame ? 1L << 62 : 0) | (packet.IsSizeChange ? 1L << 61 : 0);
        BinaryPrimitives.WriteInt64BigEndian(header, packet.PtsMicroseconds | flags);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), packet.Data.Length);
        output.Write(header, 0, 12);
        output.Write(packet.Data);
    }

    /// <summary>Reduz janelas muito grandes; múltiplos de 16 (o que sobra, até 15 px, é esticado).</summary>
    private static (int Width, int Height) VideoSize(int width, int height)
    {
        var scale = Math.Min(1.0, (double)MaxVideoSize / Math.Max(width, height));
        static int Align(double v) => Math.Max(16, (int)v / 16 * 16);
        return (Align(width * scale), Align(height * scale));
    }

    /// <summary>Mensagens do celular: toques/mouse ("pointer"), teclas ("key") e pedido de quadro completo.</summary>
    private async Task InputLoop()
    {
        try
        {
            while (await _input.ReceiveAsync() is { } message)
            {
                switch ((string?)message["type"])
                {
                    case "pointer":
                        _injector.Pointer((string?)message["action"] ?? "", (double?)message["x"] ?? 0,
                            (double?)message["y"] ?? 0, (double?)message["dx"] ?? 0, (double?)message["dy"] ?? 0);
                        break;
                    case "key":
                        _injector.Key((bool?)message["down"] ?? false, (int?)message["code"] ?? 0,
                            (int?)message["char"] ?? 0, (bool?)message["ctrl"] ?? false, (bool?)message["alt"] ?? false);
                        break;
                    case "keyframe":
                        _keyFrameRequested = true;
                        _signal.Set();
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException
                                      or System.Text.Json.JsonException or SocketException)
        {
        }
        End(); // o celular fechou a tela: a janela volta para o PC
    }

    /// <summary>
    /// Volta a janela para o PC embaixo do cursor (o usuário a trouxe arrastando do celular). Se o
    /// botão ainda estiver apertado, "pega" a janela pela barra de título para o arraste continuar.
    /// </summary>
    public void ReturnAtCursor(bool dragHeld)
    {
        _keepHidden = true;
        _hider.Show();
        Native.GetCursorPos(out var cursor);
        if (_injector.TryGetBounds(out var bounds) && GetWindowRect(Window, out var rect))
        {
            // Mantém a barra de título (16 px abaixo do topo visível) embaixo do cursor.
            var x = cursor.X - Math.Min(120, bounds.Width / 2) - (bounds.Left - rect.Left);
            var y = cursor.Y - 16 - (bounds.Top - rect.Top);
            SetWindowPos(Window, 0, x, y, 0, 0, 0x0001 | 0x0004); // sem mudar tamanho nem ordem
        }
        if (dragHeld)
            Native.InjectLeftButton(down: true);
        End();
    }

    /// <summary>Encerra: o celular fecha a tela e a janela volta a aparecer no PC.</summary>
    public void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        _ending = true;
        _signal.Set();
        _ui.BeginInvoke(async () =>
        {
            _capture.Dispose();
            _client.Dispose();
            if (!_keepHidden)
                _hider.Show();
            await RemoveReverseQuietly(_adb, _serial, _port);
            _log($"\"{Title}\" voltou para o PC.");
            Ended?.Invoke();
        });
    }

    private static async Task RemoveReverseQuietly(AdbClient adb, string serial, int port)
    {
        try { await adb.RunAsync(serial, ["reverse", "--remove", $"tcp:{port}"]); } catch (AdbException) { }
    }

    /// <summary>
    /// Instala (ou atualiza) o app Seamline no celular quando o .apk que veio com o PC é diferente do
    /// instalado. Guardamos um "resumo" (hash) do .apk instalado no celular para comparar.
    /// </summary>
    private static async Task EnsureAppInstalledAsync(AdbClient adb, string serial, Action<string> log)
    {
        var apk = Path.Combine(AppContext.BaseDirectory, "seamline-app.apk");
        if (!File.Exists(apk))
            throw new FileNotFoundException("seamline-app.apk não encontrado. Compile o projeto android (módulo app) antes do app do PC.", apk);
        string hash;
        using (var file = File.OpenRead(apk))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file))[..16];

        var installed = "";
        try
        {
            var path = await adb.RunAsync(serial, ["shell", "pm", "path", AppPackage]);
            if (path.Contains("package:"))
                installed = (await adb.RunAsync(serial, ["shell", $"cat {RemoteApkVersionPath} 2>/dev/null || true"])).Trim();
        }
        catch (AdbException)
        {
        }
        if (installed == hash)
            return;

        log("Instalando/atualizando o app Seamline no celular...");
        await adb.RunAsync(serial, ["install", "-r", apk]);
        await adb.RunAsync(serial, ["shell", $"echo {hash} > {RemoteApkVersionPath}"]);
    }
}
