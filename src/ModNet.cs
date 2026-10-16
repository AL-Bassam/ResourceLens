using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

internal static class ModNetInit
{
    [ModuleInitializer]
    internal static void Init() => Features.Register(new NetFeature());
}

/// <summary>network.json</summary>
internal sealed class NetConfig
{
    /// <summary>Monthly data cap in GB (1024³ bytes); 0 = off.</summary>
    public double CapGb { get; set; }
    /// <summary>Balloon when an application goes online for the first time (detection itself always runs).</summary>
    public bool FirstSeenAlerts { get; set; } = true;
    // Cap counter: the month it covers, bytes counted so far and the last sys.ts included. Kept here (not re-summed
    // from history) so a retention shorter than a month cannot shrink the count.
    public string? CapMonth { get; set; }
    public long CapBytes { get; set; }
    public long CapUpTo { get; set; }
    public bool Cap90Sent { get; set; }
    public bool Cap100Sent { get; set; }
    /// <summary>The silent learning period ran to its end (first run / after clearing history); until then every start learns again.</summary>
    public bool Learned { get; set; }
    /// <summary>Learning also ran with per-process byte counts (admin, ETW), which see apps a TCP snapshot misses (UDP only, brief connections).</summary>
    public bool LearnedEtw { get; set; }
}

/// <summary>
/// Network guard (GlassWire-style): live connections with owning apps, data usage per app and per day, "first time
/// online" alerts, firewall blocking and a monthly data cap. In the background it only does set lookups per sample
/// (plus one TCP-table call when per-process byte counts are unavailable); history queries run on page requests.
/// </summary>
internal sealed class NetFeature : IFeature
{
    public string Id => "network";
    const double GB = 1024d * 1024 * 1024;

    FeatureHost _host = null!;
    NetConfig _cfg = new();
    readonly object _cfgLock = new();
    DateTime _cfgSavedAt;

