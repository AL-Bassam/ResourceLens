using System.Text.Json;

namespace ResourceLens;

/// <summary>One line of the Activity log (rules_events); Detail is a small JSON object the dashboard renders.</summary>
internal readonly record struct RuleEvent(long Ts, string Kind, string? Name, uint Pid, string? Detail);

/// <summary>
/// ProBalance (Process Lasso's idea): while the whole machine is busy, background processes that take a big share of the
/// CPU drop to a lower priority class so the app in front stays responsive, and each gets its exact original class back
/// once it calms down. Pure decision logic over the sample; the OS calls go through <see cref="IOs"/> so it can be
/// self-tested. Not thread-safe: the owner serializes calls.
/// </summary>
internal sealed class ProBalance(ProBalance.IOs os)
{
    public interface IOs
    {
        RulesOs.Result Restrain(uint pid, long created, uint target, out uint orig);
        RulesOs.Result Restore(uint pid, long created, uint target, uint orig);
    }

    public sealed class Entry
    {
        public uint Pid;
        public long Created, Since, Seen;
        public string Name = "";
        public uint Orig, Target;
        public int Low;
        public double Cpu, Peak;
    }

    readonly Dictionary<(uint, long), Entry> _active = new();
    // Processes Windows refused (or that turned out not to be normal priority): not retried while they live.
    readonly Dictionary<(uint, long), long> _skip = new();
    readonly Dictionary<string, int> _deniedNames = new(StringComparer.OrdinalIgnoreCase);

    public int Restraints { get; private set; }
    public IEnumerable<Entry> Active => _active.Values;
    public IReadOnlyDictionary<string, int> DeniedNames => _deniedNames;

    static string J(object o) => JsonSerializer.Serialize(o);

    /// <summary>One pass over the sample. <paramref name="skip"/>: names never restrained (exclusions, rules with a priority).</summary>
    public void Sample(FeatureSample s, ProBalanceConfig c, Func<string, bool> skip, ref List<RuleEvent>? ev)
    {
        if (!c.Enabled)
        {
            if (_active.Count > 0) RestoreAll(s.Ts, "off", ref ev);
            return;
        }
        double sys = s.Cpu ?? -1;
        bool busy = sys >= c.SystemThreshold;
        string? fg = null;
        List<FeatureProc>? cand = null;
        foreach (var p in s.Procs)
        {
            if (p.Pid == s.ForegroundPid) fg = p.Name;
            var key = (p.Pid, p.Created);
            if (_active.Count > 0 && _active.TryGetValue(key, out var e))
            {
                e.Seen = s.Ts;
                e.Cpu = p.Cpu;
                if (p.Cpu > e.Peak) e.Peak = p.Cpu;
                e.Low = p.Cpu < c.ProcessThreshold / 2 ? e.Low + 1 : 0;
                continue;
            }
            if (_skip.Count > 0 && _skip.ContainsKey(key)) { _skip[key] = s.Ts; continue; }
            // Normal / above normal only (base 8..12): idle and below normal are already out of the way, high and realtime
            // were chosen on purpose.
            if (busy && p.Cpu >= c.ProcessThreshold && p.Priority is >= 8 and < 13 && p.Pid > 4) (cand ??= new()).Add(p);
        }
        if (_skip.Count > 0)
        {
            List<(uint, long)>? gone = null;
            foreach (var (k, seen) in _skip) if (seen != s.Ts) (gone ??= new()).Add(k);
            if (gone is not null) foreach (var k in gone) _skip.Remove(k);
        }

        if (_active.Count > 0)
        {
            List<(Entry, string)>? done = null;
            foreach (var e in _active.Values)
            {
                string? why = e.Seen != s.Ts ? "gone"
                    : e.Low >= 2 ? "calm"
                    : sys >= 0 && sys < c.SystemThreshold - 15 ? "idle"
                    : fg is not null && e.Name.Equals(fg, StringComparison.OrdinalIgnoreCase) ? "fg"
                    : skip(e.Name) ? "excluded"
                    : null;
                if (why is not null) (done ??= new()).Add((e, why));
            }
            if (done is not null) foreach (var (e, why) in done) Restore(e, s.Ts, why, ref ev);
        }

        if (cand is null) return;
        uint target = c.Restrain == "idle" ? RulesOs.IdleClass : RulesOs.BelowNormalClass;
        foreach (var p in cand)
        {
            // The app the user is in front of, with all its processes (a browser's tabs, a game's helpers), always wins.
            if (fg is not null && p.Name.Equals(fg, StringComparison.OrdinalIgnoreCase)) continue;
            if (skip(p.Name)) continue;
            var key = (p.Pid, p.Created);
            var r = os.Restrain(p.Pid, p.Created, target, out uint orig);
            if (r == RulesOs.Result.Ok)
            {
                _active[key] = new Entry { Pid = p.Pid, Created = p.Created, Name = p.Name, Orig = orig, Target = target, Since = s.Ts, Seen = s.Ts, Cpu = p.Cpu, Peak = p.Cpu };
                Restraints++;
                (ev ??= new()).Add(new RuleEvent(s.Ts, "restrain", p.Name, p.Pid,
                    J(new { cpu = Math.Round(p.Cpu, 1), sys = Math.Round(sys, 1), from = RulesOs.ClassName(orig), to = RulesOs.ClassName(target) })));
            }
            else if (r != RulesOs.Result.Gone)
            {
                _skip[key] = s.Ts;
                if (r == RulesOs.Result.Denied && (_deniedNames.Count < 50 || _deniedNames.ContainsKey(p.Name)))
                    _deniedNames[p.Name] = _deniedNames.GetValueOrDefault(p.Name) + 1;
            }
        }
    }

