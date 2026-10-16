using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ResourceLens;

public sealed class LiveInstance
{
    public uint Pid { get; set; }
    public string? Role { get; set; }
    public string? Detail { get; set; }
    public string? Title { get; set; }
    public double Cpu { get; set; }
    public double? Gpu { get; set; }
    public long Mem { get; set; }
    public double IoR { get; set; }
    public double IoW { get; set; }
    public double? NetRx { get; set; }
    public double? NetTx { get; set; }
    public int Threads { get; set; }
    public int Handles { get; set; }
    public int Priority { get; set; }
    public uint ParentPid { get; set; }
}

public sealed class LiveProcess
{
    public long Nid { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public uint Pid { get; set; }
    public double Cpu { get; set; }
    public double? Gpu { get; set; }
    public long Mem { get; set; }
    public long Ws { get; set; }
    public long Commit { get; set; }
    public double IoR { get; set; }
    public double IoW { get; set; }
    public double? NetRx { get; set; }
    public double? NetTx { get; set; }
    public int Handles { get; set; }
    public int Threads { get; set; }
    public List<LiveInstance>? Instances { get; set; }
}

public sealed class LiveForeground
{
    public long Nid { get; set; }
    public string Name { get; set; } = "";
    public uint Pid { get; set; }
    public string? Role { get; set; }
    public string? Detail { get; set; }
    public string? Title { get; set; }
}

public sealed class LiveSnapshot
{
    public long Ts { get; set; }
    public long Boot { get; set; }
    public double IntervalSec { get; set; }
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    public long MemUsed { get; set; }
    public long MemTotal { get; set; }
    public long CommitUsed { get; set; }
    public long CommitLimit { get; set; }
    public double? DiskR { get; set; }
    public double? DiskW { get; set; }
    public double? NetRx { get; set; }
    public double? NetTx { get; set; }
    public int ProcessCount { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public bool PerProcessNetwork { get; set; }
    public string? NetworkNote { get; set; }
    public LiveForeground? Foreground { get; set; }
    /// <summary>ResourceLens' own footprint (so the user can see it stays light).</summary>
    public double SelfCpu { get; set; }
    public long SelfMem { get; set; }
    public SensorReading? Sensors { get; set; }
    public Dictionary<string, double>? GpuEngines { get; set; }
    public List<LiveProcess> Processes { get; set; } = new();
}

/// <summary>
/// Background sampler: takes a snapshot every N seconds, stores it and publishes a live view.
/// Designed to stay out of the way of games/streaming: one kernel call for all processes,
/// language-independent PDH counters, no per-sample allocations beyond small lists.
/// </summary>
public sealed class Collector : IDisposable
{
    readonly Store _store;
    readonly Func<AppSettings> _settings;
    readonly Thread _thread;
    readonly ManualResetEventSlim _wake = new(false);
    volatile bool _stop;
    volatile bool _resetCaches;

    public LiveSnapshot? Latest { get; private set; }
    public event Action<LiveSnapshot>? Updated;
    public event Action<string, string>? Alert; // title, body
    /// <summary>Informational balloons from the add-on modules (no "high usage" framing).</summary>
    public event Action<string, string>? Notice; // title, body
    public long BootTime { get; private set; }
    public string? NetworkStatus { get; private set; }

    /// <summary>True while the dashboard is open: build the full per-process live view.</summary>
    public volatile bool WantDetail;

    public Collector(Store store, Func<AppSettings> settings)
    {
        _store = store;
        _settings = settings;
        _store.Cleared += () => _resetCaches = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Collector", Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        // Add-on modules (network guard, process rules, performance counters) hook into every stored sample.
        Features.Start(new FeatureHost(_store, _settings, (title, body) => Notice?.Invoke(title, body)));
        _thread.Start();
    }

    /// <summary>Apply new settings now (interval / metric toggles).</summary>
    public void Poke() => _wake.Set();

    // ---------------- state ----------------

    record struct PrevProc(long Cpu, long Read, long Write);

    sealed class InstState
    {
        public long Iid;            // 0 until first stored
        public long Nid;
        public required string Role;
        public string? Detail;
        public string? Cmd;
        public string? LastTitle;
        public long CreatedMs;
    }

