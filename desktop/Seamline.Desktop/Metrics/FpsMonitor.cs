using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Seamline.Desktop.Input;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// FPS do jogo. Cada vez que um jogo termina um quadro ele chama "Present" (DirectX); o Windows
/// pode avisar isso por eventos (ETW), o mesmo jeito que o PresentMon, o CapFrameX e outros usam.
/// Contamos os avisos por programa. Abrir essa escuta exige administrador.
///
/// Conta jogos em DirectX 9, 10, 11 e 12 (a grande maioria). Vulkan e OpenGL puros não passam por aí.
///
/// O jogo é o programa em primeiro plano que está desenhando quadros; se você trocar para outra
/// janela (como o próprio painel), continua mostrando o último jogo enquanto ele desenhar.
/// </summary>
internal sealed class FpsMonitor : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"); // Microsoft-Windows-DXGI
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9"); // Microsoft-Windows-D3D9
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const double HistoryMs = 10_000; // o "1% low" olha os últimos 10 s

    private readonly TraceEventSession? _session;
    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<double>> _presents = []; // processo → horários (ms) dos quadros
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _gamePid;
    private string _gameName = "";

    /// <summary>"ok", "admin" (abrir como administrador) ou "error".</summary>
    public string Status { get; }

    public FpsMonitor()
    {
        if (!Elevation.IsAdmin)
        {
            Status = "admin";
            return;
        }
        try
        {
            // Se o Seamline fechou de mau jeito, a sessão antiga com o mesmo nome é substituída.
            _session = new TraceEventSession("Seamline-FPS") { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = [DxgiPresentStart] });
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = [D3D9PresentStart] });
            _session.Source.AllEvents += OnEvent;
            new Thread(() =>
            {
                try { _session.Source.Process(); } catch (Exception) { }
            }) { IsBackground = true, Name = "FPS (ETW)" }.Start();
            Status = "ok";
        }
        catch (Exception)
        {
            _session?.Dispose();
            _session = null;
            Status = "error";
        }
    }

    private void OnEvent(TraceEvent data)
    {
        var id = (int)data.ID;
        if (!(data.ProviderGuid == DxgiProvider && id == DxgiPresentStart) && !(data.ProviderGuid == D3D9Provider && id == D3D9PresentStart))
            return;
        var pid = data.ProcessID;
        if (pid == Environment.ProcessId)
            return;
        var now = _clock.Elapsed.TotalMilliseconds;
        lock (_lock)
        {
            if (!_presents.TryGetValue(pid, out var times))
                _presents[pid] = times = new Queue<double>();
            times.Enqueue(now);
        }
    }

    /// <summary>FPS do último segundo, tempo médio do quadro (ms), "1% low" e o nome do jogo.</summary>
    public (double Fps, double FrameMs, double Low1, string Game)? Read()
    {
        if (_session is null)
            return null;
        var now = _clock.Elapsed.TotalMilliseconds;
        var foreground = 0;
        var window = WindowNative.GetForegroundWindow();
        if (window != 0)
        {
            WindowNative.GetWindowThreadProcessId(window, out var process);
            foreground = (int)process;
        }

        double[] times;
        lock (_lock)
        {
            foreach (var (pid, queue) in _presents.ToList())
            {
                while (queue.Count > 0 && queue.Peek() < now - HistoryMs)
                    queue.Dequeue();
                if (queue.Count == 0)
                    _presents.Remove(pid);
            }
            bool Active(int pid) => _presents.TryGetValue(pid, out var q) && q.Count > 0 && q.Last() > now - 1500;
            if (foreground != 0 && foreground != Environment.ProcessId && Active(foreground) && foreground != _gamePid)
            {
                _gamePid = foreground;
                _gameName = GameName(window, foreground);
            }
            if (!Active(_gamePid))
                return (0, 0, 0, "");
            times = _presents[_gamePid].ToArray();
        }

        var lastSecond = times.Where(t => t >= now - 1000).ToArray();
        var fps = lastSecond.Length;
        var frameMs = lastSecond.Length >= 2 ? (lastSecond[^1] - lastSecond[0]) / (lastSecond.Length - 1) : 0;
        // 1% low: a média dos 1% quadros mais lentos dos últimos 10 s, em FPS.
        var intervals = new double[Math.Max(0, times.Length - 1)];
        for (var i = 1; i < times.Length; i++)
            intervals[i - 1] = times[i] - times[i - 1];
        Array.Sort(intervals);
        var worst = intervals.Length == 0 ? 0 : intervals[^Math.Max(1, intervals.Length / 100)..].Average();
        var low1 = worst > 0 ? Math.Min(fps, 1000 / worst) : 0;
        return (fps, frameMs, low1, _gameName);
    }

    private static string GameName(IntPtr window, int pid)
    {
        var title = WindowNative.GetTitle(window);
        if (title.Length > 0)
            return title;
        try { return Process.GetProcessById(pid).ProcessName; } catch (Exception) { return ""; }
    }

    public void Dispose() => _session?.Dispose();
}
