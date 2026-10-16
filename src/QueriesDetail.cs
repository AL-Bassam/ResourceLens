using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

/// <summary>Sample-level ("what exactly happened at 14:32:05?") and per-process-instance queries.</summary>
public sealed partial class Queries
{
    static double? Dn(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    static string? Sn(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static long? Ln(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    /// <summary>Metric expression on the sys table (value per sample).</summary>
    static string SysExpr(string metric) => metric switch
    {
        "mem" => "s.mem_max",
        "disk" => "(COALESCE(s.disk_r,0)+COALESCE(s.disk_w,0))*1000.0/s.dt",
        "net" => "(COALESCE(s.net_rx,0)+COALESCE(s.net_tx,0))*1000.0/s.dt",
        "gpu" => "s.gpu_max",
        "temp" => "MAX(COALESCE(s.cpu_temp_max,0), COALESCE(s.gpu_temp_max,0))",
        _ => "s.cpu_max",
    };

    /// <summary>Metric expression on iproc/proc rows (alias p).</summary>
    static string RowExpr(string metric) => metric switch
    {
        "mem" => "COALESCE(p.mem_max, p.mem)",
        "disk" => "(COALESCE(p.io_r,0)+COALESCE(p.io_w,0))*1000.0/p.pdt",
        "net" => "(COALESCE(p.net_rx,0)+COALESCE(p.net_tx,0))*1000.0/p.pdt",
        "gpu" => "COALESCE(p.gpu,0)",
        // what heats the chip: CPU + GPU work of the process
        "temp" => "COALESCE(p.cpu_max,p.cpu,0)+COALESCE(p.gpu,0)",
        _ => "COALESCE(p.cpu_max,p.cpu,0)",
    };

    const string SysCols = """
        s.ts, s.dt, s.cpu_max, s.mem_max, s.mem_total, s.commit_used,
        s.disk_r*1000.0/s.dt, s.disk_w*1000.0/s.dt, s.net_rx*1000.0/s.dt, s.net_tx*1000.0/s.dt, s.gpu_max,
        st.text, i.role, i.detail, i.pid, n.id, n.name, n.descr, n.icon IS NOT NULL, s.cpu, s.mem_used,
        s.cpu_temp_max, s.gpu_temp_max, s.cpu_mhz, s.gpu_mhz, s.pkg_power
        """;
    const int SysColCount = 26;
    const string SysJoins = """
        FROM sys s LEFT JOIN strings st ON st.id = s.fg_title
        LEFT JOIN inst i ON i.id = s.fg_iid LEFT JOIN names n ON n.id = i.nid
        """;

    static Dictionary<string, object?> ReadSysRow(SqliteDataReader r) => new()
    {
        ["ts"] = r.GetInt64(0), ["dt"] = r.GetInt64(1),
        ["cpu"] = Dn(r, 2), ["mem"] = Dn(r, 3), ["memTotal"] = Dn(r, 4), ["commit"] = Dn(r, 5),
        ["diskR"] = Dn(r, 6), ["diskW"] = Dn(r, 7), ["netRx"] = Dn(r, 8), ["netTx"] = Dn(r, 9), ["gpu"] = Dn(r, 10),
        ["fg"] = r.IsDBNull(15) ? null : new
        {
            title = Sn(r, 11), role = Sn(r, 12), detail = Sn(r, 13), pid = Ln(r, 14),
            nid = r.GetInt64(15), name = Sn(r, 16), descr = Sn(r, 17), hasIcon = !r.IsDBNull(18) && r.GetBoolean(18),
        },
        ["cpuAvg"] = Dn(r, 19), ["memAvg"] = Dn(r, 20),
        ["cpuTemp"] = Dn(r, 21), ["gpuTemp"] = Dn(r, 22), ["cpuMhz"] = Dn(r, 23), ["gpuMhz"] = Dn(r, 24), ["pkgPower"] = Dn(r, 25),
    };

    // ---------------- Moments: every sample, newest/oldest/highest first ----------------

    public object Moments(string metric, long from, long to, string order, int offset, int limit, double? min)
    {
        string expr = SysExpr(metric);
        string where = $"WHERE s.ts >= $f AND s.ts <= $to{(min is null ? "" : $" AND {expr} >= $min")}";
        string orderBy = order switch { "old" => "s.ts ASC", "new" => "s.ts DESC", _ => $"{expr} DESC, s.ts DESC" };
        using var c = Db.OpenReader();

        long total;
        using (var qc = c.CreateCommand())
        {
            qc.CommandText = $"SELECT COUNT(*) FROM sys s {where}";
            qc.Parameters.AddWithValue("$f", from);
            qc.Parameters.AddWithValue("$to", to);
            if (min is not null) qc.Parameters.AddWithValue("$min", min.Value);
            total = (long)qc.ExecuteScalar()!;
        }

        var rows = new List<Dictionary<string, object?>>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT {SysCols}, {expr} {SysJoins} {where} ORDER BY {orderBy} LIMIT $lim OFFSET $off";
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            q.Parameters.AddWithValue("$lim", Math.Clamp(limit, 1, 500));
            q.Parameters.AddWithValue("$off", Math.Max(0, offset));
            if (min is not null) q.Parameters.AddWithValue("$min", min.Value);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var row = ReadSysRow(r);
                row["value"] = Dn(r, SysColCount);
                rows.Add(row);
            }
        }
        var tops = TopContributors(c, rows.Select(x => (long)x["ts"]!).ToList(), metric, 3);
        foreach (var row in rows) row["top"] = tops.GetValueOrDefault((long)row["ts"]!) ?? new List<object>();
        return new { total, offset, rows };
    }

    /// <summary>Top N processes (individual instances when available, otherwise applications) per sample.</summary>
    Dictionary<long, List<object>> TopContributors(SqliteConnection c, List<long> tsList, string metric, int n)
    {
        var res = new Dictionary<long, List<object>>();
        if (tsList.Count == 0) return res;
        string inList = string.Join(',', tsList);
        string expr = RowExpr(metric);
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"""
                SELECT * FROM (
                  SELECT p.ts, n.id, n.name, n.descr, n.icon IS NOT NULL, i.role, i.detail, i.pid, i.title,
                    COALESCE(p.cpu_max, p.cpu), COALESCE(p.mem_max, p.mem), (COALESCE(p.io_r,0)+COALESCE(p.io_w,0))*1000.0/p.pdt,
                    (COALESCE(p.net_rx,0)+COALESCE(p.net_tx,0))*1000.0/p.pdt, p.gpu, p.iid,
                    ROW_NUMBER() OVER (PARTITION BY p.ts ORDER BY {expr} DESC) AS rn
                  FROM iproc p JOIN inst i ON i.id = p.iid JOIN names n ON n.id = i.nid
                  WHERE p.ts IN ({inList})) WHERE rn <= {n}
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                long ts = r.GetInt64(0);
                if (!res.TryGetValue(ts, out var list)) res[ts] = list = new();
                list.Add(new
                {
                    nid = r.GetInt64(1), name = r.GetString(2), descr = Sn(r, 3), hasIcon = r.GetBoolean(4),
                    role = Sn(r, 5), detail = Sn(r, 6), pid = Ln(r, 7), title = Sn(r, 8),
                    cpu = Dn(r, 9), mem = Dn(r, 10), io = Dn(r, 11), net = Dn(r, 12), gpu = Dn(r, 13), iid = r.GetInt64(14),
                });
            }
        }
        // Older history (before per-process detail existed): fall back to application totals.
        var missing = tsList.Where(t => !res.ContainsKey(t)).ToList();
        if (missing.Count > 0)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"""
                SELECT * FROM (
                  SELECT p.ts, n.id, n.name, n.descr, n.icon IS NOT NULL,
                    COALESCE(p.cpu_max, p.cpu), COALESCE(p.mem_max, p.mem), (COALESCE(p.io_r,0)+COALESCE(p.io_w,0))*1000.0/p.pdt,
                    (COALESCE(p.net_rx,0)+COALESCE(p.net_tx,0))*1000.0/p.pdt, p.gpu,
                    ROW_NUMBER() OVER (PARTITION BY p.ts ORDER BY {expr} DESC) AS rn
                  FROM proc p JOIN names n ON n.id = p.nid
                  WHERE p.ts IN ({string.Join(',', missing)})) WHERE rn <= {n}
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                long ts = r.GetInt64(0);
                if (!res.TryGetValue(ts, out var list)) res[ts] = list = new();
                list.Add(new
                {
                    nid = r.GetInt64(1), name = r.GetString(2), descr = Sn(r, 3), hasIcon = r.GetBoolean(4),
                    role = (string?)null, detail = (string?)null, pid = (long?)null, title = (string?)null,
                    cpu = Dn(r, 5), mem = Dn(r, 6), io = Dn(r, 7), net = Dn(r, 8), gpu = Dn(r, 9), iid = (long?)null,
                });
            }
        }
        return res;
    }

    // ---------------- One moment in full detail ----------------

    /// <summary>
    /// The sample at (or the highest one for <paramref name="metric"/> inside) [t, t+span), with every process recorded then.
    /// </summary>
    public object? Moment(long t, long span, string metric)
    {
        using var c = Db.OpenReader();
        long? ts = null;
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT s.ts FROM sys s WHERE s.ts >= $t AND s.ts < $e ORDER BY {SysExpr(metric)} DESC LIMIT 1";
            q.Parameters.AddWithValue("$t", t);
            q.Parameters.AddWithValue("$e", t + Math.Max(1, span));
            ts = q.ExecuteScalar() as long?;
        }
        if (ts is null)
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT ts FROM sys ORDER BY ABS(ts - $t) LIMIT 1";
            q.Parameters.AddWithValue("$t", t);
            ts = q.ExecuteScalar() as long?;
        }
        if (ts is null) return null;

        Dictionary<string, object?> sys;
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT {SysCols} {SysJoins} WHERE s.ts = $ts";
            q.Parameters.AddWithValue("$ts", ts.Value);
            using var r = q.ExecuteReader();
            if (!r.Read()) return null;
            sys = ReadSysRow(r);
        }
        long? Neighbor(string op, string dir)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT ts FROM sys WHERE ts {op} $ts ORDER BY ts {dir} LIMIT 1";
            q.Parameters.AddWithValue("$ts", ts.Value);
            return q.ExecuteScalar() as long?;
        }

        var procs = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT p.iid, i.pid, i.role, i.detail, i.title, i.cmd, n.id, n.name, n.descr, n.icon IS NOT NULL,
                  COALESCE(p.cpu_max, p.cpu), COALESCE(p.mem_max, p.mem), p.ws, p.commit_b, p.io_r*1000.0/p.pdt, p.io_w*1000.0/p.pdt,
                  p.net_rx*1000.0/p.pdt, p.net_tx*1000.0/p.pdt, p.gpu
                FROM iproc p JOIN inst i ON i.id = p.iid JOIN names n ON n.id = i.nid
                WHERE p.ts = $ts ORDER BY COALESCE(p.mem_max, p.mem) DESC
                """;
            q.Parameters.AddWithValue("$ts", ts.Value);
            using var r = q.ExecuteReader();
            while (r.Read())
                procs.Add(new
                {
                    iid = r.GetInt64(0), pid = Ln(r, 1), role = Sn(r, 2), detail = Sn(r, 3), title = Sn(r, 4), cmd = Sn(r, 5),
                    nid = r.GetInt64(6), name = r.GetString(7), descr = Sn(r, 8), hasIcon = r.GetBoolean(9),
                    cpu = Dn(r, 10), mem = Dn(r, 11), ws = Dn(r, 12), commit = Dn(r, 13),
                    ioR = Dn(r, 14), ioW = Dn(r, 15), netRx = Dn(r, 16), netTx = Dn(r, 17), gpu = Dn(r, 18),
                });
        }
        var apps = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT n.id, n.name, n.descr, n.icon IS NOT NULL, p.cnt, COALESCE(p.cpu_max, p.cpu), COALESCE(p.mem_max, p.mem),
                  p.io_r*1000.0/p.pdt, p.io_w*1000.0/p.pdt, p.net_rx*1000.0/p.pdt, p.net_tx*1000.0/p.pdt, p.gpu
                FROM proc p JOIN names n ON n.id = p.nid WHERE p.ts = $ts ORDER BY COALESCE(p.mem_max, p.mem) DESC
                """;
            q.Parameters.AddWithValue("$ts", ts.Value);
            using var r = q.ExecuteReader();
            while (r.Read())
                apps.Add(new
                {
                    nid = r.GetInt64(0), name = r.GetString(1), descr = Sn(r, 2), hasIcon = r.GetBoolean(3), count = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    cpu = Dn(r, 5), mem = Dn(r, 6), ioR = Dn(r, 7), ioW = Dn(r, 8), netRx = Dn(r, 9), netTx = Dn(r, 10), gpu = Dn(r, 11),
                });
        }
        return new { sys, prev = Neighbor("<", "DESC"), next = Neighbor(">", "ASC"), processes = procs, apps };
    }

    // ---------------- Processes inside one application ----------------

    public object AppInstances(long nid, long from, long to) =>
        new { items = InstancesByApp(nid, from, to).GetValueOrDefault(nid) ?? new List<object>() };

    /// <summary>
    /// The processes of one application, biggest first (at most 300) — or, with no application given, of every
    /// application in a single pass over the range (there is no per-process index, so one scan beats one per app).
    /// </summary>
    internal Dictionary<long, List<object>> InstancesByApp(long? nid, long from, long to)
    {
        var res = new Dictionary<long, List<object>>();
        using var c = Db.OpenReader();
        long totalDt;
        using (var q0 = c.CreateCommand())
        {
            q0.CommandText = "SELECT COALESCE(SUM(dt),0) FROM sys WHERE ts >= $f AND ts <= $to";
            q0.Parameters.AddWithValue("$f", from);
            q0.Parameters.AddWithValue("$to", to);
            totalDt = (long)q0.ExecuteScalar()!;
        }
        if (totalDt == 0) return res;
        using var q = c.CreateCommand();
        q.CommandText = $"""
            SELECT p.iid, i.pid, i.role, i.detail, i.title, i.created, i.cmd,
              SUM(p.mem*p.pdt)*1.0/SUM(p.pdt), MAX(COALESCE(p.mem_max, p.mem)), SUM(p.cpu*p.pdt)/$tot, MAX(COALESCE(p.cpu_max, p.cpu)),
              SUM(p.io_r), SUM(p.io_w), SUM(p.net_rx), SUM(p.net_tx), SUM(p.gpu*p.pdt)/$tot,
              SUM(p.pdt)*1.0/$tot, MIN(p.ts), MAX(p.ts), i.nid
            FROM iproc p CROSS JOIN inst i
            WHERE i.id = p.iid{(nid is null ? "" : " AND i.nid = $nid")} AND p.ts >= $f AND p.ts <= $to
            GROUP BY p.iid ORDER BY {(nid is null ? "i.nid, " : "")}SUM(p.mem*p.pdt) DESC, p.iid DESC{(nid is null ? "" : " LIMIT 300")}
            """;
        if (nid is not null) q.Parameters.AddWithValue("$nid", nid.Value);
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        q.Parameters.AddWithValue("$tot", (double)totalDt);
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            long app = r.GetInt64(19);
            if (!res.TryGetValue(app, out var items)) res[app] = items = new();
            if (items.Count >= 300) continue;
            items.Add(new
            {
                iid = r.GetInt64(0), pid = Ln(r, 1), role = Sn(r, 2), detail = Sn(r, 3), title = Sn(r, 4), created = Ln(r, 5), cmd = Sn(r, 6),
                memAvg = Dn(r, 7), memMax = Dn(r, 8), cpuAvg = Dn(r, 9), cpuMax = Dn(r, 10),
                ioR = Dn(r, 11), ioW = Dn(r, 12), netRx = Dn(r, 13), netTx = Dn(r, 14), gpuAvg = Dn(r, 15),
                presence = Math.Round(Dn(r, 16) ?? 0, 3), first = r.GetInt64(17), last = r.GetInt64(18),
            });
        }
        return res;
    }

    /// <summary>Memory (or CPU) over time of the biggest processes inside one application, for a stacked chart.</summary>
    public object AppStack(long nid, long from, long to, int points, string metric)
    {
        long b = Bucket(from, to, points);
        using var c = Db.OpenReader();
        string val = metric == "cpu" ? "SUM(p.cpu*p.pdt)/SUM(p.pdt)" : "SUM(p.mem*p.pdt)*1.0/SUM(p.pdt)";
        string rank = metric == "cpu" ? "SUM(p.cpu*p.pdt)" : "SUM(p.mem*p.pdt)";

        var top = new List<(long iid, long? pid, string? role, string? detail)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"""
                SELECT p.iid, i.pid, i.role, i.detail FROM iproc p CROSS JOIN inst i
                WHERE i.id = p.iid AND i.nid = $nid AND p.ts >= $f AND p.ts <= $to GROUP BY p.iid ORDER BY {rank} DESC LIMIT 7
                """;
            q.Parameters.AddWithValue("$nid", nid);
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read()) top.Add((r.GetInt64(0), Ln(r, 1), Sn(r, 2), Sn(r, 3)));
        }
        var buckets = new List<long>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT DISTINCT ts/$b*$b FROM proc WHERE nid=$nid AND ts >= $f AND ts <= $to ORDER BY 1";
            q.Parameters.AddWithValue("$b", b);
            q.Parameters.AddWithValue("$nid", nid);
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read()) buckets.Add(r.GetInt64(0));
        }
        var index = buckets.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var series = new List<object>();
        if (top.Count > 0)
        {
            var data = top.ToDictionary(x => x.iid, _ => new double?[buckets.Count]);
            using var q = c.CreateCommand();
            q.CommandText = $"""
                SELECT p.ts/$b*$b, p.iid, {val} FROM iproc p
                WHERE p.iid IN ({string.Join(',', top.Select(x => x.iid))}) AND p.ts >= $f AND p.ts <= $to
                GROUP BY 1, 2
                """;
            q.Parameters.AddWithValue("$b", b);
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read())
                if (index.TryGetValue(r.GetInt64(0), out int i) && !r.IsDBNull(2)) data[r.GetInt64(1)][i] = Math.Round(r.GetDouble(2), 2);
            foreach (var x in top) series.Add(new { iid = x.iid, pid = x.pid, role = x.role, detail = x.detail, data = data[x.iid] });
        }
        return new { bucket = b, t = buckets, series };
    }

    public object InstanceSeries(long iid, long from, long to, int points)
    {
        long b = Bucket(from, to, points);
        using var c = Db.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT ts/$b*$b AS t,
                SUM(mem*pdt)*1.0/SUM(pdt), MAX(COALESCE(mem_max, mem)),
                SUM(cpu*pdt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE pdt END), MAX(COALESCE(cpu_max, cpu)),
                SUM(io_r)*1000.0/SUM(pdt), SUM(io_w)*1000.0/SUM(pdt),
                SUM(net_rx)*1000.0/SUM(pdt), SUM(net_tx)*1000.0/SUM(pdt),
                SUM(gpu*pdt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE pdt END), MAX(pdt)
            FROM iproc WHERE iid=$iid AND ts >= $f AND ts <= $to GROUP BY t ORDER BY t
            """;
        q.Parameters.AddWithValue("$b", b);
        q.Parameters.AddWithValue("$iid", iid);
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        var t = new List<long>();
        var cols = Enumerable.Range(0, 9).Select(_ => new List<double?>()).ToArray();
        long prevT = long.MinValue;
        using (var r = q.ExecuteReader())
        {
            while (r.Read())
            {
                long ts = r.GetInt64(0);
                long maxDt = r.GetInt64(10);
                if (prevT != long.MinValue && ts - prevT > Math.Max(3 * b, 3 * maxDt + 5000))
                {
                    t.Add(prevT + b);
                    foreach (var col in cols) col.Add(null);
                }
                t.Add(ts);
                for (int i = 0; i < 9; i++) cols[i].Add(Dn(r, i + 1) is double v ? Math.Round(v, 2) : null);
                prevT = ts;
            }
        }
        object? info = null;
        using (var qi = c.CreateCommand())
        {
            qi.CommandText = "SELECT i.pid, i.role, i.detail, i.title, i.cmd, i.created, n.id, n.name, n.descr FROM inst i JOIN names n ON n.id=i.nid WHERE i.id=$iid";
            qi.Parameters.AddWithValue("$iid", iid);
            using var r = qi.ExecuteReader();
            if (r.Read())
                info = new { pid = Ln(r, 0), role = Sn(r, 1), detail = Sn(r, 2), title = Sn(r, 3), cmd = Sn(r, 4), created = Ln(r, 5), nid = r.GetInt64(6), name = r.GetString(7), descr = Sn(r, 8) };
        }
        return new
        {
            iid, info, bucket = b, t,
            mem = cols[0], memMax = cols[1], cpu = cols[2], cpuMax = cols[3],
            ioR = cols[4], ioW = cols[5], netRx = cols[6], netTx = cols[7], gpu = cols[8],
        };
    }

    // ---------------- Alerts history ----------------

    public object Events(long from, long to)
    {
        using var c = Db.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT ts, kind, value, detail FROM events WHERE ts >= $f AND ts <= $to ORDER BY ts DESC LIMIT 200";
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        var list = new List<object>();
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            JsonElement? detail = null;
            try { if (!r.IsDBNull(3)) detail = JsonDocument.Parse(r.GetString(3)).RootElement.Clone(); } catch { }
            list.Add(new { ts = r.GetInt64(0), kind = r.GetString(1), value = Dn(r, 2), top = detail });
        }
        return list;
    }

    /// <summary>Top individual processes at one sample (falls back to applications for older history).</summary>
    internal List<object> TopInstancesAt(SqliteConnection c, long ts, string metric, int n) =>
        TopContributors(c, new List<long> { ts }, metric, n).GetValueOrDefault(ts) ?? new List<object>();
}
