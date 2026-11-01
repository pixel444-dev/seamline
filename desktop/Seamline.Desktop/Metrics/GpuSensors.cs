using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// Placa de vídeo, sem precisar de administrador:
/// - uso (%) e memória de vídeo usada: contadores "GPU Engine" e "GPU Adapter Memory" (os do Gerenciador de Tarefas);
/// - temperatura, ventoinha e consumo: o próprio driver informa ao Windows (D3DKMT, também do Gerenciador
///   de Tarefas). Funciona em placas dedicadas com driver WDDM 2.4+; nas integradas costuma não ter.
///
/// Com mais de uma placa (ex.: integrada + dedicada), usa a que tem mais memória de vídeo própria.
/// </summary>
internal sealed class GpuSensors : IDisposable
{
    private readonly PdhCounter? _engines;
    private readonly PdhCounter? _memory;
    private readonly string _luidTag; // como a placa aparece no nome das instâncias: "luid_0x00000000_0x00011E8D"
    private readonly uint _adapter;   // identificador do D3DKMT (0 se não abriu)

    public string Name { get; } = "GPU";
    public double VramTotalMb { get; }

    public GpuSensors(PdhQuery query)
    {
        uint luidLow = 0;
        var luidHigh = 0;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            ulong best = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0 || desc.DedicatedVideoMemory <= best)
                        continue;
                    best = desc.DedicatedVideoMemory;
                    Name = desc.Description.Trim();
                    VramTotalMb = desc.DedicatedVideoMemory / 1048576.0;
                    luidLow = desc.Luid.LowPart;
                    luidHigh = desc.Luid.HighPart;
                }
            }
        }
        catch (Exception)
        {
            // Sem DXGI: fica só com os contadores somados de todas as placas.
        }
        _luidTag = $"luid_0x{luidHigh:X8}_0x{luidLow:X8}";

        try { _engines = query.Add(@"\GPU Engine(*)\Utilization Percentage"); } catch (InvalidOperationException) { }
        try { _memory = query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage"); } catch (InvalidOperationException) { }

        var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = luidLow, LuidHigh = luidHigh };
        if ((luidLow != 0 || luidHigh != 0) && D3DKMTOpenAdapterFromLuid(ref open) == 0)
            _adapter = open.hAdapter;
    }

    /// <summary>
    /// Uso da placa, como no Gerenciador de Tarefas: soma o uso de cada "motor" (3D, cópia, vídeo...)
    /// por todos os programas e mostra o motor mais ocupado.
    /// </summary>
    public double? Usage()
    {
        if (_engines is null)
            return null;
        var perEngine = new Dictionary<string, double>();
        foreach (var (instance, value) in _engines.Values())
        {
            // "pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D" → motor "luid_..._phys_0_eng_3"
            var luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            var type = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0 || type < luid)
                continue;
            var engine = instance[luid..type];
            if (_adapter != 0 && !engine.StartsWith(_luidTag, StringComparison.OrdinalIgnoreCase))
                continue;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
        }
        return perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());
    }

    public double? VramUsedMb()
    {
        if (_memory is null)
            return null;
        var values = _memory.Values();
        var mine = values.Where(v => v.Instance.StartsWith(_luidTag, StringComparison.OrdinalIgnoreCase)).ToList();
        var bytes = (mine.Count > 0 ? mine : values).Sum(v => v.Value);
        return bytes / 1048576.0;
    }

    /// <summary>Temperatura (°C), ventoinha (RPM) e consumo (% do máximo), quando o driver informa.</summary>
    public unsafe (double? Temperature, double? FanRpm, double? PowerPercent) Sensors()
    {
        if (_adapter == 0)
            return (null, null, null);
        var perf = new D3DKMT_ADAPTER_PERFDATA();
        var query = new D3DKMT_QUERYADAPTERINFO
        {
            hAdapter = _adapter,
            Type = KMTQAITYPE_ADAPTERPERFDATA,
            pPrivateDriverData = (IntPtr)(&perf),
            PrivateDriverDataSize = (uint)sizeof(D3DKMT_ADAPTER_PERFDATA),
        };
        if (D3DKMTQueryAdapterInfo(ref query) != 0)
            return (null, null, null);
        // O driver manda décimos de grau e décimos de por cento; zero quer dizer "não informa".
        return (perf.Temperature > 0 ? perf.Temperature / 10.0 : null,
                perf.Temperature > 0 ? perf.FanRPM : null,
                perf.Power > 0 ? perf.Power / 10.0 : null);
    }

    public void Dispose()
    {
        if (_adapter != 0)
        {
            var close = new D3DKMT_CLOSEADAPTER { hAdapter = _adapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
}
