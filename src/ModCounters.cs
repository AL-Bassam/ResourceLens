using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

internal static class ModCountersInit
{
    [ModuleInitializer]
    internal static void Init() => Features.Register(new CountersFeature());
}

/// <summary>A counter path as the user added it: localized (from the browser) or English (presets).</summary>
internal class CounterRef
{
    public string Path { get; set; } = "";
    public bool English { get; set; }
    /// <summary>PDH paths are case-insensitive; on an English Windows both kinds of path are the same string.</summary>
    [JsonIgnore] public string Key => Path.ToLowerInvariant();
}

internal sealed class CounterItem : CounterRef
{
    /// <summary>Display multiplier (perfmon's "Scale"); 0 = automatic.</summary>
    public double Scale { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Instances of a wildcard counter the user hid.</summary>
    public List<string> Off { get; set; } = new();
}

/// <summary>counters.json: the live chart's counters, the recorded set, and how far recorded data is rolled up.</summary>
internal sealed class CountersConfig
{
    public List<CounterItem> Live { get; set; } = new();
    public List<CounterRef> Recorded { get; set; } = new();
    public long RolledTo { get; set; }
}

/// <summary>
/// Performance Monitor-style counters: browse every PDH object / counter / instance, chart up to 16 counters live
/// (1-second sampler that exists only while the page polls), and record up to 8 counters with every stored sample into
/// features.db. Nothing runs in the background unless counters are recorded; then it is one PDH collect per sample.
/// </summary>
internal sealed class CountersFeature : IFeature
{
    public const int MaxLive = 16, MaxRecorded = 8;
    const long Minute = 60_000;

    public string Id => "counters";

    FeatureHost _host = null!;
    CountersConfig _cfg = new();
    readonly object _cfgGate = new();
    readonly object _liveGate = new();
    CounterSampler? _live;
    readonly object _recGate = new(), _rebuildGate = new();
    Recorder? _rec;
    volatile bool _schema;
    readonly CounterBrowser _browser = new();

    /// <summary>One-click counters (English paths, so they work on every Windows language).</summary>
    static readonly object[] Presets =
    {
        P("cpu", "cpu", @"\Processor(_Total)\% Processor Time"),
        P("queue", "cpu", @"\System\Processor Queue Length"),
        P("ctx", "cpu", @"\System\Context Switches/sec"),
        P("avail", "mem", @"\Memory\Available MBytes"),
        P("pages", "mem", @"\Memory\Pages/sec"),
        P("committed", "mem", @"\Memory\Committed Bytes"),
        P("commitPct", "mem", @"\Memory\% Committed Bytes In Use"),
        P("pagefile", "mem", @"\Paging File(_Total)\% Usage"),
        P("diskTime", "disk", @"\PhysicalDisk(_Total)\% Disk Time"),
        P("diskQueue", "disk", @"\PhysicalDisk(_Total)\Avg. Disk Queue Length"),
        P("diskBytes", "disk", @"\PhysicalDisk(_Total)\Disk Bytes/sec"),
        P("netBytes", "net", @"\Network Interface(*)\Bytes Total/sec"),
        P("handles", "sys", @"\Process(_Total)\Handle Count"),
        P("threads", "sys", @"\System\Threads"),
    };
    static object P(string id, string group, string path) => new { id, group, path, english = true };

    // ---------------- lifecycle ----------------

    public void Start(FeatureHost host)
    {
        _host = host;
        var cfg = host.LoadConfig<CountersConfig>(Id);
        cfg.Live = Clean(cfg.Live).Take(MaxLive).ToList();
        cfg.Recorded = Clean(cfg.Recorded).Where(r => !CounterPdh.IsWildcard(r.Path)).Take(MaxRecorded).ToList();
        lock (_cfgGate) _cfg = cfg;
        host.Store.Cleared += OnHistoryCleared;
        // Adding counters loads PDH's name tables (a few hundred ms): keep that off the start-up path.
        if (cfg.Recorded.Count > 0)
            Task.Run(() => { try { RebuildRecorder(); } catch (Exception ex) { Log.Write("counters: recorder", ex); } });
    }