    Dictionary<(uint, long), PrevProc> _prev = new();
    Dictionary<(uint, long), InstState> _inst = new();
    readonly ServiceMap _services = new();
    long _prevIdle, _prevKernel, _prevUser;
    long _lastTick;
    PdhCounters? _pdh;
    (bool disk, bool gpu, bool net) _pdhFor;
    NetworkEtw? _etw;
    bool? _etwWanted;
    DateTime? _tempHighSince;
    bool _tempAlerted;

    /// <summary>Which sensor sources answered (for the UI / diagnostics).</summary>
    public string? SensorSources => SensorHub.Sources;

    // Where the sampling loop is and since when: the tray app's watchdog reads this to catch a stuck recorder.
    volatile string _stage = "start";
    long _stageAt = Environment.TickCount64;

    void Stage(string name)
    {
        _stage = name;
        Volatile.Write(ref _stageAt, Environment.TickCount64);
    }

    /// <summary>How long the loop has been in its current step; waiting between samples is never a stall.</summary>
    public (string Stage, long Ms) Progress => _stage == "wait" ? ("wait", 0) : (_stage, Environment.TickCount64 - Volatile.Read(ref _stageAt));
    readonly int _cpuCount = Environment.ProcessorCount;
    DateTime _nextMaintenance = DateTime.UtcNow.AddMinutes(2);
    DateTime? _memHighSince, _cpuHighSince;
    bool _memAlerted, _cpuAlerted;
    readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

    void Run()
    {
        Stage("boot time");
        BootTime = ResolveBootTime();
        using var scanner = new ProcessScanner();
        bool primed = false;

        while (!_stop)
        {
            var s = _settings();
            try
            {
                if (_resetCaches) { _resetCaches = false; _inst.Clear(); }
                Stage("sources");
                EnsureSources(s);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long dt = now - _lastTick;
                bool gap = !primed || dt <= 0 || dt > Math.Max(3 * s.IntervalSeconds * 1000L, 15_000);
                Sample(s, scanner, now, primed && !gap ? dt : 0);
                _lastTick = now;
                primed = true;

                if (DateTime.UtcNow >= _nextMaintenance)
                {
                    _nextMaintenance = DateTime.UtcNow.AddHours(1);
                    Stage("maintenance");
                    try
                    {
                        _store.TouchInstances(_inst.Values.Where(i => i.Iid > 0).Select(i => i.Iid).ToList(), now);
                        _store.Maintain(s.RetentionDays, s.FullResolutionDays);
                        Features.OnMaintain(now - s.RetentionDays * 86_400_000L);
                        Stage("health check");
                        _store.CheckHealth();
                    }
                    catch (Exception ex) { Log.Write("maintenance", ex); Store.Note(ex); }
                }
            }
            catch (Exception ex) { Log.Write("sample", ex); Store.Note(ex); }

            // Align to wall-clock multiples of the interval for tidy timestamps.
            long intervalMs = s.IntervalSeconds * 1000L;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long wait = intervalMs - nowMs % intervalMs;
            if (wait < 200) wait += intervalMs;
            Stage("wait");
            _wake.Wait((int)wait);
            _wake.Reset();
        }
        _pdh?.Dispose();
        _etw?.Dispose();
    }

    void EnsureSources(AppSettings s)
    {
        SensorHub.Enabled = s.TrackSensors;

        var want = (s.TrackDisk, s.TrackGpu, s.TrackNetwork);
        if (_pdh is null || want != _pdhFor)
        {
            _pdh?.Dispose();
            _pdh = new PdhCounters(want.TrackDisk, want.TrackGpu, want.TrackNetwork);
            _pdhFor = want;
        }
        bool etwWanted = s.TrackNetwork && s.TrackProcesses && s.PerProcessNetwork;
        if (_etwWanted != etwWanted)
        {
            _etwWanted = etwWanted;
            _etw?.Dispose();
            _etw = null;
            if (etwWanted)
            {
                _etw = NetworkEtw.TryStart();
                NetworkStatus = _etw.IsRunning ? null : (Native.IsUserAnAdmin() ? _etw.Error : "needs-admin");
                if (!_etw.IsRunning) { _etw.Dispose(); _etw = null; }
            }
            else NetworkStatus = "disabled";
        }
    }

