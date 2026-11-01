using System.Runtime.InteropServices;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// Contadores de desempenho do Windows (os mesmos que o Gerenciador de Tarefas usa), pela PDH.
/// Os nomes são sempre em inglês (<c>PdhAddEnglishCounter</c>): no Windows em português eles
/// aparecem traduzidos, e o nome em inglês funciona em qualquer idioma.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private readonly IntPtr _query;

    public PdhQuery()
    {
        Check(PdhOpenQueryW(null, 0, out _query), "abrir a consulta");
    }

    /// <summary>Adiciona um contador. Aceita * no nome da instância (ex.: <c>\GPU Engine(*)\...</c>).</summary>
    public PdhCounter Add(string path)
    {
        Check(PdhAddEnglishCounterW(_query, path, 0, out var counter), path);
        return new PdhCounter(counter);
    }

    /// <summary>Lê todos os contadores de uma vez. Contadores de "taxa" precisam de duas leituras.</summary>
    public void Collect() => PdhCollectQueryData(_query);

    public void Dispose() => PdhCloseQuery(_query);

    private static void Check(uint status, string what)
    {
        if (status != 0)
            throw new InvalidOperationException($"Contador do Windows indisponível ({what}): 0x{status:X8}");
    }

    internal const uint PDH_FMT_DOUBLE = 0x200;
    internal const uint PDH_FMT_NOCAP100 = 0x8000;
    internal const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
}

internal sealed class PdhCounter(IntPtr handle)
{
    /// <summary>Valor de um contador sem *; null se ainda não tiver leitura válida.</summary>
    public double? Value()
    {
        var status = PdhQuery.PdhGetFormattedCounterValue(handle, PdhQuery.PDH_FMT_DOUBLE | PdhQuery.PDH_FMT_NOCAP100, out _, out var value);
        return status == 0 && value.CStatus <= 1 ? value.DoubleValue : null;
    }

    /// <summary>Valores de um contador com * (nome da instância → valor).</summary>
    public List<(string Instance, double Value)> Values()
    {
        var result = new List<(string, double)>();
        uint size = 0;
        const uint format = PdhQuery.PDH_FMT_DOUBLE | PdhQuery.PDH_FMT_NOCAP100;
        if (PdhQuery.PdhGetFormattedCounterArrayW(handle, format, ref size, out _, 0) != PdhQuery.PDH_MORE_DATA || size == 0)
            return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhQuery.PdhGetFormattedCounterArrayW(handle, format, ref size, out var count, buffer) != 0)
                return result;
            var itemSize = Marshal.SizeOf<PdhQuery.PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhQuery.PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus <= 1)
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }
}
