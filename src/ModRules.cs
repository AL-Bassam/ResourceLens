using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

internal static class ModRulesInit
{
    [ModuleInitializer]
    internal static void Init() => Features.Register(new RulesFeature());
}

/// <summary>rules.json: the user's per-application rules, ProBalance settings and a pending power plan restore.</summary>
internal sealed class RulesConfig
{
    public List<ProcRule> Rules { get; set; } = new();
    public ProBalanceConfig ProBalance { get; set; } = new();
    /// <summary>Kept on disk while a rule's plan is active, so a crash or kill still gets the user's plan back.</summary>
    public string? SwitchedPlan { get; set; }
    public string? RestorePlan { get; set; }
}

/// <summary>One application's rule (matched by exe name, case-insensitive). Null fields mean "leave as it is".</summary>
internal sealed class ProcRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Priority { get; set; }   // idle | below | normal | above | high (never realtime)
    public List<int>? Cores { get; set; }   // logical processors (affinity)
    public bool? Efficiency { get; set; }   // EcoQoS on / off
    public string? Io { get; set; }         // low | normal
    public bool Disallowed { get; set; }    // closed as soon as it starts
    public int? Limit { get; set; }         // max instances; newer ones are closed
    public string? PowerPlan { get; set; }  // scheme GUID while the app runs
    public long Created { get; set; }
    public long Updated { get; set; }

    public ProcRule Copy() => (ProcRule)MemberwiseClone();
}

internal sealed class ProBalanceConfig
{
    public bool Enabled { get; set; }
    public double SystemThreshold { get; set; } = 85;
    public double ProcessThreshold { get; set; } = 10;
    public string Restrain { get; set; } = "below"; // below | idle
    public List<string> Exclusions { get; set; } = new();
}

/// <summary>
/// Process control (Process Lasso-style): persistent per-application rules applied once to every new process,
/// ProBalance, and "power plan while this app runs". Everything that changes the system comes from something the user
/// made (a rule, the ProBalance toggle, a click) and core Windows processes are never touched. Per sample this is one
/// pass over the process list with hash lookups; OS calls happen only for new processes that have a rule.
/// </summary>
internal sealed class RulesFeature : IFeature
{
    public string Id => "rules";

