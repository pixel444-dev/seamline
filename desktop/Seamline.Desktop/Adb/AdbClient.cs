using System.Diagnostics;
using System.IO;

namespace Seamline.Desktop.Adb;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsReady => State == "device";

    /// <summary>Conectado pela Depuração sem fio ("adb-XXX._adb-tls-connect._tcp" ou "IP:porta").</summary>
    public bool IsWireless => Serial.Contains("._adb-tls-connect") || Serial.Contains(':');

    public override string ToString() =>
        IsReady ? (IsWireless ? $"{Model}  (Wi-Fi)" : $"{Model}  (USB, {Serial})") : $"{Serial}  [{State}]";
}

/// <summary>
/// Executa o adb.exe do Android SDK. É por ele que o PC conversa com o celular
/// (por cabo USB ou pela Depuração sem fio).
/// </summary>
public sealed class AdbClient(string adbPath)
{
    public string AdbPath { get; } = adbPath;

    /// <summary>
    /// Procura o adb: variável ADB, depois o Android SDK (quem desenvolve), depois o adb que vem
    /// junto no instalador (pasta "adb" ao lado do .exe), depois o PATH.
    /// </summary>
    public static AdbClient Locate()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("ADB"),
            CombineIfSet(Environment.GetEnvironmentVariable("ANDROID_HOME"), "platform-tools", "adb.exe"),
            CombineIfSet(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
        };
        var found = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        return new AdbClient(found ?? "adb");
    }

    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        using var process = Process.Start(CreateStartInfo(args))
            ?? throw new InvalidOperationException("Não foi possível iniciar o adb.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Sem isso, um "adb pair" ou "adb connect" travado continuaria rodando escondido.
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        var output = (await stdout) + (await stderr);
        if (process.ExitCode != 0)
            throw new AdbException($"adb {string.Join(' ', args)} falhou: {output.Trim()}");
        return output;
    }

    public Task<string> RunAsync(string serial, IEnumerable<string> args, CancellationToken ct = default) =>
        RunAsync(["-s", serial, .. args], ct);

    /// <summary>Inicia um comando que fica rodando (o server no celular) e repassa cada linha de saída.</summary>
    public Process StartLongRunning(string serial, IEnumerable<string> args, Action<string> onLine)
    {
        var process = new Process { StartInfo = CreateStartInfo(["-s", serial, .. args]), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var output = await RunAsync(["devices", "-l"], ct);
        var devices = new List<AdbDevice>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices") || line.StartsWith('*'))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?["model:".Length..].Replace('_', ' ');
            devices.Add(new AdbDevice(parts[0], parts[1], model ?? parts[0]));
        }
        return devices;
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    private static string? CombineIfSet(string? root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);
}

public sealed class AdbException(string message) : Exception(message);
