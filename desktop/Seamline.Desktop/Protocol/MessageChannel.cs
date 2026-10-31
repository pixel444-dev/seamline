using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Seamline.Desktop.Protocol;

/// <summary>
/// Canal de controle: cada mensagem é um JSON precedido de 4 bytes com o tamanho (big-endian).
/// Veja docs/protocolo.md.
/// </summary>
public sealed class MessageChannel(Stream stream) : IDisposable
{
    private const int MaxMessageSize = 16 * 1024 * 1024;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendAsync(JsonObject message, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Lê a próxima mensagem. Retorna null quando o outro lado fecha a conexão.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxMessageSize)
            throw new InvalidDataException($"Tamanho de mensagem inválido: {length}");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidDataException("Mensagem vazia.");
    }

    public void Dispose()
    {
        stream.Dispose();
        _sendLock.Dispose();
    }
}
