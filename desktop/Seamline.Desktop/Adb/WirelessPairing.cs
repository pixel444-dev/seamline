using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Seamline.Desktop.Adb;

/// <summary>Um serviço que o celular anuncia na rede Wi-Fi (mDNS) e o adb encontrou.</summary>
public sealed record MdnsService(string Name, string Type, string Address)
{
    /// <summary>O celular está com a tela "Parear dispositivo" aberta (por QR ou por código).</summary>
    public bool IsPairing => Type.Contains("_adb-tls-pairing");

    /// <summary>A Depuração sem fio está ligada e aceita conexões de PCs já pareados.</summary>
    public bool IsConnect => Type.Contains("_adb-tls-connect");
}

/// <summary>
/// Pareamento pela Depuração sem fio (Android 11+), do mesmo jeito que o Android Studio faz:
/// 1. o PC mostra um QR code com um nome e uma senha inventados na hora;
/// 2. o celular lê o QR (Depuração sem fio → Parear dispositivo com código QR) e passa a anunciar
///    na rede um serviço com aquele nome;
/// 3. o PC acha o serviço pelo mDNS e roda "adb pair" com a senha;
/// 4. depois de pareado, o celular anuncia o serviço de conexão e o adb conecta.
/// O pareamento fica guardado no celular: das próximas vezes basta a Depuração sem fio estar ligada.
/// </summary>
public static partial class WirelessPairing
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Cria um nome e uma senha novos e o texto do QR code no formato que o Android entende.</summary>
    public static (string Name, string Password, string QrText) CreateQrPairing()
    {
        var name = "seamline-" + RandomString(6);
        var password = RandomString(10);
        // Mesmo formato do Android Studio. Só usamos letras e números, que não precisam de escape.
        return (name, password, $"WIFI:T:ADB;S:{name};P:{password};;");
    }

    public static async Task<IReadOnlyList<MdnsService>> GetServicesAsync(this AdbClient adb, CancellationToken ct = default)
    {
        var output = await adb.RunAsync(["mdns", "services"], ct);
        var services = new List<MdnsService>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Cada linha: nome, tipo e endereço (IP:porta), separados por espaços ou tabs.
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].StartsWith("_adb"))
                continue;
            services.Add(new MdnsService(parts[0], parts[1], parts[^1]));
        }
        return services;
    }

    /// <summary>Roda "adb pair". Devolve o nome do celular (o mesmo do serviço de conexão dele).</summary>
    public static async Task<string?> PairAsync(this AdbClient adb, string address, string password, CancellationToken ct = default)
    {
        string output;
        try
        {
            output = await adb.RunAsync(["pair", address, password], ct);
        }
        catch (AdbException ex)
        {
            throw new AdbException($"O pareamento falhou: {ex.Message}");
        }
        // Sucesso: "Successfully paired to 192.168.0.10:37145 [guid=adb-SERIAL123-AbCdEf]"
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new AdbException($"O pareamento falhou: {output.Trim()}");
        var guid = GuidPattern().Match(output);
        return guid.Success ? guid.Groups[1].Value : null;
    }

    /// <summary>Roda "adb connect". Devolve true se conectou.</summary>
    public static async Task<bool> ConnectAsync(this AdbClient adb, string address, CancellationToken ct = default)
    {
        try
        {
            var output = await adb.RunAsync(["connect", address], ct);
            return output.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);
        }
        catch (AdbException)
        {
            return false;
        }
    }

    /// <summary>
    /// Conecta nos celulares já pareados que estão com a Depuração sem fio ligada e ainda não aparecem
    /// no adb. Normalmente o próprio adb já faz isso sozinho; aqui é só uma garantia.
    /// </summary>
    public static async Task ConnectKnownDevicesAsync(this AdbClient adb, IReadOnlyList<AdbDevice> devices, CancellationToken ct = default)
    {
        foreach (var service in await adb.GetServicesAsync(ct))
        {
            if (!service.IsConnect || devices.Any(d => d.Serial.StartsWith(service.Name) || d.Serial == service.Address))
                continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await adb.ConnectAsync(service.Address, timeout.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    private static string RandomString(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });

    [GeneratedRegex(@"guid=([^\]\s]+)")]
    private static partial Regex GuidPattern();
}
