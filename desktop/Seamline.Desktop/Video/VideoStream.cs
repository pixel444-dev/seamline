using System.Buffers.Binary;
using System.IO;

namespace Seamline.Desktop.Video;

public sealed record VideoPacket(byte[] Data, long PtsMicroseconds, bool IsConfig, bool IsKeyFrame, bool IsSizeChange)
{
    /// <summary>Num pacote de "novo tamanho", a largura e a altura que o vídeo passa a ter.</summary>
    public (int Width, int Height) NewSize =>
        (BinaryPrimitives.ReadInt32BigEndian(Data), BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(4)));
}

/// <summary>
/// Lê o fluxo de vídeo que o server manda (veja docs/protocolo.md):
/// cabeçalho com largura e altura, depois pacotes H.264 com 12 bytes de cabeçalho cada.
/// Um pacote de "novo tamanho" avisa que o vídeo mudou de tamanho (ex.: a janela foi redimensionada).
/// As leituras são bloqueantes: use numa thread própria.
/// </summary>
public sealed class VideoStream(Stream stream)
{
    private const long FlagConfig = 1L << 63;
    private const long FlagKeyFrame = 1L << 62;
    private const long FlagSize = 1L << 61;
    private const long AllFlags = FlagConfig | FlagKeyFrame | FlagSize;
    private const int MaxPacketSize = 32 * 1024 * 1024;

    private readonly byte[] _header = new byte[12];

    public (int Width, int Height) ReadHeader()
    {
        stream.ReadExactly(_header, 0, 8);
        return (BinaryPrimitives.ReadInt32BigEndian(_header), BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(4)));
    }

    /// <summary>Próximo pacote, ou null quando o celular fecha a conexão.</summary>
    public VideoPacket? ReadPacket()
    {
        try
        {
            stream.ReadExactly(_header);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var ptsAndFlags = BinaryPrimitives.ReadInt64BigEndian(_header);
        var size = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(8));
        if (size < 0 || size > MaxPacketSize)
            throw new InvalidDataException($"Tamanho de pacote de vídeo inválido: {size}");

        var data = new byte[size];
        stream.ReadExactly(data);
        var isSizeChange = (ptsAndFlags & FlagSize) != 0;
        if (isSizeChange && size != 8)
            throw new InvalidDataException("Pacote de novo tamanho inválido.");
        return new VideoPacket(
            data,
            ptsAndFlags & ~AllFlags,
            (ptsAndFlags & FlagConfig) != 0,
            (ptsAndFlags & FlagKeyFrame) != 0,
            isSizeChange);
    }
}