    long ResolveBootTime()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long boot = now - (long)Native.GetTickCount64();
        // With Fast Startup the tick count survives "shutdown"; the Kernel-Boot event marks every real power-on.
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var ev = reader.ReadEvent();
            if (ev?.TimeCreated is DateTime t)
            {
                long evMs = new DateTimeOffset(t).ToUnixTimeMilliseconds();
                if (evMs > boot && evMs <= now) boot = evMs;
            }
        }
        catch { }
        // Reuse the stored id when this is the same boot (avoid jitter splitting one session).
        try
        {
            using var c = _store.OpenReader();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT boot FROM sys ORDER BY ts DESC LIMIT 1";
            if (cmd.ExecuteScalar() is long last && Math.Abs(last - boot) < 120_000) return last;
        }
        catch { }
        return boot / 1000 * 1000;
    }

    /// <summary>One process (PID) in this snapshot.</summary>
    sealed class P
    {
        public uint Pid;
        public long Create;
        public required string Name;
        public double Cpu, Gpu;
        public bool HasGpu;
        public long Mem, Ws, Commit, IoR, IoW, NetRx, NetTx;
        public int Handles, Threads, Priority;
        public uint ParentPid;
        public Agg App = null!;
        public InstState? Inst;
    }

    sealed class Agg
    {
        public string Name = "";
        public uint Pid;
        public long Nid;
        public int Count, Handles, Threads;
        public long Mem, Ws, Commit, IoR, IoW, NetRx, NetTx;
        public double Cpu, Gpu;
        public bool HasGpu;
        public List<P> Members = new(2);
    }

    static long FileTimeToUnixMs(long ft) => ft <= 0 ? 0 : (ft - 116444736000000000L) / 10_000;

    void Sample(AppSettings s, ProcessScanner scanner, long now, long dt)
    {
        bool store = dt > 0;
        double dtSec = dt / 1000.0;

        // ---- system CPU ----
        double? cpu = null;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _prevIdle, dk = kernel - _prevKernel, du = user - _prevUser;
            if (store && dk + du > 0) cpu = Math.Clamp((dk + du - di) * 100.0 / (dk + du), 0, 100);
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        }

        // ---- memory (commit = page-file-backed total, from the same call) ----
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref ms);
        long memTotal = (long)ms.ullTotalPhys, memUsed = (long)(ms.ullTotalPhys - ms.ullAvailPhys);
        long commitLimit = (long)ms.ullTotalPageFile, commitUsed = (long)(ms.ullTotalPageFile - ms.ullAvailPageFile);

        // ---- disk / gpu / network (PDH, one collection) ----
        Stage("pdh");
#if DEBUG
        if (store && Environment.GetEnvironmentVariable("RESOURCELENS_TEST_STALL") == "collector") { Stage("test stall"); Thread.Sleep(Timeout.Infinite); }
