using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// 1-second sampler behind the Performance page (Task Manager style). It only exists while the page is open:
/// the API disposes it after a minute without requests, so it costs nothing in the background.
/// </summary>
internal sealed unsafe class PerfMonitor : IDisposable
{
    const int HistoryLength = 60;
    IntPtr _q;
    readonly IntPtr _cores, _cpuTotal, _standby1, _standby2, _standby3, _modified, _free;
    readonly IntPtr _diskIdle, _diskRead, _diskWrite, _diskResp, _netRx, _netTx, _gpuEng;
    readonly Dictionary<string, List<double?>> _hist = new();
    readonly List<long> _times = new();
    object? _last;
    DateTime _lastAt;
    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    public PerfMonitor()
    {
        Native.PdhOpenQueryW(null, IntPtr.Zero, out _q);
        IntPtr Add(string path) => Native.PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
        _cores = Add(@"\Processor Information(*)\% Processor Utility");
        _cpuTotal = Add(@"\Processor Information(_Total)\% Processor Utility");
        _standby1 = Add(@"\Memory\Standby Cache Core Bytes");
        _standby2 = Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standby3 = Add(@"\Memory\Standby Cache Reserve Bytes");
        _modified = Add(@"\Memory\Modified Page List Bytes");
        _free = Add(@"\Memory\Free & Zero Page List Bytes");
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskResp = Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _netRx = Add(@"\Network Interface(*)\Bytes Received/sec");
        _netTx = Add(@"\Network Interface(*)\Bytes Sent/sec");
        _gpuEng = Add(@"\GPU Engine(*)\Utilization Percentage");
        Native.PdhCollectQueryData(_q);
    }