    void Restore(Entry e, long ts, string why, ref List<RuleEvent>? ev)
    {
        _active.Remove((e.Pid, e.Created));
        if (why == "gone") return; // exited: its PID may already belong to another process
        var r = os.Restore(e.Pid, e.Created, e.Target, e.Orig);
        if (r == RulesOs.Result.Gone) return;
        (ev ??= new()).Add(new RuleEvent(ts, "restore", e.Name, e.Pid,
            J(new { ms = ts - e.Since, why = r == RulesOs.Result.Skipped ? "changed" : why, to = RulesOs.ClassName(e.Orig), ok = r == RulesOs.Result.Ok })));
    }

    /// <summary>ProBalance turned off, or the app is closing: everything goes back to its original class.</summary>
    public void RestoreAll(long ts, string why, ref List<RuleEvent>? ev)
    {
        foreach (var e in _active.Values.ToList()) Restore(e, ts, why, ref ev);
        _skip.Clear();
        _deniedNames.Clear();
    }

    /// <summary>A rule just set this process' priority: the rule wins, nothing to restore later.</summary>
    public void Forget(uint pid, long created) => _active.Remove((pid, created));

#if DEBUG
    bool Has(uint pid) => _active.Keys.Any(k => k.Item1 == pid);

    sealed class FakeOs : IOs
    {
        public readonly Dictionary<uint, uint> Cls = new();
        public readonly HashSet<uint> Deny = new();
        public int Calls;

        public RulesOs.Result Restrain(uint pid, long created, uint target, out uint orig)
        {
            Calls++;
            orig = Cls.GetValueOrDefault(pid, RulesOs.NormalClass);
            if (Deny.Contains(pid)) return RulesOs.Result.Denied;
            if (orig is not (RulesOs.NormalClass or RulesOs.AboveNormalClass)) return RulesOs.Result.Skipped;
            Cls[pid] = target;
            return RulesOs.Result.Ok;
        }

        public RulesOs.Result Restore(uint pid, long created, uint target, uint orig)
        {
            Calls++;
            if (Cls.GetValueOrDefault(pid, RulesOs.NormalClass) != target) return RulesOs.Result.Skipped;
            Cls[pid] = orig;
            return RulesOs.Result.Ok;
        }
    }

    /// <summary>Test hook: drives the decision logic with made-up samples against a fake OS.</summary>
    public static object SelfTest()
    {
        var checks = new List<object>();
        bool all = true;
        void Check(string name, bool ok) { checks.Add(new { name, ok }); all &= ok; }

        var os = new FakeOs();
        var pb = new ProBalance(os);
        var cfg = new ProBalanceConfig { Enabled = true };
        var never = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "svchost.exe", "ruled.exe" };
        Func<string, bool> skip = never.Contains;
        long ts = 1_000_000;
        List<RuleEvent>? ev = null;
        static FeatureProc P(uint pid, string name, double cpu, int prio = 8) => new(pid, pid * 10L, name, cpu, 1 << 20, prio, 0, 0, 0);
        void Run(double sys, uint fg, params FeatureProc[] procs) => pb.Sample(new FeatureSample(ts += 5000, 5000, sys, 0, 0, fg, procs, false), cfg, skip, ref ev);