#endif
        var pdh = _pdh!.Collect();

        // ---- temperatures / clocks / power (own thread: a stuck driver only costs this sample its sensors) ----
        Stage("sensors");
        var sens = store ? SensorHub.Fresh(1000, Math.Max(2000, s.IntervalSeconds * 2000)) : null;

        // ---- processes ----
        Stage("processes");
        var raw = scanner.Scan();
        var etw = _etw?.TakeDeltas();
        var next = new Dictionary<(uint, long), PrevProc>(raw.Count);
        var byName = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var procs = new List<P>(raw.Count);
        double cpuDen = dt * 10_000.0 * _cpuCount; // 100ns units
        int threadCount = 0, handleCount = 0;

        foreach (var e in raw)
        {
            var key = (e.Pid, e.CreateTime);
            next[key] = new PrevProc(e.CpuTime100ns, e.ReadBytes, e.WriteBytes);
            threadCount += e.Threads; handleCount += e.Handles;
            var p = new P
            {
                Pid = e.Pid, Create = e.CreateTime, Name = e.Name,
                Mem = e.PrivateWorkingSet, Ws = e.WorkingSet, Commit = e.Commit, Handles = e.Handles, Threads = e.Threads,
                Priority = e.BasePriority, ParentPid = e.ParentPid,
            };
            if (store && _prev.TryGetValue(key, out var pr))
            {
                if (cpuDen > 0) p.Cpu = Math.Max(0, e.CpuTime100ns - pr.Cpu) * 100.0 / cpuDen;
                p.IoR = Math.Max(0, e.ReadBytes - pr.Read);
                p.IoW = Math.Max(0, e.WriteBytes - pr.Write);
            }
            if (etw is not null && etw.Remove(e.Pid, out var n)) { p.NetRx = n.rx; p.NetTx = n.tx; }
            if (pdh.GpuByPid is not null && pdh.GpuByPid.TryGetValue(e.Pid, out var g)) { p.Gpu = g; p.HasGpu = true; }

            if (!byName.TryGetValue(e.Name, out var a)) byName[e.Name] = a = new Agg { Name = e.Name, Pid = e.Pid };
            a.Count++;
            a.Mem += p.Mem; a.Ws += p.Ws; a.Commit += p.Commit; a.Handles += p.Handles; a.Threads += p.Threads;
            a.Cpu += p.Cpu; a.IoR += p.IoR; a.IoW += p.IoW; a.NetRx += p.NetRx; a.NetTx += p.NetTx;
            if (p.HasGpu) { a.Gpu = Math.Max(a.Gpu, p.Gpu); a.HasGpu = true; }
            a.Members.Add(p);
            p.App = a;
            procs.Add(p);
        }
        _prev = next;

        if (!store) return; // first sample after start / resume only primes the deltas

        // ---- application ids + metadata ----
        var aggs = byName.Values.ToList();
        foreach (var a in aggs)
        {
            a.Nid = _store.GetNameId(a.Name, out bool isNew);
            if (isNew) _unresolved.Add(a.Name);
        }
        if (_unresolved.Count > 0)
        {
            int budget = 25;
            foreach (var a in aggs.Where(a => _unresolved.Contains(a.Name)).OrderByDescending(a => a.Mem).ToList())
            {
                if (budget-- <= 0) break;
                _unresolved.Remove(a.Name);
                Stage($"names: {a.Name}");
                ResolveName(a.Nid, a.Pid);
            }
        }

        // ---- instance descriptions (what each PID is), cached per PID+start time ----
        Stage("instances");
        var liveInst = new Dictionary<(uint, long), InstState>(procs.Count);
        foreach (var p in procs)
        {
            var key = (p.Pid, p.Create);
            if (!_inst.TryGetValue(key, out var st))
            {
                string? cmd = p.Pid > 4 ? Native.GetCommandLine(p.Pid) : null;
                var svc = _services.Get(p.Pid, p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
                var info = ProcessDetails.Describe(p.Name, cmd, svc);
                st = new InstState { Nid = p.App.Nid, Role = info.Role, Detail = info.Detail, Cmd = info.CommandLine, CreatedMs = FileTimeToUnixMs(p.Create) };
            }
            p.Inst = st;
            liveInst[key] = st;
        }
        _inst = liveInst; // drops exited processes

        // Chromium/Electron apps: the one process without a --type is the main (browser) process.
        foreach (var a in aggs)
        {
            if (a.Members.Count < 2 || !a.Members.Any(m => m.Inst!.Role is "renderer" or "gpu" or "utility" or "network" or "extension")) continue;
            foreach (var m in a.Members)
                if (m.Inst!.Role == "app" && m.Inst.Iid == 0) m.Inst.Role = "main";
        }

        long EnsureIid(P p)
        {
            var st = p.Inst!;
            if (st.Iid == 0)
                st.Iid = _store.GetInstanceId(st.Nid, p.Pid, st.CreatedMs, new ProcessDetails.Info(st.Role, st.Detail, st.Cmd), now);
            return st.Iid;
        }

        // ---- foreground window: what the user was doing ----
        Stage("foreground");
        LiveForeground? fg = null;
        long? fgIid = null, fgTitle = null;
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd != IntPtr.Zero && Native.GetWindowThreadProcessId(hwnd, out uint fpid) != 0)
            {
                var fp = procs.FirstOrDefault(x => x.Pid == fpid);
                if (fp is not null)
                {
                    string? title = s.RecordWindowTitles ? Native.GetWindowTitle(hwnd) : null;
                    fgIid = EnsureIid(fp);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        title = title.Length > 250 ? title[..250] : title;
                        fgTitle = _store.GetStringId(title);
                        if (fp.Inst!.LastTitle != title) { fp.Inst.LastTitle = title; _store.SetInstanceTitle(fgIid.Value, title); }
                    }
                    fg = new LiveForeground { Nid = fp.App.Nid, Name = fp.Name, Pid = fp.Pid, Role = fp.Inst!.Role, Detail = fp.Inst.Detail, Title = title };
                }
            }
        }
        catch (Exception ex) { Log.Write("foreground", ex); }

        // ---- live snapshot ----
        bool perProcNet = etw is not null;
        bool detail = WantDetail;
        var self = procs.FirstOrDefault(x => x.Pid == (uint)Environment.ProcessId);
        var live = new LiveSnapshot
        {
            Ts = now, Boot = BootTime, IntervalSec = dtSec,
            Cpu = s.TrackCpu ? cpu : null,
            Gpu = s.TrackGpu && !double.IsNaN(pdh.GpuTotal) ? pdh.GpuTotal : null,
            MemUsed = memUsed, MemTotal = memTotal, CommitUsed = commitUsed, CommitLimit = commitLimit,
            DiskR = s.TrackDisk && !double.IsNaN(pdh.DiskReadBps) ? pdh.DiskReadBps : null,
            DiskW = s.TrackDisk && !double.IsNaN(pdh.DiskWriteBps) ? pdh.DiskWriteBps : null,
            NetRx = s.TrackNetwork && !double.IsNaN(pdh.NetRxBps) ? pdh.NetRxBps : null,
            NetTx = s.TrackNetwork && !double.IsNaN(pdh.NetTxBps) ? pdh.NetTxBps : null,
            ProcessCount = procs.Count, ThreadCount = threadCount, HandleCount = handleCount,
            PerProcessNetwork = perProcNet,
            NetworkNote = NetworkStatus,
            Foreground = fg,
            SelfCpu = self is null ? 0 : Math.Round(self.Cpu, 3),
            SelfMem = self?.Mem ?? 0,
            Sensors = sens,
            GpuEngines = s.TrackGpu ? pdh.GpuEngines : null,
            Processes = aggs.OrderByDescending(a => a.Mem).Take(detail ? 150 : 8).Select(a => new LiveProcess
            {
                Nid = a.Nid, Name = a.Name, Count = a.Count, Pid = a.Pid,
                Cpu = Math.Round(a.Cpu, 2), Gpu = a.HasGpu ? Math.Round(a.Gpu, 1) : null,
                Mem = a.Mem, Ws = a.Ws, Commit = a.Commit,
                IoR = a.IoR / dtSec, IoW = a.IoW / dtSec,
                NetRx = perProcNet ? a.NetRx / dtSec : null, NetTx = perProcNet ? a.NetTx / dtSec : null,
                Handles = a.Handles, Threads = a.Threads,
                Instances = detail ? a.Members.OrderByDescending(m => m.Mem).Select(m => new LiveInstance
                {
                    Pid = m.Pid, Role = m.Inst!.Role, Detail = m.Inst.Detail, Title = m.Inst.LastTitle,
                    Cpu = Math.Round(m.Cpu, 2), Gpu = m.HasGpu ? Math.Round(m.Gpu, 1) : null, Mem = m.Mem,
                    IoR = m.IoR / dtSec, IoW = m.IoW / dtSec,
                    NetRx = perProcNet ? m.NetRx / dtSec : null, NetTx = perProcNet ? m.NetTx / dtSec : null,
                    Threads = m.Threads, Handles = m.Handles, Priority = m.Priority, ParentPid = m.ParentPid,
                }).ToList() : null,
            }).ToList(),
        };
        Latest = live;

        // ---- persist ----
        var sys = new SysSample
        {
            Ts = now, Dt = dt, Boot = BootTime, Procs = procs.Count,
            Cpu = live.Cpu is double c0 ? Math.Round(c0, 2) : null,
            Gpu = live.Gpu is double g0 ? Math.Round(g0, 2) : null,
            MemUsed = s.TrackMemory ? memUsed : null, MemTotal = s.TrackMemory ? memTotal : null,
            CommitUsed = s.TrackMemory ? commitUsed : null, CommitLimit = s.TrackMemory ? commitLimit : null,
            DiskR = live.DiskR is double dr ? (long)(dr * dtSec) : null,
            DiskW = live.DiskW is double dw ? (long)(dw * dtSec) : null,
            NetRx = live.NetRx is double nr ? (long)(nr * dtSec) : null,
            NetTx = live.NetTx is double nt ? (long)(nt * dtSec) : null,
            FgIid = fgIid, FgTitle = fgTitle,
            CpuTemp = sens?.CpuTemp, GpuTemp = sens?.GpuTemp,
            CpuMhz = sens?.CpuMhz, GpuMhz = sens?.GpuMhz,
            PkgPower = sens?.PackagePower ?? sens?.CpuPower, GpuPower = sens?.GpuPower,
            GpuDed = sens?.GpuDedicatedUsed is double gd ? (long)gd : null,
            GpuShared = sens?.GpuSharedUsed is double gs ? (long)gs : null,
            FanRpm = sens?.Fan1Rpm,
        };

        var appRows = new List<ProcSample>();
        var instRows = new List<InstSample>();
        if (s.TrackProcesses)
        {
            foreach (var a in Pick(aggs, s.TopProcesses, s, perProcNet, x => x.Mem, x => x.Cpu, x => x.IoR + x.IoW, x => x.NetRx + x.NetTx, x => x.HasGpu ? x.Gpu : 0))
                appRows.Add(new ProcSample
                {
                    Nid = a.Nid, Count = a.Count,
                    Cpu = s.TrackCpu ? Math.Round(a.Cpu, 3) : null,
                    Gpu = s.TrackGpu && a.HasGpu ? Math.Round(a.Gpu, 2) : null,
                    Mem = a.Mem, Ws = a.Ws, Commit = a.Commit,
                    IoR = s.TrackDisk ? a.IoR : null, IoW = s.TrackDisk ? a.IoW : null,
                    NetRx = perProcNet ? a.NetRx : null, NetTx = perProcNet ? a.NetTx : null,
                });
            foreach (var p in Pick(procs, s.TopInstances, s, perProcNet, x => x.Mem, x => x.Cpu, x => x.IoR + x.IoW, x => x.NetRx + x.NetTx, x => x.HasGpu ? x.Gpu : 0))
                instRows.Add(new InstSample
                {
                    Iid = EnsureIid(p),
                    Cpu = s.TrackCpu ? Math.Round(p.Cpu, 3) : null,
                    Gpu = s.TrackGpu && p.HasGpu ? Math.Round(p.Gpu, 2) : null,
                    Mem = p.Mem, Ws = p.Ws, Commit = p.Commit,
                    IoR = s.TrackDisk ? p.IoR : null, IoW = s.TrackDisk ? p.IoW : null,
                    NetRx = perProcNet ? p.NetRx : null, NetTx = perProcNet ? p.NetTx : null,
                });
        }
        Stage("insert");
        _store.Insert(sys, appRows, instRows);

        if (Features.Any)
        {
            Stage("features");
            var fprocs = new List<FeatureProc>(procs.Count);
            foreach (var p in procs)
                fprocs.Add(new FeatureProc(p.Pid, p.Create, p.Name, p.Cpu, p.Mem, p.Priority, p.ParentPid, p.NetRx, p.NetTx));
            Features.OnSample(new FeatureSample(now, dt, cpu, memUsed, memTotal, fg?.Pid ?? 0, fprocs, perProcNet));
        }

        Stage("alerts");
        CheckAlerts(s, live, procs, now);
        try { Updated?.Invoke(live); } catch (Exception ex) { Log.Write("updated", ex); }
    }

    /// <summary>Union of the top consumers by each tracked metric.</summary>
    static HashSet<T> Pick<T>(List<T> items, int n, AppSettings s, bool net,
        Func<T, double> mem, Func<T, double> cpu, Func<T, double> io, Func<T, double> netF, Func<T, double> gpu)
    {
        var pick = new HashSet<T>();
        void Top(Func<T, double> f, int count, double min)
        {
            foreach (var x in items.Where(x => f(x) >= min).OrderByDescending(f).Take(count)) pick.Add(x);
        }
        if (s.TrackMemory) Top(mem, n, 1);
        if (s.TrackCpu) Top(cpu, n, 0.05);
        if (s.TrackDisk) Top(io, Math.Max(3, n / 2), 1);
        if (s.TrackNetwork && net) Top(netF, Math.Max(3, n / 2), 1);
        if (s.TrackGpu) Top(gpu, Math.Max(3, n / 2), 0.5);
        return pick;
    }

    static readonly JsonSerializerOptions EventJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    void CheckAlerts(AppSettings s, LiveSnapshot live, List<P> procs, long now)
    {
        if (!s.AlertsEnabled) { _memHighSince = _cpuHighSince = null; return; }
        var nowDt = DateTime.UtcNow;
        double memPct = live.MemTotal > 0 ? live.MemUsed * 100.0 / live.MemTotal : 0;

        object Top(IEnumerable<P> ps) => ps.Take(5).Select(p => new
        {
            nid = p.App.Nid, app = p.Name, role = p.Inst?.Role, detail = p.Inst?.Detail, pid = p.Pid,
            mem = p.Mem, cpu = Math.Round(p.Cpu, 1),
        }).ToList();

        if (memPct >= s.AlertMemoryPercent)
        {
            _memHighSince ??= nowDt;
            if (!_memAlerted && (nowDt - _memHighSince.Value).TotalSeconds >= s.AlertSustainSeconds)
            {
                _memAlerted = true;
                var top = procs.OrderByDescending(p => p.Mem).ToList();
                try { _store.InsertEvent(now, "mem", Math.Round(memPct, 1), JsonSerializer.Serialize(Top(top), EventJson)); } catch (Exception ex) { Log.Write("event", ex); }
                Alert?.Invoke($"RAM {memPct:0}%", string.Join("\n", live.Processes.Take(3).Select(p => $"{p.Name}  {p.Mem / 1048576.0:0} MB")));
            }
        }
        else if (memPct < s.AlertMemoryPercent - 3) { _memHighSince = null; _memAlerted = false; }

        if (live.Cpu is double c && c >= s.AlertCpuPercent)
        {
            _cpuHighSince ??= nowDt;
            if (!_cpuAlerted && (nowDt - _cpuHighSince.Value).TotalSeconds >= s.AlertSustainSeconds)
            {
                _cpuAlerted = true;
                var top = procs.OrderByDescending(p => p.Cpu).ToList();
                try { _store.InsertEvent(now, "cpu", Math.Round(c, 1), JsonSerializer.Serialize(Top(top), EventJson)); } catch (Exception ex) { Log.Write("event", ex); }
                Alert?.Invoke($"CPU {c:0}%", string.Join("\n", top.Take(3).Select(p => $"{p.Name}  {p.Cpu:0}%")));
            }
        }
        else if (live.Cpu is double c2 && c2 < s.AlertCpuPercent - 10) { _cpuHighSince = null; _cpuAlerted = false; }

        double temp = Math.Max(live.Sensors?.CpuTemp ?? 0, live.Sensors?.GpuTemp ?? 0);
        if (temp >= s.AlertTempC)
        {
            _tempHighSince ??= nowDt;
            if (!_tempAlerted && (nowDt - _tempHighSince.Value).TotalSeconds >= s.AlertSustainSeconds)
            {
                _tempAlerted = true;
                var top = procs.OrderByDescending(p => p.Cpu + p.Gpu).ToList();
                try { _store.InsertEvent(now, "temp", Math.Round(temp, 1), JsonSerializer.Serialize(Top(top), EventJson)); } catch (Exception ex) { Log.Write("event", ex); }
                Alert?.Invoke($"{temp:0}°C", string.Join("\n", top.Take(3).Select(p => $"{p.Name}  CPU {p.Cpu:0}%  GPU {p.Gpu:0}%")));
            }
        }
        else if (temp > 0 && temp < s.AlertTempC - 5) { _tempHighSince = null; _tempAlerted = false; }
    }

    void ResolveName(long id, uint pid)
    {
        string? path = null, descr = null;
        byte[]? icon = null;
        try
        {
            path = Native.GetProcessPath(pid);
            if (path is not null && File.Exists(path))
            {
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    descr = string.IsNullOrWhiteSpace(fvi.FileDescription) ? fvi.ProductName : fvi.FileDescription.Trim();
                }
                catch (Exception ex) { Log.Write($"version {path}", ex); }
                try
                {
                    using var ico = Icon.ExtractAssociatedIcon(path);
                    if (ico is not null)
                    {
                        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            using var big = new Icon(ico, 32, 32);
                            g.DrawIcon(big, new Rectangle(0, 0, 32, 32));
                        }
                        using var msPng = new MemoryStream();
                        bmp.Save(msPng, ImageFormat.Png);
                        icon = msPng.ToArray();
                    }
                }
                catch (Exception ex) { Log.Write($"icon {path}", ex); }
            }
        }
        catch (Exception ex) { Log.Write($"resolve {pid}", ex); }
        // Protected processes (no path without admin) are retried next run.
        if (path is not null) _store.SetNameInfo(id, path, descr, icon);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(5000);
        Features.Dispose();
    }
}
