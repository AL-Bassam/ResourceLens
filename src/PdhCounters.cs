using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// Language-independent performance counters (PdhAddEnglishCounter) for disk throughput and GPU engines.
/// </summary>
internal sealed class PdhCounters : IDisposable
{
    IntPtr _query;
    IntPtr _diskRead, _diskWrite, _gpu, _netRx, _netTx;
    bool _primed;

    public bool DiskAvailable => _diskRead != IntPtr.Zero;
    public bool GpuAvailable => _gpu != IntPtr.Zero;

    public PdhCounters(bool disk, bool gpu, bool net = false)
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (net)
        {
            if (Native.PdhAddEnglishCounterW(_query, @"\Network Interface(*)\Bytes Received/sec", IntPtr.Zero, out _netRx) != 0) _netRx = IntPtr.Zero;
            if (Native.PdhAddEnglishCounterW(_query, @"\Network Interface(*)\Bytes Sent/sec", IntPtr.Zero, out _netTx) != 0) _netTx = IntPtr.Zero;
        }
        if (disk)
        {
            if (Native.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskRead) != 0) _diskRead = IntPtr.Zero;
            if (Native.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWrite) != 0) _diskWrite = IntPtr.Zero;
        }
        if (gpu)
        {
            if (Native.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0) _gpu = IntPtr.Zero;
        }
        Native.PdhCollectQueryData(_query);
    }

    public sealed class Result
    {
        public double DiskReadBps = double.NaN, DiskWriteBps = double.NaN;
        public double GpuTotal = double.NaN;
        public Dictionary<uint, double>? GpuByPid;
        public Dictionary<string, double>? GpuEngines; // engine type (3D, Copy, VideoDecode…) -> %
        public double NetRxBps = double.NaN, NetTxBps = double.NaN;
    }

    /// <summary>Virtual/tunnel adapters would count the same traffic twice (VPN, Hyper-V, WSL).</summary>
    static bool IsVirtualAdapter(string name)
    {
        foreach (var v in VirtualMarkers)
            if (name.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    static readonly string[] VirtualMarkers =
        { "vEthernet", "Hyper-V", "Loopback", "isatap", "Teredo", "WAN Miniport", "Virtual", "TAP-", "Wintun", "WireGuard", "VPN", "Bluetooth", "Kernel Debug" };

    unsafe double SumArray(IntPtr counter, Func<string, bool> include)
    {
        uint size = 0;
        uint st = Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
        if (st != Native.PDH_MORE_DATA || size == 0) return 0;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return double.NaN;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (items[i].FmtValue.CStatus != 0) continue;
                string? name = Marshal.PtrToStringUni(items[i].szName);
                if (name is not null && include(name)) sum += Math.Max(0, items[i].FmtValue.doubleValue);
            }
            return sum;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public Result Collect()
    {
        var r = new Result();
        if (_query == IntPtr.Zero) return r;
        Native.PdhCollectQueryData(_query);
        if (!_primed) { _primed = true; }

        if (_diskRead != IntPtr.Zero && Native.PdhGetFormattedCounterValue(_diskRead, Native.PDH_FMT_DOUBLE, out _, out var v1) == 0)
            r.DiskReadBps = Math.Max(0, v1.doubleValue);
        if (_diskWrite != IntPtr.Zero && Native.PdhGetFormattedCounterValue(_diskWrite, Native.PDH_FMT_DOUBLE, out _, out var v2) == 0)
            r.DiskWriteBps = Math.Max(0, v2.doubleValue);

        if (_gpu != IntPtr.Zero) ReadGpu(r);
        if (_netRx != IntPtr.Zero) r.NetRxBps = SumArray(_netRx, n => !IsVirtualAdapter(n));
        if (_netTx != IntPtr.Zero) r.NetTxBps = SumArray(_netTx, n => !IsVirtualAdapter(n));
        return r;
    }

    unsafe void ReadGpu(Result r)
    {
        uint size = 0;
        uint st = Native.PdhGetFormattedCounterArrayW(_gpu, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (st != Native.PDH_MORE_DATA || size == 0) { r.GpuTotal = 0; r.GpuByPid = new(); return; }
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            st = Native.PdhGetFormattedCounterArrayW(_gpu, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, buf);
            if (st != 0) return;
            var list = new List<(string, double)>((int)count);
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            for (int i = 0; i < count; i++)
            {
                if (items[i].FmtValue.CStatus != 0 || items[i].FmtValue.doubleValue <= 0) continue;
                if (Marshal.PtrToStringUni(items[i].szName) is { } name) list.Add((name, items[i].FmtValue.doubleValue));
            }
            var load = GpuLoad.From(list);
            r.GpuByPid = load.ByPid;
            r.GpuTotal = load.Total;
            r.GpuEngines = load.ByType.ToDictionary(k => k.Key, v => Math.Round(v.Value, 1));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}

/// <summary>
/// GPU utilisation from the "GPU Engine" counters, which have one instance per process per engine, the way Task Manager
/// counts it: an engine's load is the sum over processes, a GPU's load is its busiest engine, and a process' GPU% is
/// the busiest engine it uses. Engines are kept apart per adapter, so an integrated and a discrete GPU never add up.
/// </summary>
internal sealed class GpuLoad
{
    public sealed class Adapter
    {
        public double Util;
        public readonly Dictionary<string, double> ByType = new(); // engine type (3D, Copy, VideoDecode…) -> busiest engine of it
    }

    /// <summary>Busiest engine of any GPU.</summary>
    public double Total;
    public readonly Dictionary<uint, double> ByPid = new();
    /// <summary>Engine type -> busiest engine of that type on any GPU.</summary>
    public readonly Dictionary<string, double> ByType = new();
    /// <summary>Adapter LUID ("0x00000000_0x0000C3B1") -> its own load.</summary>
    public readonly Dictionary<string, Adapter> ByAdapter = new();

    public static GpuLoad From(IEnumerable<(string Name, double Value)> instances)
    {
        var res = new GpuLoad();
        var engines = new Dictionary<string, (string Luid, string Type, double Sum)>();
        var pidEngine = new Dictionary<(uint, string), double>();
        foreach (var (name, v) in instances)
        {
            // idle engines stay in (the Performance page charts every engine)
            if (!Parse(name, out uint pid, out string engine, out string luid, out string type) || HardwareInfo.IsNpu(luid)) continue;
            double x = Math.Max(0, v);
            engines[engine] = engines.TryGetValue(engine, out var e) ? (luid, type, e.Sum + x) : (luid, type, x);
            if (x > 0) pidEngine[(pid, engine)] = pidEngine.GetValueOrDefault((pid, engine)) + x;
        }
        foreach (var (luid, type, sum) in engines.Values)
        {
            double u = Math.Min(100, sum);
            if (!res.ByAdapter.TryGetValue(luid, out var a)) res.ByAdapter[luid] = a = new Adapter();
            a.Util = Math.Max(a.Util, u);
            a.ByType[type] = Math.Max(a.ByType.GetValueOrDefault(type), u);
            res.ByType[type] = Math.Max(res.ByType.GetValueOrDefault(type), u);
            res.Total = Math.Max(res.Total, u);
        }
        foreach (var ((pid, _), v) in pidEngine)
            res.ByPid[pid] = Math.Max(res.ByPid.GetValueOrDefault(pid), Math.Min(100, v));
        return res;
    }

    /// <summary>"pid_1234_luid_0x00000000_0x0000C3B1_phys_0_eng_0_engtype_3D".</summary>
    internal static bool Parse(string name, out uint pid, out string engine, out string luid, out string type)
    {
        pid = 0; engine = luid = type = "";
        int li = name.IndexOf("luid_", StringComparison.Ordinal), ti = name.IndexOf("_engtype_", StringComparison.Ordinal);
        if (li < 0 || ti < li + 26) return false;
        int pi = name.IndexOf("pid_", StringComparison.Ordinal);
        if (pi >= 0 && pi < li) uint.TryParse(name.AsSpan(pi + 4, Math.Max(0, li - pi - 5)), out pid);
        engine = name[li..ti];
        luid = name.Substring(li + 5, 21);
        type = name[(ti + 9)..];
        return true;
    }
}