    double? One(IntPtr c)
    {
        if (c == IntPtr.Zero) return null;
        return Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) == 0 ? v.doubleValue : null;
    }

    List<(string Name, double V)> Many(IntPtr c)
    {
        var res = new List<(string, double)>();
        if (c == IntPtr.Zero) return res;
        uint size = 0;
        if (Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero) != Native.PDH_MORE_DATA) return res;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return res;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            for (int i = 0; i < count; i++)
                if (items[i].FmtValue.CStatus == 0) res.Add((Marshal.PtrToStringUni(items[i].szName) ?? "", items[i].FmtValue.doubleValue));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }

    void Push(string key, double? v)
    {
        if (!_hist.TryGetValue(key, out var l))
        {
            _hist[key] = l = new List<double?>(HistoryLength);
            for (int i = 1; i < _times.Count; i++) l.Add(null); // align late-appearing series
        }
        l.Add(v is null ? null : Math.Round(v.Value, 2));
        if (l.Count > HistoryLength) l.RemoveAt(0);
    }

    static double Cap(double v) => Math.Clamp(v, 0, 100);

    static Dictionary<TK, double> Dict<TK>(IEnumerable<(string Name, double V)> items, Func<string, TK> key) where TK : notnull
    {
        var d = new Dictionary<TK, double>();
        foreach (var (n, v) in items) d[key(n)] = v;
        return d;
    }

    readonly object _gate = new();

    public object Snapshot()
    {
        lock (_gate) return SnapshotCore();
    }

    object SnapshotCore()
    {
        LastUsed = DateTime.UtcNow;
        if (_last is not null && (DateTime.UtcNow - _lastAt).TotalMilliseconds < 800) return _last;
        Native.PdhCollectQueryData(_q);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _times.Add(now);
        if (_times.Count > HistoryLength) _times.RemoveAt(0);

        var sens = SensorHub.Latest();

        // ---- CPU ----
        var cores = Many(_cores).Where(x => !x.Name.Contains("_Total"))
            .Select(x => (Key: ParseCore(x.Name), V: Cap(x.V))).OrderBy(x => x.Key).Select(x => Math.Round(x.V, 1)).ToList();
        double cpu = Cap(One(_cpuTotal) ?? 0);
        Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        var cpuInfo = HardwareInfo.Cpu();
        Push("cpu", cpu);
        Push("cpuTemp", sens?.CpuTemp);
        Push("cpuMhz", sens?.CpuMhz);

        // ---- Memory ----
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref ms);
        long total = (long)ms.ullTotalPhys, avail = (long)ms.ullAvailPhys, used = total - avail;
        long standby = (long)((One(_standby1) ?? 0) + (One(_standby2) ?? 0) + (One(_standby3) ?? 0));
        long modified = (long)(One(_modified) ?? 0), free = (long)(One(_free) ?? 0);
        long page = (long)pi.PageSize;
        var memInfo = HardwareInfo.Memory();
        Push("mem", used * 100.0 / Math.Max(1, total));

        // ---- Disks ----
        var dInfo = HardwareInfo.Disks();
        var idle = Dict(Many(_diskIdle), DiskNo);
        var rd = Dict(Many(_diskRead), DiskNo);
        var wr = Dict(Many(_diskWrite), DiskNo);
        var resp = Dict(Many(_diskResp), DiskNo);
        var disks = new List<object>();
        foreach (var d in dInfo)
        {
            double active = idle.TryGetValue(d.Number, out var id) ? Cap(100 - id) : 0;
            double r = rd.GetValueOrDefault(d.Number), w = wr.GetValueOrDefault(d.Number);
            Push($"disk{d.Number}", active);
            Push($"disk{d.Number}r", r);
            Push($"disk{d.Number}w", w);
            disks.Add(new
            {
                d.Number, d.Model, d.Ssd, d.Bus, d.Size, d.Volumes, active = Math.Round(active, 1), read = r, write = w,
                respMs = Math.Round(resp.GetValueOrDefault(d.Number) * 1000, 2), key = $"disk{d.Number}",
            });
        }

        // ---- Network ----
        var rx = Dict(Many(_netRx), n => n);
        var tx = Dict(Many(_netTx), n => n);
        var nets = new List<object>();
        int ni = 0;
        foreach (var n in HardwareInfo.Networks())
        {
            string pdh = HardwareInfo.PdhName(n.Description);
            double? r = rx.TryGetValue(pdh, out var a) ? a : null, t = tx.TryGetValue(pdh, out var b) ? b : null;
            if (r is null && t is null && !n.Up) continue;
            string key = $"net{ni++}";
            Push(key + "r", r);
            Push(key + "t", t);
            nets.Add(new { n.Name, n.Description, n.Type, n.SpeedBps, n.IPv4, n.IPv6, rx = r, tx = t, key });
        }

        // ---- GPU (one entry per adapter, like Task Manager's GPU 0, GPU 1…) ----
        var load = GpuLoad.From(Many(_gpuEng));
        Push("gpu", load.Total);
        Push("gpuTemp", sens?.GpuTemp);
        Push("gpuMhz", sens?.GpuMhz);
        var gpuInfo = HardwareInfo.Gpus();
        var gpus = new List<object>();
        for (int i = 0; i < gpuInfo.Count; i++)
        {
            var g = gpuInfo[i];
            // Without LUIDs (old drivers) everything belongs to the only adapter there is.
            var l = g.Luid is null ? (gpuInfo.Count == 1 ? new GpuLoad.Adapter { Util = load.Total } : null) : load.ByAdapter.GetValueOrDefault(g.Luid);
            var types = g.Luid is null && gpuInfo.Count == 1 ? load.ByType : l?.ByType ?? new();
            var sg = g.Luid is null ? null : sens?.Gpus?.FirstOrDefault(x => x.Luid == g.Luid);
            // With one GPU, the system-wide readings describe it (they fill in what its own entry lacks).
            var only = gpuInfo.Count == 1 ? sens : null;
            string key = $"gpu{i}";
            double util = l?.Util ?? 0;
            double? temp = sg?.Temp ?? only?.GpuTemp;
            Push(key, util);
            Push(key + "T", temp);
            foreach (var (type, v) in types) Push($"{key}E:{type}", v);
            gpus.Add(new
            {
                key, g.Name, g.Driver, g.DriverDate, luid = g.Luid,
                util = Math.Round(util, 1),
                engines = types.ToDictionary(k => k.Key, v => Math.Round(v.Value, 1)),
                dedicatedTotal = g.DedicatedBytes, sharedTotal = g.SharedBytes,
                dedicatedUsed = sg?.DedicatedUsed ?? only?.GpuDedicatedUsed, sharedUsed = sg?.SharedUsed ?? only?.GpuSharedUsed,
                temp, hotspot = sg?.Hotspot ?? only?.GpuHotspot,
                coreMhz = sg?.CoreMhz ?? only?.GpuMhz, memMhz = sg?.MemMhz ?? only?.GpuMemMhz, power = sg?.Power ?? only?.GpuPower,
            });
        }

        _last = new
        {
            ts = now,
            times = _times.ToList(),
            history = _hist.ToDictionary(k => k.Key, v => v.Value.ToList()),
            sensors = sens,
            sensorSources = SensorHub.Sources,
            cpu = new
            {
                util = Math.Round(cpu, 1), cores, info = cpuInfo,
                processes = pi.ProcessCount, threads = pi.ThreadCount, handles = pi.HandleCount,
                uptimeMs = (long)Native.GetTickCount64(),
            },
            mem = new
            {
                total, used, avail, standby, modified, free,
                commit = (long)(ms.ullTotalPageFile - ms.ullAvailPageFile), commitLimit = (long)ms.ullTotalPageFile,
                cached = standby + modified,
                paged = (long)pi.KernelPaged * page, nonPaged = (long)pi.KernelNonpaged * page,
                info = memInfo, hardwareReserved = memInfo.InstalledBytes > total ? memInfo.InstalledBytes - total : 0,
            },
            disks,
            nets,
            gpu = new { util = Math.Round(load.Total, 1), adapters = gpus },
            isLenovo = HardwareInfo.IsLenovo,
        };
        _lastAt = DateTime.UtcNow;
        return _last;
    }

    static int ParseCore(string name)
    {
        // "0,13" -> 13 ; "1,2" -> 1000+2
        var parts = name.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int c) ? g * 1000 + c : int.MaxValue;
    }

    static int DiskNo(string name)
    {
        int sp = name.IndexOf(' ');
        return int.TryParse(sp > 0 ? name[..sp] : name, out int n) ? n : -1;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}