    /// <summary>Never touched by rules or ProBalance (compared without ".exe").</summary>
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "LsaIso", "svchost", "dwm", "fontdrvhost", "MsMpEng", "audiodg", "ResourceLens",
    };

    /// <summary>The shell and input stack: rules may tune them, but never close them (disallowed / instance limit).</summary>
    static readonly HashSet<string> NoKill = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "taskhostw.exe", "Taskmgr.exe", "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe", "TextInputHost.exe", "SearchHost.exe", "RuntimeBroker.exe", "LogonUI.exe", "userinit.exe",
    };

    static readonly string SelfName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ResourceLens");
    static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly object Unknown = new();

    static bool IsExcluded(string name)
    {
        string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return Excluded.Contains(bare) || bare.Equals(SelfName, StringComparison.OrdinalIgnoreCase);
    }

    FeatureHost _host = null!;
    readonly object _gate = new();
    RulesConfig _cfg = new();
    Dictionary<string, ProcRule> _rules = new(StringComparer.OrdinalIgnoreCase);   // enabled rules by exe name
    Dictionary<string, int> _planOrder = new(StringComparer.OrdinalIgnoreCase);    // apps with a power plan -> rule order
    List<ProcRule> _planRules = new();
    HashSet<string> _pbExcl = new(StringComparer.OrdinalIgnoreCase);
    HashSet<(uint, long)> _seen = new(), _seenNext = new();                        // processes rules were already applied to
    IReadOnlyList<FeatureProc>? _last;
    readonly ProBalance _pb = new(new BalanceOs());
    readonly Func<string, bool> _pbSkip;
    readonly Dictionary<string, Stats> _stats = new();
    string? _planRule;       // app whose rule switched the plan ("" = from a previous run)
    Guid? _planTarget, _planRestore;
    bool _disposed;

    public RulesFeature() => _pbSkip = SkipBalance;

    sealed class Tally { public int Matched, Applied, Denied, Failed, Killed, Limited; public uint Pid; }
    sealed class Stats { public int Applied, Denied, Failed, Killed; public long Last; }
    sealed record NameRow(long Nid, string? Descr, bool Icon);

    sealed class BalanceOs : ProBalance.IOs
    {
        public RulesOs.Result Restrain(uint pid, long created, uint target, out uint orig) => RulesOs.Restrain(pid, created, target, out orig);
        public RulesOs.Result Restore(uint pid, long created, uint target, uint orig) => RulesOs.Restore(pid, created, target, orig);
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string J(object o) => JsonSerializer.Serialize(o);
    static object Fail(string code, string? detail = null) => new { ok = false, error = code, detail };

    // ---------------- lifecycle ----------------

    public void Start(FeatureHost host)
    {
        _host = host;
        var cfg = host.LoadConfig<RulesConfig>(Id);
        cfg.Rules = (cfg.Rules ?? new()).Where(r => r is not null && Clean(r) is null)
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        cfg.ProBalance = Clean(cfg.ProBalance ?? new());
        lock (_gate)
        {
            _cfg = cfg;
            if (Guid.TryParse(cfg.SwitchedPlan, out var to) && Guid.TryParse(cfg.RestorePlan, out var back) && to != back)
            {
                _planRule = ""; _planTarget = to; _planRestore = back;
            }
            Compile();
        }
        host.Db.Write((c, tx) => Exec(c, tx, """
            CREATE TABLE IF NOT EXISTS rules_events(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT,
                pid INTEGER, detail TEXT);
            """));
        host.Store.Cleared += () =>
        {
            try { host.Db.Write((c, tx) => Exec(c, tx, "DELETE FROM rules_events;")); }
            catch (Exception ex) { Log.Write("rules: clear", ex); }
        };
    }

    public void OnSample(FeatureSample s)
    {
        List<RuleEvent>? ev = null;
        lock (_gate)
        {
            if (_disposed) return;
            _last = s.Procs;
            if (_rules.Count > 0) ApplyNew(s, ref ev);
            else if (_seen.Count > 0) _seen.Clear();
            _pb.Sample(s, _cfg.ProBalance, _pbSkip, ref ev);
            if (_planOrder.Count > 0 || _planRule is not null) PlanStep(s.Procs, s.Ts, ref ev);
        }
        if (ev is not null) WriteEvents(ev);
    }

    public void OnMaintain(long cutMs)
    {
        _host.Db.Write((c, tx) =>
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "DELETE FROM rules_events WHERE ts < $cut; DELETE FROM rules_events WHERE id <= (SELECT MAX(id) FROM rules_events) - 2000;";
            q.Parameters.AddWithValue("$cut", cutMs);
            q.ExecuteNonQuery();
        });
        lock (_gate)
            foreach (var id in _stats.Keys.Where(id => !_cfg.Rules.Exists(r => r.Id == id)).ToList()) _stats.Remove(id);
    }

    /// <summary>ProBalance and a rule's power plan are temporary: both go back to what the user had.</summary>
    public void Dispose()
    {
        List<RuleEvent>? ev = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pb.RestoreAll(Now, "exit", ref ev);
            if (_planRule is not null) RestorePlan(Now, ref ev);
        }
        if (ev is not null) WriteEvents(ev);
    }

    // ---------------- rules ----------------

    void Compile()
    {
        var map = new Dictionary<string, ProcRule>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<ProcRule>();
        foreach (var r in _cfg.Rules)
        {
            if (!r.Enabled) continue;
            map[r.Name] = r;
            if (r.PowerPlan is not null && !r.Disallowed) { order[r.Name] = plans.Count; plans.Add(r); }
        }
        _rules = map;
        _planOrder = order;
        _planRules = plans;
        _pbExcl = new HashSet<string>(_cfg.ProBalance.Exclusions, StringComparer.OrdinalIgnoreCase);
    }

    bool SkipBalance(string name)
    {
#if DEBUG
        if (_testOnly is { } only && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) return true;
#endif
        return IsExcluded(name) || _pbExcl.Contains(name)
            || _rules.TryGetValue(name, out var r) && (r.Priority is not null || r.Efficiency == true || r.Disallowed);
    }

#if DEBUG
    // Test hook: live ProBalance tests look at one throwaway exe only, so nothing else on the machine is ever touched.
    volatile string? _testOnly;
