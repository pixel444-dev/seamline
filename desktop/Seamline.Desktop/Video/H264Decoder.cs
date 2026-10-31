using System.IO;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Seamline.Desktop.Video;

/// <summary>
/// Decodifica H.264 com o decodificador que já vem no Windows (Media Foundation)
/// e converte cada quadro para BGRA, o formato que o WPF desenha.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    // CODECAPI_AVLowLatencyMode: entrega cada quadro assim que ele chega, sem acumular.
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private const uint EnumSyncMft = 0x1, EnumLocalMft = 0x4, EnumSortAndFilter = 0x40;

    private readonly IMFTransform _decoder;
    private OutputStreamInfo _outputInfo;
    private int _frameHeight; // altura alocada pelo decodificador (pode ter linhas extras)
    private int _defaultStride;
    private byte[]? _pendingConfig;

    public int Width { get; }
    public int Height { get; }

    public H264Decoder(int width, int height)
    {
        Width = width;
        Height = height;
        MediaFactory.MFStartup(true).CheckError();

        var inputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using (var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder,
                   EnumSyncMft | EnumLocalMft | EnumSortAndFilter, inputInfo, null))
        {
            var activate = activates.FirstOrDefault()
                ?? throw new NotSupportedException("Nenhum decodificador H.264 encontrado no Windows.");
            _decoder = activate.ActivateObject<IMFTransform>();
        }

        try
        {
            using var attributes = _decoder.Attributes;
            attributes?.Set(LowLatencyMode, 1u);
        }
        catch (SharpGenException)
        {
            // Nem todo decodificador aceita; só fica com um pouco mais de atraso.
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height).CheckError();
            _decoder.SetInputType(0, input, 0);
        }
        SelectNv12Output();
        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// Decodifica um pacote. Se ele gerar um quadro, escreve em <paramref name="bgra"/>
    /// (Width × Height × 4 bytes) e retorna true.
    /// </summary>
    public bool Decode(VideoPacket packet, byte[] bgra)
    {
        if (packet.IsConfig)
        {
            // SPS/PPS: vão junto com o próximo quadro.
            _pendingConfig = packet.Data;
            return false;
        }

        var data = packet.Data;
        if (_pendingConfig is not null)
        {
            data = [.. _pendingConfig, .. data];
            _pendingConfig = null;
        }

        using (var sample = CreateSample(data, packet.PtsMicroseconds))
            _decoder.ProcessInput(0, sample, 0);

        var gotFrame = false;
        while (true)
        {
            var result = ReadOutput(bgra);
            if (result == OutputResult.NeedMoreInput)
                return gotFrame;
            if (result == OutputResult.Frame)
                gotFrame = true;
        }
    }

    private enum OutputResult { NeedMoreInput, StreamChanged, Frame }

    private OutputResult ReadOutput(byte[] bgra)
    {
        IMFSample? ownSample = null;
        if ((_outputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
        {
            ownSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(_outputInfo.Size);
            ownSample.AddBuffer(buffer);
        }

        var output = new OutputDataBuffer { StreamID = 0, Sample = ownSample! };
        try
        {
            var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result == ResultCode.TransformNeedMoreInput)
                return OutputResult.NeedMoreInput;
            if (result == ResultCode.TransformStreamChange)
            {
                // O decodificador descobriu o tamanho real do vídeo: escolhe o formato de saída de novo.
                SelectNv12Output();
                return OutputResult.StreamChanged;
            }
            result.CheckError();
            CopyFrame(output.Sample, bgra);
            return OutputResult.Frame;
        }
        finally
        {
            // Cuidado: a biblioteca cria um objeto C# novo para o pSample devolvido, mesmo quando ele
            // aponta para a nossa própria amostra, e sem contar uma referência a mais. Liberar os dois
            // liberaria a mesma amostra duas vezes e corromperia a memória (o Seamline fechava sozinho).
            // Só liberamos o que veio do decodificador quando é realmente uma amostra dele.
            if (output.Sample is { } returned && returned.NativePointer != (ownSample?.NativePointer ?? 0))
                returned.Dispose();
            output.Events?.Dispose();
            ownSample?.Dispose();
        }
    }

    private void SelectNv12Output()
    {
        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = _decoder.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                throw new NotSupportedException("O decodificador do Windows não oferece saída NV12.");
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;
                _decoder.SetOutputType(0, type, 0);
                var frameSize = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var frameWidth = (int)(frameSize >> 32);
                _frameHeight = (int)(frameSize & 0xFFFFFFFF);
                _defaultStride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success
                    ? (int)stride
                    : frameWidth;
                _outputInfo = _decoder.GetOutputStreamInfo(0);
                return;
            }
        }
    }

    private static IMFSample CreateSample(byte[] data, long ptsMicroseconds)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
            buffer.Unlock();
            buffer.CurrentLength = data.Length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMicroseconds * 10; // Media Foundation conta em unidades de 100 ns
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private void CopyFrame(IMFSample sample, byte[] bgra)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        nint pointer;
        int pitch;
        if (buffer2D is not null)
        {
            buffer2D.Lock2D(out pointer, out pitch);
        }
        else
        {
            buffer.Lock(out pointer, out _, out _);
            pitch = _defaultStride;
        }

        try
        {
            if (bgra.Length < Width * Height * 4)
                throw new InvalidDataException("Buffer de quadro pequeno demais.");
            ConvertNv12ToBgra(pointer, pitch, _frameHeight, bgra, Width, Height);
        }
        finally
        {
            if (buffer2D is not null)
                buffer2D.Unlock2D();
            else
                buffer.Unlock();
        }
    }

    /// <summary>
    /// NV12 = um plano Y (brilho) seguido de um plano UV (cor) com metade da resolução.
    /// Fórmula BT.601 "limited range", a mais comum nos codificadores de celular.
    /// </summary>
    private static void ConvertNv12ToBgra(nint source, int pitch, int planeHeight, byte[] destination, int width, int height)
    {
        fixed (byte* destinationPointer = destination)
        {
            var dst = (nint)destinationPointer;
            var uvPlane = source + (nint)pitch * planeHeight;
            Parallel.For(0, height, y =>
            {
                var yRow = (byte*)(source + (nint)y * pitch);
                var uvRow = (byte*)(uvPlane + (nint)(y / 2) * pitch);
                var outRow = (byte*)(dst + (nint)y * width * 4);
                for (var x = 0; x < width; x++)
                {
                    var c = 298 * (yRow[x] - 16);
                    var d = uvRow[x & ~1] - 128;
                    var e = uvRow[x | 1] - 128;
                    outRow[0] = Clamp((c + 516 * d + 128) >> 8);
                    outRow[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    outRow[2] = Clamp((c + 409 * e + 128) >> 8);
                    outRow[3] = 255;
                    outRow += 4;
                }
            });
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    public void Dispose()
    {
        try
        {
            _decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        _decoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
