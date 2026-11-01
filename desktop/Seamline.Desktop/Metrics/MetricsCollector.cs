using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// Uma leitura de todas as métricas do PC. null = não disponível neste PC (ou sem permissão).
/// Memórias em MB, temperaturas em °C, relógio em MHz.
/// </summary>
public sealed record PcMetrics(
    string CpuName, double CpuUsage, double? CpuClockMhz, double? CpuTemp, string CpuTempStatus,
    string GpuName, double? GpuUsage, double? GpuTemp, double? GpuFanRpm, double? GpuPower,
    double? VramUsedMb, double VramTotalMb,
    double RamUsedMb, double RamTotalMb,
    double? Fps, double? FrameMs, double? FpsLow1, string Game, string FpsStatus,
    bool IsAdmin)
{
    /// <summary>Mensagem "metrics" mandada ao celular (veja docs/protocolo.md).</summary>
    public JsonObject ToJson() => new()
    {
        ["type"] = "metrics",
        ["cpu_name"] = CpuName,
        ["cpu_usage"] = Round(CpuUsage),
        ["cpu_clock"] = Round(CpuClockMhz),
        ["cpu_temp"] = Round(CpuTemp),
        ["cpu_temp_status"] = CpuTempStatus,
        ["gpu_name"] = GpuName,
        ["gpu_usage"] = Round(GpuUsage),
        ["gpu_temp"] = Round(GpuTemp),
        ["gpu_fan"] = Round(GpuFanRpm),
        ["gpu_power"] = Round(GpuPower),
        ["vram_used"] = Round(VramUsedMb),
        ["vram_total"] = Round(VramTotalMb),
        ["ram_used"] = Round(RamUsedMb),
        ["ram_total"] = Round(RamTotalMb),
        ["fps"] = Round(Fps),
        ["frame_ms"] = Round(FrameMs, 2),
        ["fps_low"] = Round(FpsLow1),
        ["game"] = Game,
        ["fps_status"] = FpsStatus,
        ["admin"] = IsAdmin,
    };

    private static double? Round(double? value, int digits = 1) => value is { } v ? Math.Round(v, digits) : null;
}

/// <summary>
/// Lê as métricas do PC uma vez por segundo, numa thread própria, enquanto alguém estiver olhando
/// (a janela do painel e/ou o celular). Sem ninguém olhando, para tudo: não gasta nada durante o jogo.
/// </summary>
public sealed class MetricsCollector
{
    public static MetricsCollector Shared { get; } = new();

    private readonly object _lock = new();
    private readonly List<Action<PcMetrics>> _listeners = [];
    private CancellationTokenSource? _stop; // um por thread de leitura

    /// <summary>Começa a receber leituras (numa thread qualquer). Dispose para parar.</summary>
    public IDisposable Subscribe(Action<PcMetrics> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
            if (_stop is null)
            {
                var stop = _stop = new CancellationTokenSource();
                new Thread(() => Loop(stop.Token)) { IsBackground = true, Name = "Métricas do PC" }.Start();
            }
        }
        return new Subscription(this, listener);
    }

    private void Unsubscribe(Action<PcMetrics> listener)
    {
        lock (_lock)
        {
            _listeners.Remove(listener);
            if (_listeners.Count == 0 && _stop is not null)
            {
                _stop.Cancel();
                _stop = null;
            }
        }
    }

    private sealed class Subscription(MetricsCollector owner, Action<PcMetrics> listener) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(listener);
        }
    }

    private void Loop(CancellationToken stop)
    {
        using var query = new PdhQuery();
        PdhCounter? perf = null, nominal = null;
        try
        {
            // Relógio atual = relógio nominal × "% de desempenho" (passa de 100% no turbo).
            perf = query.Add(@"\Processor Information(_Total)\% Processor Performance");
            nominal = query.Add(@"\Processor Information(_Total)\Processor Frequency");
        }
        catch (InvalidOperationException) { }
        using var gpu = new GpuSensors(query);
        using var cpuTemp = new CpuTemperature();
        using var fps = new FpsMonitor();
        var cpuName = CpuName();
        GetSystemTimes(out var lastIdle, out var lastKernel, out var lastUser);
        query.Collect();

        while (!stop.WaitHandle.WaitOne(1000))
        {
            try
            {
                query.Collect();

                GetSystemTimes(out var idle, out var kernel, out var user);
                var total = (kernel - lastKernel) + (user - lastUser); // o tempo "kernel" já inclui o ocioso
                var cpuUsage = total > 0 ? Math.Clamp(100.0 * (total - (idle - lastIdle)) / total, 0, 100) : 0;
                (lastIdle, lastKernel, lastUser) = (idle, kernel, user);
                double? clock = perf?.Value() is { } p && nominal?.Value() is { } n && n > 0 ? n * p / 100 : null;

                var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                GlobalMemoryStatusEx(ref memory);
                var ramTotal = memory.ullTotalPhys / 1048576.0;
                var ramUsed = ramTotal - memory.ullAvailPhys / 1048576.0;

                var (gpuTemp, fan, power) = gpu.Sensors();
                var frame = fps.Read();

                var metrics = new PcMetrics(
                    cpuName, cpuUsage, clock, cpuTemp.Read(), cpuTemp.Status,
                    gpu.Name, gpu.Usage(), gpuTemp, fan, power, gpu.VramUsedMb(), gpu.VramTotalMb,
                    ramUsed, ramTotal,
                    frame?.Fps, frame?.FrameMs, frame?.Low1, frame?.Game ?? "", fps.Status,
                    Elevation.IsAdmin);

                Action<PcMetrics>[] listeners;
                lock (_lock)
                    listeners = [.. _listeners];
                foreach (var listener in listeners)
                    listener(metrics);
            }
            catch (Exception)
            {
                // Uma leitura que falhou não derruba o painel: tenta de novo no próximo segundo.
            }
        }
    }

    private static string CpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