    // ---- first-time-online detection ----
    readonly object _gate = new();
    readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase); // pending last_ts, written hourly
    readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);           // collector thread only
    readonly HashSet<uint> _owners = new();                                              // collector thread only
    readonly ModNetTables.Buffer _sampleBuf = new();                                     // collector thread (+ Dispose, under _sampleLock)
    readonly object _sampleLock = new();
    bool _disposed;
    int _phase; // 0 = loading the baseline, 1 = learning silently (2 minutes), 2 = watching
    long _learnUntil;
    long _nextCapCheck;
    volatile IReadOnlyList<FeatureProc>? _procs;
    volatile bool _perProc;

    // ---- API side ----
    readonly ModNetDns _dns = new();
    readonly ModNetTables.Buffer _apiBuf = new();
    readonly object _apiLock = new();
    readonly Dictionary<string, (long At, object Value)> _cache = new();
    sealed record AppInfo(long Nid, string? Descr, string? Path, bool HasIcon);
    readonly object _namesLock = new();
    Dictionary<string, AppInfo>? _names;
    long _namesAt;

    public void Start(FeatureHost host)
    {
        _host = host;
        _cfg = host.LoadConfig<NetConfig>(Id);
        host.Db.Write((c, tx) => Exec(c, tx, """
            CREATE TABLE IF NOT EXISTS network_seen(name TEXT PRIMARY KEY COLLATE NOCASE, first_ts INTEGER NOT NULL, last_ts INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS network_events(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT, detail TEXT);
            CREATE INDEX IF NOT EXISTS network_events_ts ON network_events(ts);
            """));
        using (var c = host.Db.OpenReader())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT name FROM network_seen";
            using var r = q.ExecuteReader();
            while (r.Read()) _known.Add(r.GetString(0));
        }
        host.Store.Cleared += OnCleared;
        // A learning period cut short (e.g. "Restart as administrator" within the 2 minutes) starts over instead of
        // treating every app it had not met yet as new.
        if (_known.Count > 0) _phase = _cfg.Learned ? 2 : 1;
        else new Thread(SeedFromHistory) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "network baseline" }.Start();
    }

    /// <summary>
    /// First run: every app that history already shows with network traffic counts as known, and the first two
    /// minutes add whatever goes online silently (apps that connect only now and then included), so installing the
    /// module does not fire a balloon per app.
    /// </summary>
    void SeedFromHistory()
    {
        try
        {
            var rows = new List<(string Name, long First, long Last)>();
            using (var c = _host.Store.OpenReader())
            using (var q = c.CreateCommand())
            {
                // nid+0: one pass over the table and a small temp b-tree, instead of walking proc_nid_ts with a lookup per row
                q.CommandText = """
                    SELECT n.name, g.f, g.l FROM (SELECT nid+0 AS k, MIN(ts) AS f, MAX(ts) AS l FROM proc
                        WHERE net_rx > 0 OR net_tx > 0 GROUP BY k) g JOIN names n ON n.id = g.k
                    """;
                using var r = q.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
            }
            if (rows.Count > 0)
                _host.Db.Write((c, tx) =>
                {
                    using var q = Cmd(c, tx, "INSERT OR IGNORE INTO network_seen(name, first_ts, last_ts) VALUES($n, $f, $l)", "$n", "$f", "$l");
                    foreach (var x in rows) { Bind(q, x.Name, x.First, x.Last); q.ExecuteNonQuery(); }
                });
            lock (_gate) foreach (var x in rows) _known.Add(x.Name);
        }
        catch (Exception ex) { Log.Write("feature network: baseline", ex); }
        Interlocked.CompareExchange(ref _phase, 1, 0);
    }

    void OnCleared()
    {
        try { _host.Db.Write((c, tx) => Exec(c, tx, "DELETE FROM network_seen; DELETE FROM network_events;")); }
        catch (Exception ex) { Log.Write("feature network: clear", ex); }
        lock (_gate) { _known.Clear(); _lastSeen.Clear(); }
        lock (_cfgLock) { _cfg.Learned = _cfg.LearnedEtw = false; SaveConfig(); }
        _learnUntil = 0;
        Volatile.Write(ref _phase, 1);
        lock (_cache) _cache.Clear();
    }

    /// <summary>End of a learning period (collector thread): remembered so a restart does not learn again.</summary>
    void FinishLearning(bool etw)
    {
        lock (_cfgLock)
        {
            _cfg.Learned = true;
            if (etw) _cfg.LearnedEtw = true;
            SaveConfig();
        }
    }

    // ---------------- background ----------------

    public void OnSample(FeatureSample s)
    {
        _procs = s.Procs;
        _perProc = s.PerProcessNetwork;
        int phase = Volatile.Read(ref _phase);
        // The first run with per-process counts (admin) meets apps the TCP snapshot never saw (UDP only, brief
        // connections): learn those silently too rather than calling them new.
        if (phase == 2 && s.PerProcessNetwork && !_cfg.LearnedEtw && Interlocked.CompareExchange(ref _phase, 1, 2) == 2)
        {
            _learnUntil = 0;
            phase = 1;
        }
        if (phase != 0)
        {
            bool baseline = phase == 1, alerts = _cfg.FirstSeenAlerts;
            // Without ETW, finding who is online costs a TCP-table call: only spent while the alert is wanted.
            if (s.PerProcessNetwork || alerts || baseline) Detect(s, baseline, alerts);
        }
        if (s.Ts >= _nextCapCheck) CheckCap(s.Ts);
    }

    void Detect(FeatureSample s, bool baseline, bool alerts)
    {
        _online.Clear();
        if (s.PerProcessNetwork)
            foreach (var p in s.Procs)
                if (p.NetRx + p.NetTx > 0 && p.Pid > 4) _online.Add(p.Name);
        if (!s.PerProcessNetwork || baseline)
        {
            _owners.Clear();
            lock (_sampleLock) if (!_disposed) ModNetTables.EstablishedOwners(_sampleBuf, _owners); // Dispose frees the buffer
            if (_owners.Count > 0)
                foreach (var p in s.Procs)
                    if (_owners.Contains(p.Pid)) _online.Add(p.Name);
        }
        if (baseline)
        {
            if (_learnUntil == 0) _learnUntil = s.Ts + 120_000;
            else if (s.Ts >= _learnUntil && Interlocked.CompareExchange(ref _phase, 2, 1) == 1) FinishLearning(s.PerProcessNetwork);
        }
        if (_online.Count == 0) return;

        List<string>? fresh = null;
        lock (_gate)
            foreach (var name in _online)
            {
                _lastSeen[name] = s.Ts;
                if (_known.Add(name)) (fresh ??= new()).Add(name);
            }
        if (fresh is not null) Discovered(fresh, s, baseline, alerts);
    }

    void Discovered(List<string> names, FeatureSample s, bool baseline, bool alerts)
    {
        var paths = new string?[names.Count];
        if (!baseline)
            for (int i = 0; i < names.Count; i++)
                foreach (var p in s.Procs)
                    if (p.Pid > 4 && p.Name.Equals(names[i], StringComparison.OrdinalIgnoreCase) && Native.GetProcessPath(p.Pid) is { } path)
                    { paths[i] = path; break; }
        _host.Db.Write((c, tx) =>
        {
            using var q = Cmd(c, tx, "INSERT OR IGNORE INTO network_seen(name, first_ts, last_ts) VALUES($n, $t, $t)", "$n", "$t");
            foreach (var n in names) { Bind(q, n, s.Ts); q.ExecuteNonQuery(); }
            if (baseline) return;
            using var e = Cmd(c, tx, "INSERT INTO network_events(ts, kind, name, detail) VALUES($t, 'first', $n, $d)", "$t", "$n", "$d");
            for (int i = 0; i < names.Count; i++) { Bind(e, s.Ts, names[i], paths[i]); e.ExecuteNonQuery(); }
        });
        if (baseline || !alerts) return;

        string Disp(string n) => DisplayName(n);
        bool ar = _host.Arabic;
        string list = string.Join(ar ? "، " : ", ", names.Take(3).Select(Disp));
        int more = names.Count - 3;
        string body = names.Count == 1
            ? (ar ? $"{list} اتصل بالإنترنت لأول مرة" : $"{list} connected to the internet for the first time")
            : ar ? $"{list}{(more > 0 ? $" و{more} تطبيقات أخرى" : "")} اتصلت بالإنترنت لأول مرة"
                 : $"{list}{(more > 0 ? $" and {more} more" : "")} connected to the internet for the first time";
        _host.Notify(ar ? "تطبيق جديد على الشبكة" : "New app on the network", body);
    }

    string DisplayName(string name)
    {
        try
        {
            using var c = _host.Store.OpenReader();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT descr FROM names WHERE name=$n";
            q.Parameters.AddWithValue("$n", name);
            if (q.ExecuteScalar() is string d && !string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Every 5 minutes while a cap is set: add the bytes recorded since the last check (a few sys rows).</summary>
    void CheckCap(long ts)
    {
        _nextCapCheck = ts + 300_000;
        string? level = null;
        double pct = 0, capGb = 0;
        long used = 0;
        lock (_cfgLock)
        {
            if (_cfg.CapGb <= 0) return;
            try { used = CapUsage(); } catch (Exception ex) { Log.Write("feature network: cap", ex); return; }
            capGb = _cfg.CapGb;
            pct = used * 100.0 / (capGb * GB);
            if (pct >= 100 && !_cfg.Cap100Sent) { level = "cap100"; _cfg.Cap100Sent = _cfg.Cap90Sent = true; }
            else if (pct >= 90 && !_cfg.Cap90Sent) { level = "cap90"; _cfg.Cap90Sent = true; }
            if (level is not null || DateTime.UtcNow - _cfgSavedAt > TimeSpan.FromMinutes(30)) SaveConfig();
        }
        if (level is null) return;
        bool ar = _host.Arabic;
        string u = (used / GB).ToString("0.#", CultureInfo.InvariantCulture), cap = capGb.ToString("0.#", CultureInfo.InvariantCulture);
        string p = Math.Floor(pct).ToString(CultureInfo.InvariantCulture);
        Event(level, null, $"{used}/{(long)(capGb * GB)}");
        _host.Notify(ar ? "حد البيانات الشهري" : "Monthly data cap",
            ar ? $"استهلكت {p}% من حد البيانات الشهري ({u} من {cap} GB)." : $"You have used {p}% of your monthly data cap ({u} of {cap} GB).");
    }

    /// <summary>Bytes downloaded + uploaded this calendar month (local time). Call under _cfgLock.</summary>
    long CapUsage()
    {
        var now = DateTime.Now;
        string key = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (_cfg.CapMonth != key)
        {
            _cfg.CapMonth = key;
            _cfg.CapBytes = 0;
            _cfg.CapUpTo = Ms(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local)) - 1;
            _cfg.Cap90Sent = _cfg.Cap100Sent = false;
        }
        using var c = _host.Store.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(SUM(net_rx), 0) + COALESCE(SUM(net_tx), 0), MAX(ts) FROM sys WHERE ts > $after";
        q.Parameters.AddWithValue("$after", _cfg.CapUpTo);
        using var r = q.ExecuteReader();
        if (r.Read() && !r.IsDBNull(1)) { _cfg.CapBytes += r.GetInt64(0); _cfg.CapUpTo = r.GetInt64(1); }
        return _cfg.CapBytes;
    }

    public void OnMaintain(long cutMs)
    {
        FlushLastSeen();
        _host.Db.Write((c, tx) => Exec(c, tx, "DELETE FROM network_events WHERE ts < $cut", ("$cut", cutMs)));
    }

    /// <summary>last_ts of every app seen online since the previous flush, in one transaction (hourly and on exit).</summary>
    void FlushLastSeen()
    {
        KeyValuePair<string, long>[] batch;
        lock (_gate)
        {
            if (_lastSeen.Count == 0) return;
            batch = _lastSeen.ToArray();
            _lastSeen.Clear();
        }
        try
        {
            _host.Db.Write((c, tx) =>
            {
                using var q = Cmd(c, tx, "UPDATE network_seen SET last_ts = MAX(last_ts, $t) WHERE name = $n", "$t", "$n");
                foreach (var (n, t) in batch) { Bind(q, t, n); q.ExecuteNonQuery(); }
            });
        }
        catch (Exception ex) { Log.Write("feature network: last seen", ex); }
    }

    void SaveConfig()
    {
        try { _host.SaveConfig(Id, _cfg); _cfgSavedAt = DateTime.UtcNow; }
        catch (Exception ex) { Log.Write("feature network: save config", ex); }
    }

    void Event(string kind, string? name, string? detail)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { _host.Db.Write((c, tx) => Exec(c, tx, "INSERT INTO network_events(ts, kind, name, detail) VALUES($t, $k, $n, $d)", ("$t", ts), ("$k", kind), ("$n", name), ("$d", detail))); }
        catch (Exception ex) { Log.Write("feature network: event", ex); }
    }

    public void Dispose()
    {
        FlushLastSeen();
        lock (_cfgLock) if (_cfg.CapGb > 0) SaveConfig();
        _dns.Dispose();
        lock (_sampleLock) { _disposed = true; _sampleBuf.Dispose(); } // the collector may still be sampling if its join timed out
        lock (_apiLock) _apiBuf.Dispose();
    }

    // ---------------- API ----------------

    public bool TryQuery(string method, JsonElement p, out object? result)
    {
        result = null;
        if (!method.StartsWith("network.", StringComparison.Ordinal)) return false;
        switch (method)
        {
            case "network.summary": result = Summary(); return true;
            case "network.connections": result = Connections(B(p, "established", false), B(p, "loopback", false)); return true;
            case "network.usage": result = Usage(I(p, "days", 1)); return true;
            case "network.activity": result = Activity(I(p, "limit", 60)); return true;
            case "network.blocked": result = Blocked(B(p, "quick", false)); return true;
            case "network.block": result = Block(S(p, "name"), (uint)Math.Max(0, I(p, "pid", 0))); return true;
            case "network.unblock": result = Unblock(S(p, "name")); return true;
            case "network.config": result = ConfigView(); return true;
            case "network.setConfig": result = SetConfig(p); return true;
            default: return false;
        }
    }

    /// <summary>Today, this month and the last 30 days (download / upload) from the system history.</summary>
    object Summary() => Cached("summary", 15_000, () =>
    {
        var now = DateTime.Now;
        DateTime today = now.Date, month = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local), first = today.AddDays(-29);
        DateTime from = first < month ? first : month;
        var days = new Dictionary<DateTime, (long Rx, long Tx)>();
        long tRx = 0, tTx = 0, mRx = 0, mTx = 0, asOf = 0;
        using (var c = _host.Store.OpenReader())
        using (var q = c.CreateCommand())
        {
            // 15-minute buckets map exactly onto local days in every time zone (offsets are multiples of 15 min).
            q.CommandText = "SELECT ts/900000, SUM(net_rx), SUM(net_tx), MAX(ts) FROM sys WHERE ts >= $f GROUP BY ts/900000";
            q.Parameters.AddWithValue("$f", Ms(from));
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                asOf = Math.Max(asOf, r.GetInt64(3));
                if (r.IsDBNull(1) && r.IsDBNull(2)) continue;
                long rx = r.IsDBNull(1) ? 0 : r.GetInt64(1), tx = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                var day = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0) * 900_000).LocalDateTime.Date;
                var d = days.GetValueOrDefault(day);
                days[day] = (d.Rx + rx, d.Tx + tx);
                if (day >= month) { mRx += rx; mTx += tx; }
                if (day == today) { tRx += rx; tTx += tx; }
            }
        }
        double capGb;
        long? capUsed = null;
        lock (_cfgLock)
        {
            capGb = _cfg.CapGb;
            if (capGb > 0) capUsed = Math.Max(CapUsage(), mRx + mTx);
        }
        var list = new List<object>(30);
        for (int i = 0; i < 30; i++)
        {
            var d = first.AddDays(i);
            bool has = days.TryGetValue(d, out var v);
            list.Add(new { ts = Ms(d), rx = has ? v.Rx : (long?)null, tx = has ? v.Tx : (long?)null });
        }
        return new
        {
            asOf, dayStart = Ms(today), monthStart = Ms(month),
            today = new { rx = tRx, tx = tTx }, month = new { rx = mRx, tx = mTx }, days = list,
            cap = capUsed is long used ? new { gb = capGb, used, pct = used * 100.0 / (capGb * GB) } : null,
            retentionDays = _host.Settings.RetentionDays,
        };
    });

    object Connections(bool establishedOnly, bool loopback)
    {
        List<ModNetTables.Conn> all;
        lock (_apiLock) all = ModNetTables.Snapshot(_apiBuf);
        var pidNames = new Dictionary<uint, string?>();
        if (_procs is { } procs) foreach (var p in procs) pidNames[p.Pid] = p.Name;
        string? NameOf(uint pid)
        {
            if (pid == 0) return "System Idle Process";
            if (pidNames.TryGetValue(pid, out var n)) return n;
            n = Native.GetProcessPath(pid) is { } path ? Path.GetFileName(path) : null; // started since the last sample
            pidNames[pid] = n;
            return n;
        }

        var blocked = ModNetFirewall.Cached();
        var info = Names();
        var apps = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>(all.Count);
        int est = 0, listen = 0, udp = 0, total = 0;
        foreach (var r in all)
        {
            bool lo = IPAddress.IsLoopback(r.LocalIp) || (r.RemoteIp is { } ra && IPAddress.IsLoopback(ra));
            if (lo && !loopback) continue;
            total++;
            if (r.State == ModNetTables.Established) est++;
            else if (r.State == ModNetTables.Listen) listen++;
            else if (r.State == 0) udp++;
            if (establishedOnly && r.State != ModNetTables.Established) continue;

            string? name = NameOf(r.Pid);
            var remote = r.RemoteIp is { } ip && !ModNetTables.IsUnspecified(ip) ? ip : null;
            string? host = remote is not null && r.State != ModNetTables.Listen && ModNetDns.Resolvable(remote) ? _dns.Get(remote) : null;
            rows.Add(new
            {
                pid = r.Pid, name, proto = r.Proto,
                local = ModNetTables.Endpoint(r.LocalIp, r.LocalPort),
                remote = remote is null ? null : ModNetTables.Endpoint(remote, r.RemotePort),
                remoteIp = remote?.ToString(), host,
                state = r.State == 0 ? null : ModNetTables.StateName(r.State),
            });
            if (name is not null && !apps.ContainsKey(name))
            {
                info.TryGetValue(name, out var a);
                apps[name] = new
                {
                    nid = a?.Nid, descr = a?.Descr, hasIcon = a?.HasIcon ?? false,
                    blocked = blocked?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false,
                    protectedApp = ModNetFirewall.IsProtected(name),
                };
            }
        }
        return new { rows, apps, counts = new { total, established = est, listening = listen, udp }, admin = Native.IsUserAnAdmin() };
    }

    /// <summary>Bytes per application from the process history (only recorded while running as administrator).</summary>
    object Usage(int days)
    {
        days = days is 7 or 30 ? days : 1;
        return Cached($"usage{days}", 60_000, () =>
        {
            long from = Ms(DateTime.Now.Date.AddDays(1 - days));
            var apps = new List<object>();
            bool perApp = false;
            long? sysRx = null, sysTx = null;
            using var c = _host.Store.OpenReader();
            using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT g.k, n.name, n.descr, n.path, n.icon IS NOT NULL, g.rx, g.tx, g.c FROM
                        (SELECT nid+0 AS k, SUM(net_rx) AS rx, SUM(net_tx) AS tx, COUNT(net_rx) AS c FROM proc WHERE ts >= $f GROUP BY k) g
                    JOIN names n ON n.id = g.k
                    ORDER BY COALESCE(g.rx, 0) + COALESCE(g.tx, 0) DESC
                    """;
                q.Parameters.AddWithValue("$f", from);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetInt64(7) > 0) perApp = true;
                    long rx = r.IsDBNull(5) ? 0 : r.GetInt64(5), tx = r.IsDBNull(6) ? 0 : r.GetInt64(6);
                    if (rx + tx <= 0 || apps.Count >= 100) continue;
                    string name = r.GetString(1);
                    apps.Add(new
                    {
                        nid = r.GetInt64(0), name, descr = r.IsDBNull(2) ? null : r.GetString(2), path = r.IsDBNull(3) ? null : r.GetString(3),
                        hasIcon = r.GetBoolean(4), rx, tx, protectedApp = ModNetFirewall.IsProtected(name),
                    });
                }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT SUM(net_rx), SUM(net_tx) FROM sys WHERE ts >= $f";
                q.Parameters.AddWithValue("$f", from);
                using var r = q.ExecuteReader();
                if (r.Read()) { sysRx = r.IsDBNull(0) ? null : r.GetInt64(0); sysTx = r.IsDBNull(1) ? null : r.GetInt64(1); }
            }
            return new { days, from, perApp, apps, totalRx = sysRx, totalTx = sysTx };
        });
    }

    /// <summary>Recent first-time-online apps (and the module's own block / cap events).</summary>
    object Activity(int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        var info = Names();
        var events = new List<object>();
        long known;
        using (var c = _host.Db.OpenReader())
        {
            using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT e.id, e.ts, e.kind, e.name, e.detail, s.first_ts, s.last_ts FROM network_events e
                    LEFT JOIN network_seen s ON s.name = e.name ORDER BY e.ts DESC, e.id DESC LIMIT $l
                    """;
                q.Parameters.AddWithValue("$l", limit);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    string? name = r.IsDBNull(3) ? null : r.GetString(3);
                    long? last = r.IsDBNull(6) ? null : r.GetInt64(6);
                    if (name is not null) lock (_gate) if (_lastSeen.TryGetValue(name, out long pending) && pending > (last ?? 0)) last = pending;
                    AppInfo? a = null;
                    if (name is not null) info.TryGetValue(name, out a);
                    events.Add(new
                    {
                        id = r.GetInt64(0), ts = r.GetInt64(1), kind = r.GetString(2), name, detail = r.IsDBNull(4) ? null : r.GetString(4),
                        firstTs = r.IsDBNull(5) ? (long?)null : r.GetInt64(5), lastTs = last,
                        nid = a?.Nid, descr = a?.Descr, hasIcon = a?.HasIcon ?? false,
                        protectedApp = name is null || ModNetFirewall.IsProtected(name),
                    });
                }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT COUNT(*) FROM network_seen";
                known = (long)q.ExecuteScalar()!;
            }
        }
        bool alerts = _cfg.FirstSeenAlerts, perProc = _perProc;
        return new { events, known, alerts, perProcess = perProc, watching = perProc || alerts, learning = Volatile.Read(ref _phase) != 2 };
    }

    object Blocked(bool quick)
    {
        var list = quick ? ModNetFirewall.Cached() : ModNetFirewall.List();
        bool admin = Native.IsUserAnAdmin();
        if (list is null) return new { loaded = false, admin, rules = new List<object>() };
        var info = Names();
        return new
        {
            loaded = true, admin,
            rules = list.Select(n =>
            {
                info.TryGetValue(n, out var a);
                return (object)new { name = n, nid = a?.Nid, descr = a?.Descr, hasIcon = a?.HasIcon ?? false, path = a?.Path };
            }).ToList(),
        };
    }

    static object Fail(string code, string? detail = null) => new { ok = false, error = code, detail };

    object Block(string name, uint pid)
    {
        if (!Native.IsUserAnAdmin()) return Fail("access");
        if (name.Length == 0 && pid > 4 && Native.GetProcessPath(pid) is { } own) name = Path.GetFileName(own);
        if (name.Length == 0) return Fail("no-path");
        if (ModNetFirewall.IsProtected(name)) return Fail("critical", name);
        string? path = ExePath(name, pid);
        if (path is null) return Fail("no-path", name);
        var res = ModNetFirewall.Block(name, path);
        if (!res.Ok) return Fail(res.Error ?? "failed", res.Detail);
        Event("block", name, path);
        return new { ok = true, name, path };
    }

    object Unblock(string name)
    {
        if (!Native.IsUserAnAdmin()) return Fail("access");
        var res = ModNetFirewall.Unblock(name);
        if (!res.Ok) return Fail(res.Error ?? "failed", res.Detail);
        Event("unblock", name, null);
        return new { ok = true, name };
    }

    /// <summary>The running copy's path (exactly what is using the network), else the one history last saw.</summary>
    string? ExePath(string name, uint pid)
    {
        bool Match(string? p) => p is not null && Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase);
        if (pid > 4 && Native.GetProcessPath(pid) is { } a && Match(a)) return a;
        if (_procs is { } procs)
            foreach (var p in procs)
                if (p.Pid > 4 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Native.GetProcessPath(p.Pid) is { } b && Match(b)) return b;
        return Names(true).TryGetValue(name, out var i) && Match(i.Path) && File.Exists(i.Path) ? i.Path : null;
    }

    object ConfigView()
    {
        lock (_cfgLock) return new { capGb = _cfg.CapGb, firstSeenAlerts = _cfg.FirstSeenAlerts };
    }

    object SetConfig(JsonElement p)
    {
        lock (_cfgLock)
        {
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("capGb", out var cv) && cv.ValueKind == JsonValueKind.Number)
            {
                double cap = Math.Clamp(Math.Round(cv.GetDouble(), 1), 0, 1_000_000);
                if (cap != _cfg.CapGb)
                {
                    _cfg.CapGb = cap;
                    _cfg.Cap90Sent = _cfg.Cap100Sent = false; // a new cap gets its own 90% / 100% balloons
                    _nextCapCheck = 0;
                }
            }
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("firstSeenAlerts", out var fv) && fv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _cfg.FirstSeenAlerts = fv.GetBoolean();
            SaveConfig();
        }
        lock (_cache) _cache.Remove("summary");
        return ConfigView();
    }

    // ---------------- helpers ----------------

    /// <summary>names table of history.db by exe name (id for the icon, description, path), refreshed each minute.</summary>
    Dictionary<string, AppInfo> Names(bool fresh = false)
    {
        lock (_namesLock)
        {
            long now = Environment.TickCount64;
            if (!fresh && _names is not null && now - _namesAt < 60_000) return _names;
            var map = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            using (var c = _host.Store.OpenReader())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id, name, descr, path, icon IS NOT NULL FROM names";
                using var r = q.ExecuteReader();
                while (r.Read())
                    map[r.GetString(1)] = new AppInfo(r.GetInt64(0), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4));
            }
            _names = map;
            _namesAt = now;
            return map;
        }
    }

    object Cached(string key, int ms, Func<object> make)
    {
        long now = Environment.TickCount64;
        lock (_cache) if (_cache.TryGetValue(key, out var v) && now - v.At < ms) return v.Value;
        var value = make();
        lock (_cache) _cache[key] = (now, value);
        return value;
    }

    static long Ms(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    static bool B(JsonElement p, string name, bool def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    static int I(JsonElement p, string name, int def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? x : def;
    static string S(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        foreach (var (n, v) in ps) q.Parameters.AddWithValue(n, v ?? DBNull.Value);
        q.ExecuteNonQuery();
    }

    static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params string[] names)
    {
        var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        foreach (var n in names) q.Parameters.Add(new SqliteParameter(n, null));
        return q;
    }

    static void Bind(SqliteCommand q, params object?[] values)
    {
        for (int i = 0; i < values.Length; i++) q.Parameters[i].Value = values[i] ?? DBNull.Value;
    }
}