#endif

    /// <summary>Applies the rules once to each process that was not in the previous sample.</summary>
    void ApplyNew(FeatureSample s, ref List<RuleEvent>? ev)
    {
        var next = _seenNext;
        next.Clear();
        Dictionary<ProcRule, Tally>? hits = null;
        foreach (var p in s.Procs)
        {
            var key = (p.Pid, p.Created);
            next.Add(key);
            if (_seen.Contains(key) || !_rules.TryGetValue(p.Name, out var r)) continue;
            hits ??= new(ReferenceEqualityComparer.Instance);
            if (!hits.TryGetValue(r, out var t)) hits[r] = t = new Tally();
            t.Matched++;
            ApplyOne(r, p, t);
        }
        _seenNext = _seen;
        _seen = next;
        if (hits is null) return;
        foreach (var (r, t) in hits)
        {
            if (r.Limit is int max && !r.Disallowed) EnforceLimit(r, max, s.Procs, t);
            Report(r, t, s.Ts, ref ev);
        }
    }

    void ApplyOne(ProcRule r, FeatureProc p, Tally t)
    {
        if (p.Pid <= 4 || IsExcluded(p.Name)) return;
        t.Pid = t.Pid == 0 ? p.Pid : t.Pid;
        if (r.Disallowed)
        {
            if (!NoKill.Contains(r.Name)) Count(t, RulesOs.Kill(p.Pid, p.Created), kill: true);
            return;
        }
        // Efficiency mode the way Task Manager does it: EcoQoS plus low priority, unless the rule picks a priority.
        uint? cls = RulesOs.PriorityClass(r.Priority) ?? (r.Efficiency == true ? RulesOs.IdleClass : null);
        ulong? mask = r.Cores is { Count: > 0 } ? Mask(r.Cores) : null;
        int? io = r.Io switch { "low" => 1, "normal" => 2, _ => null };
        if (cls is null && mask is null && r.Efficiency is null && io is null) return;
        var res = RulesOs.Apply(p.Pid, p.Created, cls, mask, r.Efficiency, io);
        if (cls is not null && res != RulesOs.Result.Gone) _pb.Forget(p.Pid, p.Created);
        Count(t, res, kill: false);
    }

    static void Count(Tally t, RulesOs.Result res, bool kill)
    {
        switch (res)
        {
            case RulesOs.Result.Ok: if (kill) t.Killed++; else t.Applied++; break;
            case RulesOs.Result.Denied: t.Denied++; break;
            case RulesOs.Result.Failed: t.Failed++; break;
        }
    }

    static ulong Mask(List<int> cores)
    {
        ulong m = 0;
        foreach (int i in cores) if (i is >= 0 and < 64) m |= 1UL << i;
        return m;
    }

    /// <summary>Instance limit: the oldest <paramref name="max"/> processes stay, the newer ones are closed, newest first.</summary>
    static void EnforceLimit(ProcRule r, int max, IReadOnlyList<FeatureProc> procs, Tally t)
    {
        if (NoKill.Contains(r.Name) || IsExcluded(r.Name)) return;
        var same = new List<FeatureProc>();
        foreach (var p in procs) if (p.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) same.Add(p);
        if (same.Count <= max) return;
        same.Sort((a, b) => b.Created.CompareTo(a.Created));
        for (int i = 0; i < same.Count - max; i++)
            switch (RulesOs.Kill(same[i].Pid, same[i].Created))
            {
                case RulesOs.Result.Ok: t.Limited++; break;
                case RulesOs.Result.Denied: t.Denied++; break;
                case RulesOs.Result.Failed: t.Failed++; break;
            }
    }

    /// <summary>Counters for the rules table and one Activity line per rule and sample (not one per process).</summary>
    void Report(ProcRule r, Tally t, long ts, ref List<RuleEvent>? ev)
    {
        if (t.Applied + t.Denied + t.Failed + t.Killed + t.Limited == 0) return;
        if (!_stats.TryGetValue(r.Id, out var st)) _stats[r.Id] = st = new Stats();
        st.Applied += t.Applied; st.Denied += t.Denied; st.Failed += t.Failed; st.Killed += t.Killed + t.Limited; st.Last = ts;
        ev ??= new();
        uint pid = t.Matched == 1 ? t.Pid : 0;
        if (t.Killed > 0) ev.Add(new(ts, "kill", r.Name, pid, J(new { n = t.Killed })));
        if (t.Limited > 0) ev.Add(new(ts, "limit", r.Name, 0, J(new { n = t.Limited, limit = r.Limit })));
        if (t.Applied > 0) ev.Add(new(ts, "rule", r.Name, pid, J(new { n = t.Applied })));
        if (t.Denied + t.Failed > 0) ev.Add(new(ts, "denied", r.Name, pid, J(new { n = t.Denied, failed = t.Failed })));
    }

    /// <summary>After a rule is saved or switched on: apply it to its running processes now (from the last sample).</summary>
    Tally ApplyRunning(ProcRule r, long ts, ref List<RuleEvent>? ev)
    {
        var t = new Tally();
        if (!r.Enabled || _last is null) return t;
        foreach (var p in _last)
        {
            if (!p.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) continue;
            t.Matched++;
            _seen.Add((p.Pid, p.Created));
            ApplyOne(r, p, t);
        }
        if (r.Limit is int max && !r.Disallowed && t.Matched > max) EnforceLimit(r, max, _last, t);
        Report(r, t, ts, ref ev);
        return t;
    }

    // ---------------- power plan while an app runs ----------------

    void PlanStep(IReadOnlyList<FeatureProc> procs, long ts, ref List<RuleEvent>? ev)
    {
        int best = int.MaxValue;
        if (_planOrder.Count > 0)
            foreach (var p in procs)
                if (_planOrder.TryGetValue(p.Name, out int i) && i < best) best = i;
        var want = best < _planRules.Count ? _planRules[best] : null;
        if (want is not null && Guid.TryParse(want.PowerPlan, out var g))
        {
            if (_planTarget == g && string.Equals(_planRule, want.Name, StringComparison.OrdinalIgnoreCase)) return;
            var cur = RulesOs.ActivePlan();
            if (_planRule is null) _planRestore = cur;
            _planRule = want.Name;
            _planTarget = g;
            if (cur != g)
            {
                uint rc = RulesOs.SetPlan(g);
                (ev ??= new()).Add(new(ts, "plan", want.Name, 0, J(new
                {
                    to = g, toName = RulesOs.PlanName(g), from = cur, fromName = cur is Guid f ? RulesOs.PlanName(f) : null, ok = rc == 0, error = rc,
                })));
            }
            SavePlanState();
        }
        else if (_planRule is not null) RestorePlan(ts, ref ev);
    }

    /// <summary>Back to the plan from before, unless the user picked another one meanwhile (theirs stays).</summary>
    void RestorePlan(long ts, ref List<RuleEvent>? ev)
    {
        if (_planRestore is Guid back && RulesOs.ActivePlan() is Guid cur && cur == _planTarget && cur != back)
        {
            uint rc = RulesOs.SetPlan(back);
            (ev ??= new()).Add(new(ts, "planRestore", _planRule is { Length: > 0 } n ? n : null, 0,
                J(new { to = back, toName = RulesOs.PlanName(back), ok = rc == 0, error = rc })));
        }
        _planRule = null;
        _planTarget = null;
        _planRestore = null;
        SavePlanState();
    }

    void SavePlanState()
    {
        string? to = _planRestore is not null && _planRestore != _planTarget ? _planTarget?.ToString() : null;
        string? back = to is null ? null : _planRestore?.ToString();
        if (_cfg.SwitchedPlan == to && _cfg.RestorePlan == back) return;
        _cfg.SwitchedPlan = to;
        _cfg.RestorePlan = back;
        SaveCfg();
    }

    void SaveCfg()
    {
        try { _host.SaveConfig(Id, _cfg); }
        catch (Exception ex) { Log.Write("rules: save config", ex); }
    }

    // ---------------- events (features.db) ----------------

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        q.ExecuteNonQuery();
    }

    void WriteEvents(List<RuleEvent> ev)
    {
        try
        {
            _host.Db.Write((c, tx) =>
            {
                using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "INSERT INTO rules_events(ts, kind, name, pid, detail) VALUES($t, $k, $n, $p, $d)";
                var pt = q.Parameters.Add("$t", SqliteType.Integer);
                var pk = q.Parameters.Add("$k", SqliteType.Text);
                var pn = q.Parameters.Add("$n", SqliteType.Text);
                var pp = q.Parameters.Add("$p", SqliteType.Integer);
                var pd = q.Parameters.Add("$d", SqliteType.Text);
                foreach (var e in ev)
                {
                    pt.Value = e.Ts; pk.Value = e.Kind; pn.Value = (object?)e.Name ?? DBNull.Value;
                    pp.Value = e.Pid == 0 ? DBNull.Value : e.Pid; pd.Value = (object?)e.Detail ?? DBNull.Value;
                    q.ExecuteNonQuery();
                }
                q.Parameters.Clear();
                q.CommandText = "DELETE FROM rules_events WHERE id <= (SELECT MAX(id) FROM rules_events) - 2000";
                q.ExecuteNonQuery();
            });
        }
        catch (Exception ex) { Log.Write("rules: events", ex); }
    }

    // ---------------- validation ----------------

    /// <summary>"Chrome", "chrome.exe" or a full path all become the exe name the sampler reports.</summary>
    static string? NormName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string n = raw.Trim().Trim('"');
        int slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) n = n[(slash + 1)..];
        if (n.Length is 0 or > 200 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return n.Contains('.') ? n : n + ".exe";
    }

    /// <summary>Normalizes a rule in place; returns an error code for the dashboard instead of throwing.</summary>
    static string? Clean(ProcRule r)
    {
        var n = NormName(r.Name);
        if (n is null) return "name";
        if (IsExcluded(n) || IsExcluded(r.Name.Trim())) return "critical";
        r.Name = n;
        if (RulesOs.PriorityClass(r.Priority) is null) r.Priority = null;
        if (r.Io is not ("low" or "normal")) r.Io = null;
        if (r.Cores is not null)
        {
            int max = Math.Min(64, Environment.ProcessorCount);
            r.Cores = r.Cores.Where(i => i >= 0 && i < max).Distinct().Order().ToList();
            if (r.Cores.Count == 0) return "cores";
        }
        if (r.Limit is int l) r.Limit = l < 1 ? null : Math.Min(l, 999);
        if (r.PowerPlan is not null) r.PowerPlan = Guid.TryParse(r.PowerPlan, out var g) ? g.ToString() : null;
        if ((r.Disallowed || r.Limit is not null) && NoKill.Contains(r.Name)) return "nokill";
        if (string.IsNullOrEmpty(r.Id)) r.Id = Guid.NewGuid().ToString("N")[..12];
        return null;
    }

    static ProBalanceConfig Clean(ProBalanceConfig c) => new()
    {
        Enabled = c.Enabled,
        SystemThreshold = Math.Round(Math.Clamp(double.IsFinite(c.SystemThreshold) ? c.SystemThreshold : 85, 30, 100)),
        ProcessThreshold = Math.Round(Math.Clamp(double.IsFinite(c.ProcessThreshold) ? c.ProcessThreshold : 10, 1, 100)),
        Restrain = c.Restrain == "idle" ? "idle" : "below",
        Exclusions = (c.Exclusions ?? new()).Select(NormName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList(),
    };

    // ---------------- API ----------------

    static string? S(JsonElement p, string n) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int I(JsonElement p, string n, int def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? x : def;
    static bool B(JsonElement p, string n) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    public bool TryQuery(string method, JsonElement p, out object? result)
    {
        result = null;
        if (!method.StartsWith("rules.", StringComparison.Ordinal)) return false;
        result = method switch
        {
            "rules.list" => List(),
            "rules.apps" => Apps(),
            "rules.save" => Save(p),
            "rules.delete" => Delete(S(p, "id")),
            "rules.setEnabled" => SetEnabled(S(p, "id"), B(p, "enabled")),
            "rules.apply" => SetEnabled(S(p, "id"), null),
            "rules.probalance.get" => BalanceInfo(),
            "rules.probalance.set" => SetBalance(p),
            "rules.events" => Events(I(p, "limit", 200), S(p, "group")),
            "rules.clearEvents" => ClearEvents(),
            "rules.powerPlans" => PowerPlans(),
            "rules.setPowerPlan" => SetPowerPlan(S(p, "guid")),
            "rules.cores" => Cores(),
#if DEBUG
            // test hooks
            "rules.debug.selftest" => ProBalance.SelfTest(),
            "rules.debug.inspect" => RulesOs.Inspect((uint)I(p, "pid", 0)),
            "rules.debug.only" => new { ok = true, only = _testOnly = NormName(S(p, "name")) },
#endif
            _ => Unknown,
        };
        if (!ReferenceEquals(result, Unknown)) return true;
        result = null;
        return false;
    }

    /// <summary>Icons and descriptions come from history.db's names table (what the rest of the dashboard shows).</summary>
    Dictionary<string, NameRow> NameInfo(IEnumerable<string> names)
    {
        var map = new Dictionary<string, NameRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = _host.Store.OpenReader();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT id, descr, icon IS NOT NULL FROM names WHERE name=$n";
            var pn = q.Parameters.Add("$n", SqliteType.Text);
            foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                pn.Value = n;
                using var r = q.ExecuteReader();
                if (r.Read()) map[n] = new NameRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2) != 0);
            }
        }
        catch (Exception ex) { Log.Write("rules: names", ex); }
        return map;
    }

    object List()
    {
        List<(ProcRule Rule, int Running, Stats? Stats)> rows;
        lock (_gate)
        {
            var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _cfg.Rules) count[r.Name] = 0;
            if (_last is not null)
                foreach (var p in _last)
                    if (count.TryGetValue(p.Name, out int c)) count[p.Name] = c + 1;
            rows = _cfg.Rules.Select(r => (r.Copy(), count[r.Name],
                _stats.TryGetValue(r.Id, out var st) ? new Stats { Applied = st.Applied, Denied = st.Denied, Failed = st.Failed, Killed = st.Killed, Last = st.Last } : null)).ToList();
        }
        var info = NameInfo(rows.Select(x => x.Rule.Name));
        return new
        {
            rules = rows.Select(x =>
            {
                var r = x.Rule;
                info.TryGetValue(r.Name, out var ni);
                return new
                {
                    r.Id, r.Name, r.Enabled, r.Priority, r.Cores, r.Efficiency, r.Io, r.Disallowed, r.Limit, r.PowerPlan, r.Created, r.Updated,
                    nid = ni?.Nid, descr = ni?.Descr, hasIcon = ni?.Icon ?? false, running = x.Running,
                    stats = x.Stats is { } s ? new { s.Applied, s.Denied, s.Failed, s.Killed, s.Last } : null,
                };
            }).ToList(),
            isAdmin = Native.IsUserAnAdmin(),
            excluded = Excluded.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            noKill = NoKill.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Running applications for the "Add rule" picker: one row per exe name.</summary>
    object Apps()
    {
        var agg = new Dictionary<string, (int Count, double Cpu, long Mem)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            if (_last is not null)
                foreach (var p in _last)
                {
                    if (p.Pid <= 4) continue;
                    var a = agg.GetValueOrDefault(p.Name);
                    agg[p.Name] = (a.Count + 1, a.Cpu + p.Cpu, a.Mem + p.Mem);
                }
        foreach (var n in agg.Keys.Where(IsExcluded).ToList()) agg.Remove(n);
        var info = NameInfo(agg.Keys);
        return agg.OrderByDescending(x => x.Value.Cpu).ThenByDescending(x => x.Value.Mem).Select(x =>
        {
            info.TryGetValue(x.Key, out var ni);
            return new
            {
                name = x.Key, count = x.Value.Count, cpu = Math.Round(x.Value.Cpu, 2), mem = x.Value.Mem,
                nid = ni?.Nid, descr = ni?.Descr, hasIcon = ni?.Icon ?? false,
            };
        }).ToList();
    }

    object Save(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("rule", out var jr) || jr.ValueKind != JsonValueKind.Object) return Fail("name");
        ProcRule input;
        try { input = jr.Deserialize<ProcRule>(ReadJson) ?? new(); }
        catch (JsonException ex) { return Fail("failed", ex.Message); }
        List<RuleEvent>? ev = null;
        object res;
        lock (_gate)
        {
            if (_disposed) return Fail("failed");
            string? n = NormName(input.Name);
            var existing = (string.IsNullOrEmpty(input.Id) ? null : _cfg.Rules.Find(x => x.Id == input.Id))
                ?? (n is null ? null : _cfg.Rules.Find(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var r = input;
            if (B(p, "merge") && existing is not null)
            {
                // Quick actions send only what they change; everything else stays as the rule had it.
                var node = JsonSerializer.SerializeToNode(existing, CamelJson)!.AsObject();
                foreach (var prop in jr.EnumerateObject()) node[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                r = node.Deserialize<ProcRule>(ReadJson) ?? input;
            }
            r.Id = existing?.Id ?? "";
            r.Created = existing?.Created ?? Now;
            r.Updated = Now;
            if (Clean(r) is { } err) return Fail(err);
            // A rule renamed onto another app's name replaces that rule.
            _cfg.Rules.RemoveAll(x => !ReferenceEquals(x, existing) && x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            int at = _cfg.Rules.FindIndex(x => ReferenceEquals(x, existing));
            if (at >= 0) _cfg.Rules[at] = r; else _cfg.Rules.Add(r);
            SaveCfg();
            Compile();
            _stats.Remove(r.Id);
            var t = ApplyRunning(r, Now, ref ev);
            res = new { ok = true, id = r.Id, name = r.Name, matched = t.Matched, applied = t.Applied, denied = t.Denied, failed = t.Failed, killed = t.Killed + t.Limited };
        }
        if (ev is not null) WriteEvents(ev);
        return res;
    }

    object Delete(string? id)
    {
        lock (_gate)
        {
            if (_cfg.Rules.RemoveAll(r => r.Id == id) == 0) return Fail("not-found");
            _stats.Remove(id!);
            SaveCfg();
            Compile(); // a power plan this rule switched to is restored on the next sample
        }
        return new { ok = true };
    }

    /// <summary>Switch a rule on/off, or (enabled == null) re-apply it to its running processes now.</summary>
    object SetEnabled(string? id, bool? enabled)
    {
        List<RuleEvent>? ev = null;
        object res;
        lock (_gate)
        {
            var r = _cfg.Rules.Find(x => x.Id == id);
            if (r is null) return Fail("not-found");
            if (enabled is bool on && on != r.Enabled)
            {
                r.Enabled = on;
                r.Updated = Now;
                SaveCfg();
                Compile();
            }
            var t = r.Enabled && enabled != false ? ApplyRunning(r, Now, ref ev) : new Tally();
            res = new { ok = true, matched = t.Matched, applied = t.Applied, denied = t.Denied, failed = t.Failed, killed = t.Killed + t.Limited };
        }
        if (ev is not null) WriteEvents(ev);
        return res;
    }

    object BalanceInfo()
    {
        ProBalanceConfig cfg;
        List<(string Name, uint Pid, long Since, double Cpu, double Peak, string From, string To)> act;
        int session;
        List<string> denied;
        lock (_gate)
        {
            var c = _cfg.ProBalance;
            cfg = new ProBalanceConfig
            {
                Enabled = c.Enabled, SystemThreshold = c.SystemThreshold, ProcessThreshold = c.ProcessThreshold, Restrain = c.Restrain,
                Exclusions = c.Exclusions.ToList(),
            };
            act = _pb.Active.Select(e => (e.Name, e.Pid, e.Since, Math.Round(e.Cpu, 1), Math.Round(e.Peak, 1), RulesOs.ClassName(e.Orig), RulesOs.ClassName(e.Target)))
                .OrderByDescending(e => e.Item4).ToList();
            session = _pb.Restraints;
            denied = _pb.DeniedNames.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        long today = 0;
        try
        {
            using var c = _host.Db.OpenReader();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM rules_events WHERE kind='restrain' AND ts >= $d";
            q.Parameters.AddWithValue("$d", new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds());
            today = Convert.ToInt64(q.ExecuteScalar());
        }
        catch (Exception ex) { Log.Write("rules: today", ex); }
        var info = NameInfo(act.Select(a => a.Name));
        return new
        {
            config = cfg,
            active = act.Select(a =>
            {
                info.TryGetValue(a.Name, out var ni);
                return new { name = a.Name, pid = a.Pid, since = a.Since, cpu = a.Cpu, peak = a.Peak, from = a.From, to = a.To, nid = ni?.Nid, descr = ni?.Descr, hasIcon = ni?.Icon ?? false };
            }).ToList(),
            today, session, denied,
            isAdmin = Native.IsUserAnAdmin(),
        };
    }

    object SetBalance(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("config", out var jc) || jc.ValueKind != JsonValueKind.Object) return Fail("failed");
        ProBalanceConfig c;
        try { c = Clean(jc.Deserialize<ProBalanceConfig>(ReadJson) ?? new()); }
        catch (JsonException ex) { return Fail("failed", ex.Message); }
        List<RuleEvent>? ev = null;
        lock (_gate)
        {
            bool was = _cfg.ProBalance.Enabled;
            _cfg.ProBalance = c;
            SaveCfg();
            Compile();
            if (was && !c.Enabled) _pb.RestoreAll(Now, "off", ref ev);
            if (was != c.Enabled) (ev ??= new()).Add(new(Now, c.Enabled ? "pbOn" : "pbOff", null, 0, null));
        }
        if (ev is not null) WriteEvents(ev);
        return BalanceInfo();
    }

    static readonly Dictionary<string, string> Groups = new()
    {
        ["balance"] = "'restrain','restore','pbOn','pbOff'",
        ["rules"] = "'rule','denied','kill','limit'",
        ["power"] = "'plan','planRestore'",
    };

    object Events(int limit, string? group)
    {
        var rows = new List<(long Id, long Ts, string Kind, string? Name, long? Pid, string? Detail)>();
        try
        {
            using var c = _host.Db.OpenReader();
            using var q = c.CreateCommand();
            string where = group is not null && Groups.TryGetValue(group, out var kinds) ? $"WHERE kind IN ({kinds})" : "";
            q.CommandText = $"SELECT id, ts, kind, name, pid, detail FROM rules_events {where} ORDER BY id DESC LIMIT $l";
            q.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 1000));
            using var r = q.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        catch (Exception ex) { Log.Write("rules: read events", ex); }
        var info = NameInfo(rows.Where(x => x.Name is not null).Select(x => x.Name!));
        return rows.Select(x =>
        {
            NameRow? ni = x.Name is not null && info.TryGetValue(x.Name, out var v) ? v : null;
            JsonElement? detail = null;
            if (x.Detail is not null)
                try { detail = JsonDocument.Parse(x.Detail).RootElement.Clone(); } catch (JsonException) { }
            return new { id = x.Id, ts = x.Ts, kind = x.Kind, name = x.Name, pid = x.Pid, detail, nid = ni?.Nid, descr = ni?.Descr, hasIcon = ni?.Icon ?? false };
        }).ToList();
    }

    object ClearEvents()
    {
        _host.Db.Write((c, tx) => Exec(c, tx, "DELETE FROM rules_events;"));
        return new { ok = true };
    }

    object PowerPlans()
    {
        var plans = RulesOs.Plans();
        var active = RulesOs.ActivePlan();
        string? byRule;
        Guid? restore;
        lock (_gate) { byRule = _planRule is { Length: > 0 } n ? n : null; restore = _planRestore; }
        return new
        {
            plans = plans.Select(x => new { guid = x.Guid.ToString(), name = x.Name, active = x.Guid == active }).ToList(),
            active = active?.ToString(),
            byRule,
            restore = restore?.ToString(),
            restoreName = restore is Guid g ? plans.Find(x => x.Guid == g).Name ?? RulesOs.PlanName(g) : null,
        };
    }

    static object SetPowerPlan(string? guid)
    {
        if (!Guid.TryParse(guid, out var g) || !RulesOs.Plans().Exists(x => x.Guid == g)) return Fail("not-found");
        uint rc = RulesOs.SetPlan(g);
        return rc == 0 ? new { ok = true } : Fail(rc == 5 ? "access-denied" : "failed", new Win32Exception((int)rc).Message);
    }

    static object Cores()
    {
        var cpus = RulesOs.Cpus();
        return new
        {
            count = cpus.Count,
            total = Environment.ProcessorCount,
            cores = cpus.Select(c => new { index = c.Index, core = c.Core, eff = c.Eff }).ToList(),
            hybrid = cpus.Select(c => c.Eff).Distinct().Count() > 1,
            smt = cpus.Select(c => c.Core).Distinct().Count() < cpus.Count,
        };
    }
}
