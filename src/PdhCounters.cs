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

            // Task Manager semantics: a process' GPU% is the busiest engine type it uses;
            // the system GPU% is the busiest engine type overall.
            var perPidEngine = new Dictionary<(uint pid, string eng), double>();
            var perEngine = new Dictionary<string, double>();
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            for (int i = 0; i < count; i++)
            {
                if (items[i].FmtValue.CStatus != 0) continue;
                double val = items[i].FmtValue.doubleValue;
                if (val <= 0) continue;
                string? name = Marshal.PtrToStringUni(items[i].szName);
                if (name is null) continue;
                // pid_1234_luid_0x00000000_0x0000C3B1_phys_0_eng_0_engtype_3D
                uint pid = 0;
                int pi = name.IndexOf("pid_", StringComparison.Ordinal);
                if (pi >= 0)
                {
                    int e = name.IndexOf('_', pi + 4);
                    if (e > pi) uint.TryParse(name.AsSpan(pi + 4, e - pi - 4), out pid);
                }
                int ei = name.IndexOf("engtype_", StringComparison.Ordinal);
                string eng = ei >= 0 ? name[(ei + 8)..] : "?";
                var key = (pid, eng);
                perPidEngine[key] = perPidEngine.GetValueOrDefault(key) + val;
                perEngine[eng] = perEngine.GetValueOrDefault(eng) + val;
            }
            var byPid = new Dictionary<uint, double>();
            foreach (var (k, val) in perPidEngine)
                byPid[k.pid] = Math.Max(byPid.GetValueOrDefault(k.pid), Math.Min(100, val));
            r.GpuByPid = byPid;
            r.GpuTotal = perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());
            r.GpuEngines = perEngine.ToDictionary(k => k.Key, v => Math.Round(Math.Min(100, v.Value), 1));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