        os.Cls[60] = RulesOs.AboveNormalClass;
        Run(92, 20, P(10, "hog.exe", 30), P(20, "game.exe", 50), P(21, "game.exe", 15), P(30, "small.exe", 4), P(40, "svchost.exe", 20),
            P(50, "idle.exe", 20, 4), P(60, "above.exe", 25, 10), P(70, "ruled.exe", 40), P(80, "high.exe", 30, 13));
        Check("busy: restrains the background hogs (normal + above normal)", pb._active.Count == 2 && pb.Has(10) && pb.Has(60));
        Check("foreground app and all its processes untouched", !pb.Has(20) && !pb.Has(21));
        Check("small / excluded / idle / high / ruled untouched", !pb.Has(30) && !pb.Has(40) && !pb.Has(50) && !pb.Has(70) && !pb.Has(80));
        Check("lowered to below normal", os.Cls[10] == RulesOs.BelowNormalClass && os.Cls[60] == RulesOs.BelowNormalClass);
        Check("logged two restraints", ev?.Count(e => e.Kind == "restrain") == 2);

        Run(92, 20, P(10, "hog.exe", 3), P(60, "above.exe", 25, 6));
        Check("one calm sample is not enough", pb.Has(10));
        Run(92, 20, P(10, "hog.exe", 2), P(60, "above.exe", 25, 6));
        Check("restored after two calm samples, exactly to normal", !pb.Has(10) && os.Cls[10] == RulesOs.NormalClass);
        Run(60, 20, P(60, "above.exe", 25, 6));
        Check("machine calm (< threshold-15): above normal restored exactly", !pb.Has(60) && os.Cls[60] == RulesOs.AboveNormalClass);

        Run(80, 20, P(10, "hog.exe", 40));
        Check("below the system threshold nothing is restrained", !pb.Has(10));

        Run(92, 20, P(10, "hog.exe", 30));
        Run(92, 10, P(10, "hog.exe", 30));
        Check("restored the moment it comes to the foreground", !pb.Has(10) && os.Cls[10] == RulesOs.NormalClass);

        os.Deny.Add(90);
        int calls = os.Calls;
        Run(92, 20, P(90, "admin.exe", 30));
        Run(92, 20, P(90, "admin.exe", 30));
        Run(92, 20, P(90, "admin.exe", 30));
        Check("access denied: tried once, not every sample", os.Calls == calls + 1 && pb.DeniedNames.ContainsKey("admin.exe"));

        Run(92, 20, P(10, "hog.exe", 30));
        calls = os.Calls;
        Run(92, 20);
        Check("exited process dropped without touching its (reused) PID", !pb.Has(10) && os.Calls == calls);

        os.Cls[10] = RulesOs.NormalClass;
        Run(92, 20, P(10, "hog.exe", 30));
        bool again = pb.Has(10);
        os.Cls[10] = RulesOs.HighClass; // the user raised it meanwhile
        Run(92, 20, P(10, "hog.exe", 1));
        Run(92, 20, P(10, "hog.exe", 1));
        Check("a priority changed meanwhile is never overridden", again && !pb.Has(10) && os.Cls[10] == RulesOs.HighClass
            && ev?.LastOrDefault().Detail?.Contains("\"changed\"") == true);

        os.Cls[10] = RulesOs.NormalClass;
        Run(92, 20, P(10, "hog.exe", 30), P(11, "hog2.exe", 30));
        never.Add("hog2.exe");
        Run(92, 20, P(10, "hog.exe", 30), P(11, "hog2.exe", 30));
        Check("newly excluded name is restored", pb.Has(10) && !pb.Has(11) && os.Cls[11] == RulesOs.NormalClass);

        cfg.Enabled = false;
        Run(92, 20, P(10, "hog.exe", 30));
        Check("turning ProBalance off restores everything", pb._active.Count == 0 && os.Cls[10] == RulesOs.NormalClass);

        cfg.Enabled = true; cfg.Restrain = "idle";
        Run(95, 20, P(12, "hog3.exe", 30));
        Check("restrain to idle when chosen", os.Cls.GetValueOrDefault(12u) == RulesOs.IdleClass);
        ev = null;
        pb.RestoreAll(ts, "exit", ref ev);
        Check("closing restores and logs", os.Cls[12] == RulesOs.NormalClass && ev?.Count == 1 && ev[0].Kind == "restore");

        return new { ok = all, checks };
    }
#endif
}
