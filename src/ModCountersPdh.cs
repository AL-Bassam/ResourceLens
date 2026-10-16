using System.Runtime.InteropServices;
using System.Text;

namespace ResourceLens;

/// <summary>
/// PDH calls the counters module needs beyond Native.cs: browsing objects / counters / instances, localized paths,
/// path checks and explain texts.
/// </summary>
internal static unsafe class CounterPdh
{
    public const uint Fmt = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
    const uint PERF_DETAIL_WIZARD = 400; // every object and counter, like perfmon's "Add Counters"
    const uint PDH_CSTATUS_NEW_DATA = 1;
    const uint PDH_CSTATUS_NO_MACHINE = 0x800007D0, PDH_CSTATUS_NO_INSTANCE = 0x800007D1, PDH_NO_DATA = 0x800007D5;
    const uint PDH_CSTATUS_NO_OBJECT = 0xC0000BB8, PDH_CSTATUS_NO_COUNTER = 0xC0000BB9;
    const uint PDH_CSTATUS_NO_COUNTERNAME = 0xC0000BBF, PDH_CSTATUS_BAD_COUNTERNAME = 0xC0000BC0;
    const uint PDH_INVALID_ARGUMENT = 0xC0000BBD, PDH_CANNOT_CONNECT_MACHINE = 0xC0000BC3, PDH_INVALID_PATH = 0xC0000BC4;
    const uint PDH_INVALID_INSTANCE = 0xC0000BC5, PDH_ACCESS_DENIED = 0xC0000BDB;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhEnumObjectsW(string? dataSource, string? machine, char[]? list, ref uint size, uint detail, bool refresh);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhEnumObjectItemsW(string? dataSource, string? machine, string obj, char[]? counters, ref uint countersLen,
        char[]? instances, ref uint instancesLen, uint detail, uint flags);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhValidatePathW(string path);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetCounterInfoW(IntPtr counter, bool explain, ref uint size, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_COUNTER_INFO
    {
        public uint dwLength, dwType, CVersion, CStatus;
        public int lScale, lDefaultScale;
        public IntPtr dwUserData, dwQueryUserData, szFullPath;
        public IntPtr szMachineName, szObjectName, szInstanceName, szParentInstance;
        public uint dwInstanceIndex;
        public IntPtr szCounterName, szExplainText;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int FormatMessageW(uint flags, IntPtr source, uint id, uint lang, StringBuilder buf, int size, IntPtr args);

    // ---------------- status codes ----------------

    public static bool Valid(uint cstatus) => cstatus == 0 || cstatus == PDH_CSTATUS_NEW_DATA;

    /// <summary>Error code the dashboard translates (cn.err.*).</summary>
    public static string Code(uint st) => st switch
    {
        PDH_CSTATUS_NO_OBJECT => "no-object",
        PDH_CSTATUS_NO_COUNTER or PDH_CSTATUS_NO_COUNTERNAME or PDH_CSTATUS_BAD_COUNTERNAME => "no-counter",
        PDH_CSTATUS_NO_INSTANCE or PDH_INVALID_INSTANCE => "no-instance",
        PDH_INVALID_PATH or PDH_INVALID_ARGUMENT => "bad-path",
        PDH_ACCESS_DENIED or 5 => "access",
        PDH_CSTATUS_NO_MACHINE or PDH_CANNOT_CONNECT_MACHINE => "remote",
        PDH_NO_DATA => "no-data",
        _ => "failed",
    };

    /// <summary>"0xC0000BB8 — the specified object was not found" (PDH's own text, in the Windows language).</summary>
    public static string Detail(uint st)
    {
        var sb = new StringBuilder(512);
        int n = FormatMessageW(0x00000800 | 0x00000200 /* FROM_HMODULE | IGNORE_INSERTS */, GetModuleHandleW("pdh.dll"), st, 0, sb, sb.Capacity, IntPtr.Zero);
        string msg = n > 0 ? sb.ToString().Trim() : "";
        return msg.Length > 0 ? $"0x{st:X8} — {msg}" : $"0x{st:X8}";
    }

    // ---------------- browsing ----------------

    static List<string> MultiSz(char[] buf)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != '\0') continue;
            if (i == start) break; // double NUL ends the list
            list.Add(new string(buf, start, i - start));
            start = i + 1;
        }
        return list;
    }

    /// <summary>Object names in the Windows display language. Slow (hundreds of ms to seconds): call from a background task.</summary>
    public static (List<string> Objects, uint Status) EnumObjects(bool refresh)
    {
        uint size = 0;
        uint st = PdhEnumObjectsW(null, null, null, ref size, PERF_DETAIL_WIZARD, refresh);
        for (int i = 0; i < 4 && st == Native.PDH_MORE_DATA; i++)
        {
            var buf = new char[size + 256];
            size = (uint)buf.Length;
            st = PdhEnumObjectsW(null, null, buf, ref size, PERF_DETAIL_WIZARD, false);
            if (st == 0) return (MultiSz(buf), 0);
        }
        return (new List<string>(), st);
    }

    /// <summary>Counters and instances of one object; Instances is null for single-instance objects (Memory, System…).</summary>
    public static (List<string> Counters, List<string>? Instances, uint Status) EnumItems(string obj)
    {
        uint cl = 0, il = 0;
        uint st = PdhEnumObjectItemsW(null, null, obj, null, ref cl, null, ref il, PERF_DETAIL_WIZARD, 0);
        bool multi = il > 0;
        // Instances come and go between the two calls (Process, Thread): retry with roomier buffers.
        for (int i = 0; i < 4 && st == Native.PDH_MORE_DATA; i++)
        {
            var cb = new char[cl + 256];
            var ib = multi ? new char[il + il / 4 + 4096] : null;
            cl = (uint)cb.Length;
            il = ib is null ? 0 : (uint)ib.Length;
            st = PdhEnumObjectItemsW(null, null, obj, cb, ref cl, ib, ref il, PERF_DETAIL_WIZARD, 0);
            if (st == 0) return (MultiSz(cb), ib is null ? null : MultiSz(ib), 0);
        }
        // an object whose provider is not loaded can answer "success" with nothing in it
        return (new List<string>(), null, st == 0 ? PDH_CSTATUS_NO_OBJECT : st);
    }

    // ---------------- paths ----------------

    public readonly record struct PathParts(string Object, string? Instance, string Counter);

    /// <summary>Splits "\Object(Instance)\Counter" (machine prefix not supported: local counters only).</summary>
    public static PathParts? Parse(string path)
    {
        path = path.Trim();
        if (path.Length < 4 || path[0] != '\\' || path.StartsWith(@"\\")) return null;
        int last = path.LastIndexOf('\\');
        if (last <= 1 || last == path.Length - 1) return null;
        string head = path[1..last], counter = path[(last + 1)..];
        if (head.Length == 0) return null;
        // The instance is the final "(…)" glued to the object name; object names may carry their own spaced
        // parentheses ("DC Locator (DC)", "Hyper-V Virtual IDE Controller (Emulated)(*)"). PDH writes instance
        // parentheses as [ ], so instance names never nest.
        int open = head.EndsWith(')') ? head.LastIndexOf('(') : -1;
        if (open <= 0 || head[open - 1] == ' ') return new PathParts(head, null, counter);
        return new PathParts(head[..open], head[(open + 1)..^1], counter);
    }

    public static string MakePath(string obj, string? instance, string counter) =>
        instance is null ? $@"\{obj}\{counter}" : $@"\{obj}({instance})\{counter}";

    public static bool IsWildcard(string path) => Parse(path)?.Instance?.Contains('*') == true;

    public static uint Add(IntPtr query, string path, bool english, out IntPtr counter) =>
        english ? Native.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter) : PdhAddCounterW(query, path, IntPtr.Zero, out counter);

    public static double? One(IntPtr counter) =>
        Native.PdhGetFormattedCounterValue(counter, Fmt, out _, out var v) == 0 && Valid(v.CStatus) && double.IsFinite(v.doubleValue) ? v.doubleValue : null;

    /// <summary>Wildcard values, with repeated instance names numbered the way perfmon shows them (svchost, svchost#1…).</summary>
    public static List<(string Name, double V)> Many(IntPtr counter)
    {
        var res = new List<(string, double)>();
        uint size = 0;
        if (Native.PdhGetFormattedCounterArrayW(counter, Fmt, ref size, out uint count, IntPtr.Zero) != Native.PDH_MORE_DATA || size == 0) return res;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, Fmt, ref size, out count, buf) != 0) return res;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string name = Marshal.PtrToStringUni(items[i].szName) ?? "";
                int n = seen.GetValueOrDefault(name);
                seen[name] = n + 1;
                if (n > 0) name = $"{name}#{n}";
                if (Valid(items[i].FmtValue.CStatus) && double.IsFinite(items[i].FmtValue.doubleValue)) res.Add((name, items[i].FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }

    public sealed record ProbeResult(bool Ok, string? Error, string? Detail, double? Value, int Instances);

    /// <summary>
    /// Proves a path works here: add it to a throw-away query and collect twice (rates need two samples). Localized
    /// paths are first checked with PdhValidatePath, which names the failing part precisely.
    /// </summary>
    public static ProbeResult Probe(string path, bool english)
    {
        var parts = Parse(path);
        if (path.TrimStart().StartsWith(@"\\")) return new(false, "remote", null, null, 0);
        if (parts is null) return new(false, "bad-path", null, null, 0);
        if (parts.Value.Object.Contains('*') || parts.Value.Counter.Contains('*')) return new(false, "wild-counter", null, null, 0);
        bool wild = parts.Value.Instance?.Contains('*') == true;
        if (!english && !wild)
        {
            uint vs = PdhValidatePathW(path);
            if (vs != 0 && vs != PDH_CSTATUS_NO_INSTANCE) return new(false, Code(vs), Detail(vs), null, 0);
        }
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return new(false, "failed", null, null, 0);
        try
        {
            uint st = Add(q, path, english, out var c);
            if (st != 0) return new(false, Code(st), Detail(st), null, 0);
            Native.PdhCollectQueryData(q);
            Thread.Sleep(150);
            st = Native.PdhCollectQueryData(q);
            // "no data" for a named instance means that instance does not exist right now (process not running…)
            if (st == PDH_NO_DATA && parts.Value.Instance is not null) return new(false, "no-instance", Detail(st), null, 0);
            if (st != 0 && st != Native.PDH_MORE_DATA) return new(false, Code(st), Detail(st), null, 0);
            if (wild)
            {
                int n = Many(c).Count;
                return n > 0 ? new(true, null, null, null, n) : new(false, "no-instance", null, null, 0);
            }
            st = Native.PdhGetFormattedCounterValue(c, Fmt, out _, out var v);
            // the item status says why (no such instance, access…); the call's own status is only "invalid data"
            if (!Valid(v.CStatus)) st = v.CStatus;
            if (st != 0) return new(false, Code(st), Detail(st), null, 0);
            return new(true, null, null, double.IsFinite(v.doubleValue) ? v.doubleValue : null, 1);
        }
        finally { Native.PdhCloseQuery(q); }
    }

    /// <summary>The counter's description (perfmon's "Show description"), in the Windows display language.</summary>
    public static string? Explain(string path, bool english)
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return null;
        try
        {
            if (Add(q, path, english, out var c) != 0) return null;
            uint size = 0;
            if (PdhGetCounterInfoW(c, true, ref size, IntPtr.Zero) != Native.PDH_MORE_DATA || size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetCounterInfoW(c, true, ref size, buf) != 0) return null;
                var info = Marshal.PtrToStructure<PDH_COUNTER_INFO>(buf);
                return info.szExplainText == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.szExplainText)?.Trim();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.PdhCloseQuery(q); }
    }

    /// <summary>Orders "Processor 2" before "Processor 10" and "0,9" before "0,10".</summary>
    public sealed class Natural : IComparer<string>
    {
        public static readonly Natural Instance = new();
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : na.CompareTo(nb, StringComparison.Ordinal);
                    if (c != 0) return c;
                    continue;
                }
                int d = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
                if (d != 0) return d;
                i++; j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}

