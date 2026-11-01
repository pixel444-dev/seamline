using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// Temperatura do processador. O Windows não informa isso a programas comuns: é preciso ler
/// registradores do processador, o que só um driver consegue. Usamos a biblioteca do
/// LibreHardwareMonitor (licença MPL 2.0), que fala com o driver PawnIO (gratuito, assinado).
/// Precisa: Seamline aberto como administrador + PawnIO instalado.
/// </summary>
internal sealed class CpuTemperature : IDisposable
{
    private readonly Computer? _computer;
    private readonly IHardware? _cpu;

    /// <summary>"ok", "admin" (abrir como administrador), "pawnio" (instalar o PawnIO) ou "none" (sem sensor).</summary>
    public string Status { get; }

    public CpuTemperature()
    {
        if (!Elevation.IsAdmin)
        {
            Status = "admin";
            return;
        }
        if (!PawnIoInstalled())
        {
            Status = "pawnio";
            return;
        }
        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            Status = _cpu is null ? "none" : "ok";
        }
        catch (Exception)
        {
            Status = "none";
        }
    }

    public double? Read()
    {
        if (_cpu is null)
            return null;
        _cpu.Update();
        var temps = _cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        // AMD: "Core (Tctl/Tdie)"; Intel: "CPU Package". Sem esses, o núcleo mais quente.
        var main = temps.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Tdie"))
                   ?? temps.FirstOrDefault(s => s.Name.Contains("Package"));
        return main?.Value ?? (temps.Count > 0 ? temps.Max(s => s.Value) : null);
    }

    private static bool PawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    public void Dispose() => _computer?.Close();
}
