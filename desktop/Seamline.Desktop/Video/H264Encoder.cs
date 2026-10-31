using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Seamline.Desktop.Video;

/// <summary>
/// Codifica imagens BGRA em H.264 com o codificador que já vem no Windows (Media Foundation),
/// para mandar uma janela do PC ao celular (Fase 5). Perfil "Baseline" (sem quadros B, menos
/// atraso), que todo celular decodifica. O primeiro quadro é sempre completo (quadro-chave): para
/// pedir outro, crie um codificador novo.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid SampleCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid TransformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");
    private static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    /// <summary>O codificador H.264 por software da Microsoft (síncrono, sempre presente).</summary>
    private static readonly Guid MicrosoftH264Encoder = new("6ca50344-051a-4ded-9779-a43305165e35");
    private const uint EnumSyncMft = 0x1, EnumLocalMft = 0x4, EnumSortAndFilter = 0x40;
    private const uint ProfileBaseline = 66;
    private const uint InterlaceProgressive = 2;

    private readonly IMFTransform _encoder;
    private readonly OutputStreamInfo _outputInfo;
    private readonly byte[] _nv12;
    private readonly int _fps;

    public int Width { get; }
    public int Height { get; }

    /// <param name="width">Largura do vídeo (par; múltiplo de 16 é o mais seguro).</param>
    public H264Encoder(int width, int height, int fps = 30, int bitRate = 6_000_000)
    {
        Width = width;
        Height = height;
        _fps = fps;
        _nv12 = new byte[width * height * 3 / 2];
        MediaFactory.MFStartup(true).CheckError();

        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using (var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                   EnumSyncMft | EnumLocalMft | EnumSortAndFilter, input, output))
        {
            // Codificadores de placa de vídeo costumam ser "assíncronos" (outro jeito de usar); o da
            // Microsoft é síncrono, como o decodificador, e dá conta de uma janela a 30 quadros/s.
            var activate = activates.FirstOrDefault(a => Has(a, TransformClsid, MicrosoftH264Encoder))
                ?? activates.FirstOrDefault(a => !IsAsync(a))
                ?? throw new NotSupportedException("Nenhum codificador H.264 encontrado no Windows.");
            _encoder = activate.ActivateObject<IMFTransform>();
        }

        try
        {
            using var attributes = _encoder.Attributes;
            attributes?.Set(LowLatencyMode, 1u);
        }
        catch (SharpGenException)
        {
        }

        var frameSize = ((ulong)width << 32) | (uint)height;
        var frameRate = ((ulong)fps << 32) | 1;
        // No codificador, o formato de saída vem antes do de entrada.
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitRate).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, frameSize).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, frameRate).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive).CheckError();
            type.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileBaseline).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1).CheckError();
            _encoder.SetOutputType(0, type, 0);
        }
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, frameSize).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, frameRate).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1).CheckError();
            _encoder.SetInputType(0, type, 0);
        }
        _outputInfo = _encoder.GetOutputStreamInfo(0);
        _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static bool Has(IMFActivate activate, Guid key, Guid value)
    {
        try
        {
            return activate.GetGUID(key) == value;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private static bool IsAsync(IMFActivate activate) =>
        activate.GetUInt32(TransformAsync, out var value).Success && value != 0;

    /// <summary>
    /// Codifica uma imagem BGRA de <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>
    /// (esticada para o tamanho do vídeo se for diferente). Retorna os pacotes prontos para mandar;
    /// a configuração (SPS/PPS) vem num pacote separado, como o celular espera.
    /// </summary>
    public List<VideoPacket> Encode(byte[] bgra, int sourceWidth, int sourceHeight, long ptsMicroseconds)
    {
        ConvertToNv12(bgra, sourceWidth, sourceHeight);
        using (var sample = CreateSample(ptsMicroseconds))
            _encoder.ProcessInput(0, sample, 0);

        var packets = new List<VideoPacket>();
        while (ReadOutput(packets))
        {
        }
        return packets;
    }

    private IMFSample CreateSample(long ptsMicroseconds)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            _nv12.CopyTo(new Span<byte>((void*)pointer, _nv12.Length));
            buffer.Unlock();
            buffer.CurrentLength = _nv12.Length;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMicroseconds * 10; // unidades de 100 ns
            sample.SampleDuration = 10_000_000 / _fps;
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Lê um pacote do codificador; false quando ele precisa de mais imagens.</summary>
    private bool ReadOutput(List<VideoPacket> packets)
    {
        IMFSample? ownSample = null;
        if ((_outputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
        {
            ownSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputInfo.Size, _nv12.Length));
            ownSample.AddBuffer(buffer);
        }

        var output = new OutputDataBuffer { StreamID = 0, Sample = ownSample! };
        try
        {
            var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result == ResultCode.TransformNeedMoreInput)
                return false;
            if (result == ResultCode.TransformStreamChange)
                return true;
            result.CheckError();

            var sample = output.Sample;
            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var pointer, out _, out var length);
            byte[] data;
            try
            {
                data = new Span<byte>((void*)pointer, length).ToArray();
            }
            finally
            {
                contiguous.Unlock();
            }
            var isKey = sample.GetUInt32(SampleCleanPoint, out var clean).Success && clean != 0;
            var pts = sample.SampleTime / 10;
            AddPackets(packets, data, pts, isKey);
            return true;
        }
        finally
        {
            // Mesmo cuidado do decodificador: não liberar duas vezes a nossa própria amostra.
            if (output.Sample is { } returned && returned.NativePointer != (ownSample?.NativePointer ?? 0))
                returned.Dispose();
            output.Events?.Dispose();
            ownSample?.Dispose();
        }
    }

    /// <summary>
    /// Separa SPS/PPS (tipos 7 e 8) num pacote de configuração, como o celular espera, e descarta os
    /// delimitadores de quadro (tipo 9), que só ocupam espaço.
    /// </summary>
    private static void AddPackets(List<VideoPacket> packets, byte[] data, long pts, bool isKey)
    {
        var config = new List<byte>();
        var frame = new List<byte>(data.Length);
        foreach (var (start, end) in SplitNals(data))
        {
            var header = start + (data[start + 2] == 1 ? 3 : 4);
            var type = header < end ? data[header] & 0x1F : 0;
            var target = type switch { 7 or 8 => config, 9 => null, _ => frame };
            target?.AddRange(data.AsSpan(start, end - start));
        }
        if (config.Count > 0)
            packets.Add(new VideoPacket([.. config], pts, IsConfig: true, IsKeyFrame: false, IsSizeChange: false));
        if (frame.Count > 0)
            packets.Add(new VideoPacket([.. frame], pts, IsConfig: false, isKey, IsSizeChange: false));
    }

    /// <summary>Início (com o código 00 00 01 ou 00 00 00 01) e fim de cada NAL do fluxo Annex B.</summary>
    private static List<(int Start, int End)> SplitNals(byte[] data)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                continue;
            if (data[i + 2] == 1)
            {
                starts.Add(i > 0 && data[i - 1] == 0 ? i - 1 : i);
                i += 2;
            }
        }
        var nals = new List<(int, int)>();
        for (var n = 0; n < starts.Count; n++)
            nals.Add((starts[n], n + 1 < starts.Count ? starts[n + 1] : data.Length));
        return nals;
    }

    /// <summary>
    /// BGRA → NV12 (plano Y + plano UV com metade da resolução), fórmula BT.601 "limited range",
    /// a mesma que o decodificador do PC usa no caminho contrário. Se o tamanho for diferente,
    /// pega o pixel mais próximo (a janela só é reduzida quando é muito grande).
    /// </summary>
    private void ConvertToNv12(byte[] bgra, int sourceWidth, int sourceHeight)
    {
        int width = Width, height = Height;
        var xs = new int[width];
        for (var x = 0; x < width; x++)
            xs[x] = (int)((long)x * sourceWidth / width) * 4;

        fixed (byte* source = bgra)
        fixed (byte* destination = _nv12)
        {
            var src = (nint)source;
            var dst = (nint)destination;
            Parallel.For(0, height / 2, row =>
            {
                var y0 = row * 2;
                var line0 = (byte*)src + (long)(y0 * sourceHeight / height) * sourceWidth * 4;
                var line1 = (byte*)src + (long)((y0 + 1) * sourceHeight / height) * sourceWidth * 4;
                var yOut0 = (byte*)dst + (long)y0 * width;
                var yOut1 = yOut0 + width;
                var uvOut = (byte*)dst + (long)width * height + (long)row * width;
                for (var x = 0; x < width; x += 2)
                {
                    var p00 = line0 + xs[x];
                    var p01 = line0 + xs[x + 1];
                    var p10 = line1 + xs[x];
                    var p11 = line1 + xs[x + 1];
                    yOut0[x] = Luma(p00);
                    yOut0[x + 1] = Luma(p01);
                    yOut1[x] = Luma(p10);
                    yOut1[x + 1] = Luma(p11);
                    var b = (p00[0] + p01[0] + p10[0] + p11[0]) >> 2;
                    var g = (p00[1] + p01[1] + p10[1] + p11[1]) >> 2;
                    var r = (p00[2] + p01[2] + p10[2] + p11[2]) >> 2;
                    uvOut[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    uvOut[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            });
        }
    }

    private static byte Luma(byte* p) => (byte)(((66 * p[2] + 129 * p[1] + 25 * p[0] + 128) >> 8) + 16);

    public void Dispose()
    {
        try
        {
            _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        _encoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