/// <summary>
/// The live chart's 1-second sampler: its own PDH query and timer, created when the page polls and gone a minute after
/// the last "counters.live" call (same idea as the Performance page's PerfMonitor), so it costs nothing otherwise.
/// </summary>
internal sealed class CounterSampler : IDisposable
{
    public const int Window = 120, MaxPerItem = 16;
    const long IdleMs = 60_000;

    public sealed class Series
    {
        public required string Name;
        public readonly List<double?> V = new(Window);
        public double WindowMax() { double m = double.MinValue; foreach (var x in V) if (x is { } v && v > m) m = v; return m; }
    }

    public sealed class Entry
    {
        public required string Key;
        public required string Path;
        public required bool English;
        public bool Wild;
        public IntPtr H;
        public string? Error, Detail;
        public int More;
        public readonly List<Series> Series = new();
    }

    readonly object _gate = new();
    readonly List<long> _times = new(Window);
    readonly List<Entry> _entries = new();
    readonly System.Threading.Timer _timer;
    readonly Action<CounterSampler> _onIdle;
    IntPtr _q;
    long _lastUsed = Environment.TickCount64;

    public CounterSampler(IEnumerable<(string Key, string Path, bool English)> items, Action<CounterSampler> onIdle)
    {
        _onIdle = onIdle;
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
        lock (_gate)
        {
            foreach (var it in items) AddEntry(it.Key, it.Path, it.English);
            if (_q != IntPtr.Zero) Native.PdhCollectQueryData(_q); // prime rate counters
        }
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Touch() => Volatile.Write(ref _lastUsed, Environment.TickCount64);

    void AddEntry(string key, string path, bool english)
    {
        var e = new Entry { Key = key, Path = path, English = english, Wild = CounterPdh.IsWildcard(path) };
        if (_q == IntPtr.Zero) e.Error = "failed";
        else
        {
            uint st = CounterPdh.Add(_q, path, english, out e.H);
            if (st != 0) { e.H = IntPtr.Zero; e.Error = CounterPdh.Code(st); e.Detail = CounterPdh.Detail(st); }
        }
        if (!e.Wild && e.Error is null) e.Series.Add(new Series { Name = "" });
        _entries.Add(e);
    }

    /// <summary>Makes the sampled set match the dashboard's list; kept counters keep their history.</summary>
    public void SetItems(IEnumerable<(string Key, string Path, bool English)> items)
    {
        lock (_gate)
        {
            var want = items.ToList();
            var keys = want.Select(w => w.Key).ToHashSet();
            foreach (var e in _entries.Where(e => !keys.Contains(e.Key)).ToList())
            {
                if (e.H != IntPtr.Zero) CounterPdh.PdhRemoveCounter(e.H);
                _entries.Remove(e);
            }
            foreach (var w in want)
                if (!_entries.Any(e => e.Key == w.Key)) AddEntry(w.Key, w.Path, w.English);
            _entries.Sort((a, b) => want.FindIndex(w => w.Key == a.Key).CompareTo(want.FindIndex(w => w.Key == b.Key)));
        }
    }

    void Tick()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastUsed) > IdleMs) { _onIdle(this); return; }
        if (!Monitor.TryEnter(_gate)) return; // previous tick still collecting
        try
        {
            if (_q == IntPtr.Zero) return;
            Native.PdhCollectQueryData(_q);
            _times.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (_times.Count > Window) _times.RemoveAt(0);
            foreach (var e in _entries)
            {
                if (e.H == IntPtr.Zero) continue;
                if (!e.Wild) { Push(e.Series[0], CounterPdh.One(e.H)); continue; }
                SampleWild(e);
            }
        }
        catch (Exception ex) { Log.Write("counters: live tick", ex); }
        finally { Monitor.Exit(_gate); }
    }

    void Push(Series s, double? v)
    {
        while (s.V.Count < _times.Count - 1) s.V.Add(null); // a late series starts aligned with the time axis
        s.V.Add(v);
        while (s.V.Count > _times.Count) s.V.RemoveAt(0);
    }

    /// <summary>
    /// Up to 16 series per wildcard: the busiest instances. A tracked instance gives way only to one clearly busier,
    /// so the chart does not flicker between similar instances.
    /// </summary>
    void SampleWild(Entry e)
    {
        var cur = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in CounterPdh.Many(e.H)) cur[n] = v;
        e.Series.RemoveAll(s => !cur.ContainsKey(s.Name) && s.V.All(x => x is null));
        var tracked = e.Series.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int swaps = 0;
        foreach (var (name, v) in cur.Where(kv => !tracked.Contains(kv.Key)).OrderByDescending(kv => kv.Key == "_Total" ? double.MaxValue : kv.Value))
        {
            // _Total rides along on top of the cap, so 16 cores plus their total still fit
            if (name != "_Total" && e.Series.Count(s => s.Name != "_Total") >= MaxPerItem)
            {
                var weakest = e.Series.Where(s => s.Name != "_Total").MinBy(s => s.WindowMax());
                double wm = weakest?.WindowMax() ?? double.MaxValue;
                if (weakest is null || swaps >= 2 || !(v > Math.Max(wm * 2, wm + 1))) break;
                e.Series.Remove(weakest);
                swaps++;
            }
            e.Series.Add(new Series { Name = name });
        }
        foreach (var s in e.Series) Push(s, cur.TryGetValue(s.Name, out var x) ? x : null);
        e.More = Math.Max(0, cur.Count - e.Series.Count(s => cur.ContainsKey(s.Name)));
    }

    public sealed record SeriesView(string Name, double?[] Values, double? Last, double? Avg, double? Min, double? Max);
    public sealed record EntryView(string Key, string? Error, string? Detail, int More, List<SeriesView> Series);

    static double? R(double? v) => v is { } x ? CountersFeature.Sig(x) : null;

    public (long[] Times, Dictionary<string, EntryView> Entries) Snapshot()
    {
        Touch();
        lock (_gate)
        {
            var map = new Dictionary<string, EntryView>();
            foreach (var e in _entries)
            {
                var list = new List<SeriesView>();
                var ordered = e.Series.OrderBy(s => s.Name == "_Total" ? 0 : 1).ThenBy(s => s.Name, CounterPdh.Natural.Instance);
                foreach (var s in ordered)
                {
                    double? last = s.V.Count > 0 ? s.V[^1] : null, min = null, max = null;
                    double sum = 0; int n = 0;
                    foreach (var x in s.V)
                    {
                        if (x is not { } v) continue;
                        sum += v; n++;
                        if (min is null || v < min) min = v;
                        if (max is null || v > max) max = v;
                    }
                    list.Add(new SeriesView(s.Name, s.V.Select(R).ToArray(), R(last), n > 0 ? R(sum / n) : null, R(min), R(max)));
                }
                map[e.Key] = new EntryView(e.Key, e.Error, e.Detail, e.More, list);
            }
            return (_times.ToArray(), map);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
            _entries.Clear();
        }
    }
}