    static List<T> Clean<T>(IEnumerable<T>? items) where T : CounterRef
    {
        var seen = new HashSet<string>();
        var list = new List<T>();
        foreach (var it in items ?? Enumerable.Empty<T>())
        {
            it.Path = (it.Path ?? "").Trim();
            if (CounterPdh.Parse(it.Path) is null || !seen.Add(it.Key)) continue;
            if (it is CounterItem ci)
            {
                if (!double.IsFinite(ci.Scale) || ci.Scale < 0) ci.Scale = 0;
                ci.Off ??= new();
            }
            list.Add(it);
        }
        return list;
    }

    void SaveConfig()
    {
        try { _host.SaveConfig(Id, _cfg); } catch (Exception ex) { Log.Write("counters: save config", ex); }
    }

    public void OnSample(FeatureSample s)
    {
        List<(long Id, double V)> rows;
        lock (_recGate)
        {
            if (_rec is null) return; // nothing recorded: zero cost
            rows = _rec.Collect();
        }
        if (rows.Count == 0) return;
        _host.Db.Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO counters_data(ts, cid, v) VALUES ($t, $c, $v)";
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pc = cmd.Parameters.Add("$c", SqliteType.Integer);
            var pv = cmd.Parameters.Add("$v", SqliteType.Real);
            pt.Value = s.Ts;
            foreach (var (id, v) in rows) { pc.Value = id; pv.Value = v; cmd.ExecuteNonQuery(); }
        });
    }

    /// <summary>Retention, plus 1-minute means for samples older than the full-resolution window (like history.db).</summary>
    public void OnMaintain(long cutMs)
    {
        if (!File.Exists(FeatureDb.DbPath)) return;
        EnsureSchema();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fullCut = now - Math.Max(1, _host.Settings.FullResolutionDays) * 86_400_000L;
        fullCut -= fullCut % Minute;
        long rolledTo;
        HashSet<string> recorded;
        lock (_cfgGate) { rolledTo = _cfg.RolledTo; recorded = _cfg.Recorded.Select(r => r.Key).ToHashSet(); }
        long rollFrom = Math.Max(rolledTo, cutMs - cutMs % Minute);

        _host.Db.Write((c, tx) =>
        {
            var defs = new List<(long Id, string Path)>();
            using (var q = Cmd(c, tx, "SELECT id, path FROM counters_def"))
            using (var r = q.ExecuteReader())
                while (r.Read()) defs.Add((r.GetInt64(0), r.GetString(1)));

            foreach (var (id, path) in defs)
            {
                using (var del = Cmd(c, tx, "DELETE FROM counters_data WHERE cid = $c AND ts < $cut", ("$c", id), ("$cut", cutMs)))
                    del.ExecuteNonQuery();
                if (fullCut > rollFrom) RollUp(c, tx, id, rollFrom, fullCut);
                if (!recorded.Contains(path.ToLowerInvariant()))
                {
                    using var any = Cmd(c, tx, "SELECT EXISTS(SELECT 1 FROM counters_data WHERE cid = $c)", ("$c", id));
                    if (Convert.ToInt64(any.ExecuteScalar()) == 0)
                        using (var dd = Cmd(c, tx, "DELETE FROM counters_def WHERE id = $c", ("$c", id))) dd.ExecuteNonQuery();
                }
            }
        });
        if (fullCut > rolledTo)
            lock (_cfgGate) { _cfg.RolledTo = fullCut; SaveConfig(); }
    }

    static void RollUp(SqliteConnection c, SqliteTransaction tx, long id, long from, long to)
    {
        var means = new List<(long T, double V)>();
        using (var q = Cmd(c, tx, "SELECT (ts / 60000) * 60000 AS b, AVG(v) FROM counters_data WHERE cid = $c AND ts >= $f AND ts < $t GROUP BY b",
                   ("$c", id), ("$f", from), ("$t", to)))
        using (var r = q.ExecuteReader())
            while (r.Read()) means.Add((r.GetInt64(0), Sig(r.GetDouble(1))));
        if (means.Count == 0) return;
        using (var del = Cmd(c, tx, "DELETE FROM counters_data WHERE cid = $c AND ts >= $f AND ts < $t", ("$c", id), ("$f", from), ("$t", to)))
            del.ExecuteNonQuery();
        using var ins = Cmd(c, tx, "INSERT OR REPLACE INTO counters_data(ts, cid, v) VALUES ($t, $c, $v)", ("$c", id));
        var pt = ins.Parameters.Add("$t", SqliteType.Integer);
        var pv = ins.Parameters.Add("$v", SqliteType.Real);
        foreach (var (t, v) in means) { pt.Value = t; pv.Value = v; ins.ExecuteNonQuery(); }
    }

    /// <summary>The user wiped the history: recorded counter data goes with it (the recorded set itself stays).</summary>
    void OnHistoryCleared()
    {
        try
        {
            if (!File.Exists(FeatureDb.DbPath)) return;
            EnsureSchema();
            HashSet<string> recorded;
            lock (_cfgGate) { recorded = _cfg.Recorded.Select(r => r.Key).ToHashSet(); _cfg.RolledTo = 0; SaveConfig(); }
            _host.Db.Write((c, tx) =>
            {
                using (var d = Cmd(c, tx, "DELETE FROM counters_data")) d.ExecuteNonQuery();
                var drop = new List<long>();
                using (var q = Cmd(c, tx, "SELECT id, path FROM counters_def"))
                using (var r = q.ExecuteReader())
                    while (r.Read()) if (!recorded.Contains(r.GetString(1).ToLowerInvariant())) drop.Add(r.GetInt64(0));
                foreach (var id in drop)
                    using (var d = Cmd(c, tx, "DELETE FROM counters_def WHERE id = $c", ("$c", id))) d.ExecuteNonQuery();
            });
        }
        catch (Exception ex) { Log.Write("counters: clear", ex); }
    }

    public void Dispose()
    {
        CounterSampler? live;
        lock (_liveGate) { live = _live; _live = null; }
        live?.Dispose();
        lock (_recGate) { _rec?.Dispose(); _rec = null; }
        _browser.Dispose();
    }

    // ---------------- database ----------------

    static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    void EnsureSchema()
    {
        if (_schema) return;
        _host.Db.Write((c, tx) =>
        {
            using var cmd = Cmd(c, tx, """
                CREATE TABLE IF NOT EXISTS counters_def(id INTEGER PRIMARY KEY, path TEXT UNIQUE COLLATE NOCASE, english INTEGER);
                CREATE TABLE IF NOT EXISTS counters_data(ts INTEGER, cid INTEGER, v REAL, PRIMARY KEY(cid, ts)) WITHOUT ROWID;
                """);
            cmd.ExecuteNonQuery();
        });
        _schema = true;
    }

    /// <summary>Six significant digits: plenty for a chart, and whole numbers are stored by SQLite as compact integers.</summary>
    public static double Sig(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return double.IsFinite(v) ? v : 0;
        int digits = 5 - (int)Math.Floor(Math.Log10(Math.Abs(v)));
        return digits <= 0 ? Math.Round(v) : Math.Round(v, Math.Min(digits, 12));
    }

    // ---------------- recording ----------------

    /// <summary>The persistent query behind recording; it exists only while at least one counter is recorded.</summary>
    sealed class Recorder : IDisposable
    {
        public sealed record Rec(long Id, string Path, IntPtr H, string? Error);
        IntPtr _q;
        public readonly List<Rec> Items = new();

        public Recorder(IEnumerable<(long Id, CounterRef Ref)> items)
        {
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
            foreach (var (id, r) in items)
            {
                IntPtr h = IntPtr.Zero;
                uint st = _q == IntPtr.Zero ? 1u : CounterPdh.Add(_q, r.Path, r.English, out h);
                Items.Add(st == 0 ? new Rec(id, r.Path, h, null) : new Rec(id, r.Path, IntPtr.Zero, st == 1 ? "failed" : CounterPdh.Code(st)));
            }
            if (_q != IntPtr.Zero) Native.PdhCollectQueryData(_q);
        }

        public List<(long, double)> Collect()
        {
            var rows = new List<(long, double)>(Items.Count);
            if (_q == IntPtr.Zero) return rows;
            Native.PdhCollectQueryData(_q);
            foreach (var it in Items)
                if (it.H != IntPtr.Zero && CounterPdh.One(it.H) is { } v) rows.Add((it.Id, Sig(v)));
            return rows;
        }

        public void Dispose()
        {
            if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
        }
    }

    void RebuildRecorder()
    {
        lock (_rebuildGate)
        {
            List<CounterRef> refs;
            lock (_cfgGate) refs = _cfg.Recorded.ToList();
            Recorder? next = null;
            if (refs.Count > 0)
            {
                EnsureSchema();
                var ids = new List<long>();
                _host.Db.Write((c, tx) =>
                {
                    foreach (var r in refs)
                    {
                        using (var up = Cmd(c, tx, "INSERT INTO counters_def(path, english) VALUES ($p, $e) ON CONFLICT(path) DO UPDATE SET english = excluded.english",
                                   ("$p", r.Path), ("$e", r.English ? 1 : 0)))
                            up.ExecuteNonQuery();
                        using var q = Cmd(c, tx, "SELECT id FROM counters_def WHERE path = $p", ("$p", r.Path));
                        ids.Add(Convert.ToInt64(q.ExecuteScalar()));
                    }
                });
                next = new Recorder(ids.Zip(refs));
            }
            Recorder? old;
            lock (_recGate) { old = _rec; _rec = next; }
            old?.Dispose();
        }
    }

    List<object> RecordedView()
    {
        List<CounterRef> refs;
        lock (_cfgGate) refs = _cfg.Recorded.ToList();
        Dictionary<string, Recorder.Rec> state;
        lock (_recGate) state = _rec?.Items.ToDictionary(i => i.Path.ToLowerInvariant()) ?? new();
        return refs.Select(r =>
        {
            var parts = CounterPdh.Parse(r.Path);
            var st = state.GetValueOrDefault(r.Key);
            return (object)new { key = r.Key, r.Path, r.English, id = st?.Id, error = st?.Error, obj = parts?.Object, inst = parts?.Instance, counter = parts?.Counter };
        }).ToList();
    }

    // ---------------- live ----------------

    List<(string Key, string Path, bool English)> LiveRefs()
    {
        lock (_cfgGate) return _cfg.Live.Select(i => (i.Key, i.Path, i.English)).ToList();
    }

    void DropLive(CounterSampler s)
    {
        lock (_liveGate) { if (_live == s) _live = null; }
        s.Dispose();
    }

    object LiveView()
    {
        CounterSampler s;
        lock (_liveGate) s = _live ??= new CounterSampler(LiveRefs(), DropLive);
        var (times, entries) = s.Snapshot();
        List<CounterItem> items;
        HashSet<string> rec;
        lock (_cfgGate)
        {
            items = _cfg.Live.Select(i => new CounterItem { Path = i.Path, English = i.English, Scale = i.Scale, Hidden = i.Hidden, Off = i.Off.ToList() }).ToList();
            rec = _cfg.Recorded.Select(r => r.Key).ToHashSet();
        }
        return new
        {
            times,
            items = items.Select(it =>
            {
                var parts = CounterPdh.Parse(it.Path);
                var ev = entries.GetValueOrDefault(it.Key);
                return new
                {
                    key = it.Key, it.Path, it.English, it.Scale, it.Hidden, it.Off,
                    record = rec.Contains(it.Key), wildcard = CounterPdh.IsWildcard(it.Path),
                    obj = parts?.Object, inst = parts?.Instance, counter = parts?.Counter,
                    error = ev?.Error, detail = ev?.Detail, more = ev?.More ?? 0,
                    series = (IEnumerable<CounterSampler.SeriesView>?)ev?.Series ?? Array.Empty<CounterSampler.SeriesView>(),
                };
            }).ToList(),
            recorded = RecordedView(),
            maxLive = MaxLive, maxRecorded = MaxRecorded, maxSeries = CounterSampler.MaxPerItem,
            interval = _host.Settings.IntervalSeconds,
            isAdmin = Native.IsUserAnAdmin(),
        };
    }

    object SetLive(JsonElement p)
    {
        var list = new List<CounterItem>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
            {
                var it = new CounterItem
                {
                    Path = S(el, "path"), English = B(el, "english"), Hidden = B(el, "hidden"),
                    Scale = el.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0,
                    Off = el.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Array
                        ? off.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(256).ToList() : new(),
                };
                list.Add(it);
            }
        list = Clean(list);
        if (list.Count > MaxLive) return Fail("limit");
        lock (_cfgGate) { _cfg.Live = list; SaveConfig(); }
        lock (_liveGate) _live?.SetItems(list.Select(i => (i.Key, i.Path, i.English)));
        return new { ok = true };
    }

    object SetRecorded(JsonElement p)
    {
        var list = new List<CounterRef>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray()) list.Add(new CounterRef { Path = S(el, "path"), English = B(el, "english") });
        list = Clean(list);
        if (list.Count > MaxRecorded) return Fail("limit");
        HashSet<string> current;
        lock (_cfgGate) current = _cfg.Recorded.Select(r => r.Key).ToHashSet();
        foreach (var r in list)
        {
            // one stored value per sample: a wildcard would mean an open-ended number of rows
            if (CounterPdh.IsWildcard(r.Path)) return Fail("wildcard", null, r.Path);
            if (current.Contains(r.Key)) continue;
            var probe = CounterPdh.Probe(r.Path, r.English);
            if (!probe.Ok) return Fail(probe.Error ?? "failed", probe.Detail, r.Path);
        }
        lock (_cfgGate) { _cfg.Recorded = list; SaveConfig(); }
        RebuildRecorder();
        return new { ok = true, recorded = RecordedView() };
    }

    // ---------------- history ----------------

    object History(JsonElement p)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long span = S(p, "range", "1h") switch { "24h" => 86_400_000L, "7d" => 7 * 86_400_000L, _ => 3_600_000L };
        long to = L(p, "to") ?? now, from = L(p, "from") ?? to - span;
        if (to <= from) return Fail("bad-range");
        long intervalMs = Math.Max(1, _host.Settings.IntervalSeconds) * 1000L;
        // ≤ 600 points; a whole number of sample intervals per bucket so every bucket averages the same count
        long bucket = (long)Math.Max(1, Math.Ceiling((to - from) / 600.0 / intervalMs)) * intervalMs;
        HashSet<string> recorded;
        long rolledTo;
        lock (_cfgGate) { recorded = _cfg.Recorded.Select(r => r.Key).ToHashSet(); rolledTo = _cfg.RolledTo; }
        var series = new List<object>();
        if (File.Exists(FeatureDb.DbPath))
        {
            EnsureSchema();
            using var c = _host.Db.OpenReader();
            var defs = new List<(long Id, string Path, bool English)>();
            using (var q = Cmd(c, null, "SELECT id, path, english FROM counters_def ORDER BY id"))
            using (var r = q.ExecuteReader())
                while (r.Read()) defs.Add((r.GetInt64(0), r.GetString(1), !r.IsDBNull(2) && r.GetInt64(2) != 0));
            foreach (var (id, path, english) in defs)
            {
                var t = new List<long>();
                var avg = new List<double?>();
                var lo = new List<double?>();
                var hi = new List<double?>();
                using (var q = Cmd(c, null, "SELECT (ts / $b) * $b AS t, AVG(v), MIN(v), MAX(v) FROM counters_data WHERE cid = $c AND ts >= $f AND ts <= $to GROUP BY t ORDER BY t",
                           ("$b", bucket), ("$c", id), ("$f", from), ("$to", to)))
                using (var r = q.ExecuteReader())
                {
                    long prev = 0;
                    while (r.Read())
                    {
                        long ts = r.GetInt64(0);
                        // the app was not running (or the counter was not recorded): break the line instead of bridging it
                        long gap = Math.Max(5 * Math.Max(bucket, intervalMs) / 2, ts < rolledTo ? 3 * Minute : 0);
                        if (prev > 0 && ts - prev > gap) { t.Add(prev + bucket); avg.Add(null); lo.Add(null); hi.Add(null); }
                        t.Add(ts); avg.Add(Sig(r.GetDouble(1))); lo.Add(Sig(r.GetDouble(2))); hi.Add(Sig(r.GetDouble(3)));
                        prev = ts;
                    }
                }
                bool rec = recorded.Contains(path.ToLowerInvariant());
                if (!rec && t.Count == 0) continue;
                var parts = CounterPdh.Parse(path);
                series.Add(new { id, path, english, recording = rec, obj = parts?.Object, inst = parts?.Instance, counter = parts?.Counter, t, avg, lo, hi });
            }
        }
        return new
        {
            from, to, bucket, series, recorded = RecordedView(), maxRecorded = MaxRecorded,
            interval = _host.Settings.IntervalSeconds, retentionDays = _host.Settings.RetentionDays,
            fullResolutionDays = _host.Settings.FullResolutionDays,
            dbSize = File.Exists(FeatureDb.DbPath) ? FeatureDb.Instance.FileSize() : 0,
        };
    }

    /// <summary>Drops the stored samples of a counter that is no longer recorded.</summary>
    object Forget(JsonElement p)
    {
        long id = L(p, "id") ?? 0;
        if (id <= 0 || !File.Exists(FeatureDb.DbPath)) return Fail("failed");
        EnsureSchema();
        lock (_recGate)
            if (_rec?.Items.Any(i => i.Id == id) == true) return Fail("recording");
        _host.Db.Write((c, tx) =>
        {
            using (var d = Cmd(c, tx, "DELETE FROM counters_data WHERE cid = $c", ("$c", id))) d.ExecuteNonQuery();
            using (var d = Cmd(c, tx, "DELETE FROM counters_def WHERE id = $c", ("$c", id))) d.ExecuteNonQuery();
        });
        return new { ok = true };
    }

    // ---------------- browsing / checks ----------------

    object Validate(JsonElement p)
    {
        string path = S(p, "path").Trim();
        bool? english = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("english", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        if (path.Length == 0) return Fail("bad-path");
        var probe = CounterPdh.Probe(path, english ?? false);
        // a typed path may be English on a localized Windows: try that before giving up
        if (english is null && !probe.Ok && probe.Error is "no-object" or "no-counter" or "bad-path")
        {
            var en = CounterPdh.Probe(path, true);
            if (en.Ok) { probe = en; english = true; }
        }
        var parts = CounterPdh.Parse(path);
        return new
        {
            ok = probe.Ok, error = probe.Error, detail = probe.Detail, path, english = english ?? false,
            wildcard = CounterPdh.IsWildcard(path), value = probe.Value is { } v ? Sig(v) : (double?)null, instances = probe.Instances,
            obj = parts?.Object, inst = parts?.Instance, counter = parts?.Counter,
        };
    }

    public bool TryQuery(string method, JsonElement p, out object? result)
    {
        result = null;
        if (!method.StartsWith("counters.", StringComparison.Ordinal)) return false;
        switch (method)
        {
            case "counters.presets": result = Presets; break;
            case "counters.objects": result = _browser.Objects(B(p, "refresh"), B(p, "deep")); break;
            case "counters.items": result = _browser.Items(S(p, "object")); break;
            case "counters.explain": result = new { text = CounterPdh.Explain(S(p, "path"), B(p, "english")) }; break;
            case "counters.validate": result = Validate(p); break;
            case "counters.live": result = LiveView(); break;
            case "counters.setLive": result = SetLive(p); break;
            case "counters.setRecorded": result = SetRecorded(p); break;
            case "counters.history": result = History(p); break;
            case "counters.forget": result = Forget(p); break;
            default: return false;
        }
        return true;
    }

    static object Fail(string code, string? detail = null, string? path = null) => new { ok = false, error = code, detail, path };
    static string S(JsonElement p, string name, string def = "") =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;
    static bool B(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static long? L(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}

/// <summary>
/// The counter browser's enumeration cache. Enumerating PDH is slow, so it runs on demand on a background task and
/// stays in memory while the page is in use; five minutes without a request drop it.
/// </summary>
internal sealed class CounterBrowser : IDisposable
{
    const int MaxInstances = 5000;
    static readonly TimeSpan Idle = TimeSpan.FromMinutes(5), ItemsFresh = TimeSpan.FromSeconds(20), Wait = TimeSpan.FromSeconds(12);

    readonly object _gate = new();
    Task<(List<string> Objects, uint Status)>? _objects;
    Task<(Dictionary<string, List<string>> Counters, List<string> Unavailable)>? _index;
    readonly Dictionary<string, (DateTime At, object Result)> _items = new(StringComparer.OrdinalIgnoreCase);
    System.Threading.Timer? _reaper;
    DateTime _lastUse;

    void Use()
    {
        _lastUse = DateTime.UtcNow;
        _reaper ??= new System.Threading.Timer(_ => Reap(), null, 60_000, 60_000);
    }

    void Reap()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastUse < Idle) return;
            _objects = null; _index = null; _items.Clear();
            _reaper?.Dispose(); _reaper = null;
        }
    }

    /// <summary>Object list (and, with deep, every object's counter names for the search box). ready=false: ask again.</summary>
    public object Objects(bool refresh, bool deep)
    {
        Task<(List<string> Objects, uint Status)> objects;
        Task<(Dictionary<string, List<string>> Counters, List<string> Unavailable)>? index = null;
        lock (_gate)
        {
            Use();
            if (refresh) { _objects = null; _index = null; _items.Clear(); }
            objects = _objects ??= Task.Run(() =>
            {
                var (list, st) = CounterPdh.EnumObjects(true);
                return (list.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList(), st);
            });
            if (deep) index = _index ??= objects.ContinueWith(t => BuildIndex(t.Result.Objects), TaskScheduler.Default);
        }
        if (!objects.Wait(Wait)) return new { ready = false };
        var (objs, status) = objects.Result;
        if (objs.Count == 0) return new { ready = true, ok = false, error = CounterPdh.Code(status), detail = CounterPdh.Detail(status), objects = objs };
        if (index is null) return new { ready = true, ok = true, objects = objs };
        if (!index.Wait(Wait)) return new { ready = true, ok = true, objects = objs, indexReady = false };
        return new { ready = true, ok = true, objects = objs, indexReady = true, index = index.Result.Counters, unavailable = index.Result.Unavailable };
    }

    /// <summary>Every object's counter names, plus the objects Windows lists but whose provider is not loaded.</summary>
    static (Dictionary<string, List<string>>, List<string>) BuildIndex(List<string> objects)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unavailable = new List<string>();
        foreach (var o in objects)
        {
            var (counters, _, st) = CounterPdh.EnumItems(o);
            if (st == 0) index[o] = counters.Distinct().ToList();
            else unavailable.Add(o);
        }
        return (index, unavailable);
    }

    /// <summary>Counters and current instances of one object (instances refresh after 20 s: processes come and go).</summary>
    public object Items(string obj)
    {
        lock (_gate)
        {
            Use();
            if (_items.TryGetValue(obj, out var hit) && DateTime.UtcNow - hit.At < ItemsFresh) return hit.Result;
        }
        if (obj.Length == 0) return new { ok = false, error = "no-object" };
        var (counters, instances, st) = CounterPdh.EnumItems(obj);
        object result;
        if (st != 0) result = new { ok = false, error = CounterPdh.Code(st), detail = CounterPdh.Detail(st), @object = obj };
        else
        {
            var cs = counters.Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            List<string>? inst = null;
            if (instances is not null)
            {
                // perfmon numbers repeated names (svchost, svchost#1…), and PDH paths address them the same way
                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                inst = new List<string>(instances.Count);
                foreach (var n in instances)
                {
                    int k = seen.GetValueOrDefault(n);
                    seen[n] = k + 1;
                    inst.Add(k == 0 ? n : $"{n}#{k}");
                }
                inst = inst.OrderBy(n => n == "_Total" ? 0 : 1).ThenBy(n => n, CounterPdh.Natural.Instance).ToList();
            }
            result = new
            {
                ok = true, @object = obj, counters = cs, single = inst is null,
                instances = inst?.Take(MaxInstances).ToList(), totalInstances = inst?.Count ?? 0, truncated = inst?.Count > MaxInstances,
            };
        }
        lock (_gate) _items[obj] = (DateTime.UtcNow, result);
        return result;
    }

    public void Dispose()
    {
        lock (_gate) { _reaper?.Dispose(); _reaper = null; _objects = null; _index = null; _items.Clear(); }
    }
}
