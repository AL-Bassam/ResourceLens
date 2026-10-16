using Microsoft.Data.Sqlite;

namespace ResourceLens;

/// <summary>Read-side queries used by the dashboard. Results are plain objects serialized to JSON.</summary>
public sealed partial class Queries(Store store)
{
    Store Db => store;

    static long Bucket(long from, long to, int points) => Math.Max(1000, (long)Math.Ceiling((to - from) / (double)Math.Max(10, points)));

    static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    static double R1(double v) => Math.Round(v, 1);

    // ---------------- Ranges ----------------

    public object Boots()
    {
        using var c = store.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT boot, MIN(ts), MAX(ts) FROM sys GROUP BY boot ORDER BY boot DESC LIMIT 30";
        using var r = q.ExecuteReader();
        var list = new List<object>();
        while (r.Read()) list.Add(new { boot = r.GetInt64(0), first = r.GetInt64(1), last = r.GetInt64(2) });

        using var q2 = c.CreateCommand();
        q2.CommandText = "SELECT MIN(ts), MAX(ts), COUNT(*) FROM sys";
        using var r2 = q2.ExecuteReader();
        r2.Read();
        return new
        {
            boots = list,
            first = r2.IsDBNull(0) ? (long?)null : r2.GetInt64(0),
            last = r2.IsDBNull(1) ? (long?)null : r2.GetInt64(1),
            samples = r2.GetInt64(2),
        };
    }

    // ---------------- System series ----------------

    public object SystemSeries(long from, long to, int points)
    {
        long b = Bucket(from, to, points);
        using var c = store.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT ts/$b*$b AS t,
                SUM(cpu*dt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE dt END), MAX(cpu_max),
                SUM(mem_used*dt)/SUM(CASE WHEN mem_used IS NULL THEN NULL ELSE dt END), MAX(mem_max), MAX(mem_total),
                SUM(commit_used*dt)/SUM(CASE WHEN commit_used IS NULL THEN NULL ELSE dt END), MAX(commit_limit),
                SUM(disk_r)*1000.0/SUM(dt), SUM(disk_w)*1000.0/SUM(dt),
                SUM(net_rx)*1000.0/SUM(dt), SUM(net_tx)*1000.0/SUM(dt),
                SUM(gpu*dt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE dt END), MAX(gpu_max),
                SUM(cpu_temp*dt)/SUM(CASE WHEN cpu_temp IS NULL THEN NULL ELSE dt END), MAX(cpu_temp_max),
                SUM(gpu_temp*dt)/SUM(CASE WHEN gpu_temp IS NULL THEN NULL ELSE dt END), MAX(gpu_temp_max),
                SUM(cpu_mhz*dt)/SUM(CASE WHEN cpu_mhz IS NULL THEN NULL ELSE dt END),
                SUM(gpu_mhz*dt)/SUM(CASE WHEN gpu_mhz IS NULL THEN NULL ELSE dt END),
                SUM(pkg_power*dt)/SUM(CASE WHEN pkg_power IS NULL THEN NULL ELSE dt END),
                SUM(gpu_power*dt)/SUM(CASE WHEN gpu_power IS NULL THEN NULL ELSE dt END),
                SUM(gpu_ded*dt)/SUM(CASE WHEN gpu_ded IS NULL THEN NULL ELSE dt END),
                SUM(gpu_shared*dt)/SUM(CASE WHEN gpu_shared IS NULL THEN NULL ELSE dt END),
                MAX(fan_rpm),
                MAX(dt), SUM(dt)
            FROM sys WHERE ts >= $f AND ts <= $to GROUP BY t ORDER BY t
            """;
        q.Parameters.AddWithValue("$b", b);
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);

        const int N = 24;
        var t = new List<long>();
        var cols = Enumerable.Range(0, N).Select(_ => new List<double?>()).ToArray();
        long prevT = long.MinValue;
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            long ts = r.GetInt64(0);
            long maxDt = r.GetInt64(N + 1);
            // Insert a gap marker when the PC was off / asleep so lines don't bridge it.
            if (prevT != long.MinValue && ts - prevT > Math.Max(3 * b, 3 * maxDt + 5000))
            {
                t.Add(prevT + b);
                foreach (var col in cols) col.Add(null);
            }
            t.Add(ts);
            for (int i = 0; i < N; i++) cols[i].Add(D(r, i + 1) is double v ? Math.Round(v, 2) : null);
            prevT = ts;
        }
        return new
        {
            bucket = b,
            t,
            cpu = cols[0], cpuMax = cols[1],
            mem = cols[2], memMax = cols[3], memTotal = cols[4],
            commit = cols[5], commitLimit = cols[6],
            diskR = cols[7], diskW = cols[8],
            netRx = cols[9], netTx = cols[10],
            gpu = cols[11], gpuMax = cols[12],
            cpuTemp = cols[13], cpuTempMax = cols[14], gpuTemp = cols[15], gpuTempMax = cols[16],
            cpuMhz = cols[17], gpuMhz = cols[18], pkgPower = cols[19], gpuPower = cols[20],
            gpuDed = cols[21], gpuShared = cols[22], fan = cols[23],
        };
    }

    // ---------------- Range summary ----------------

    public object Summary(long from, long to)
    {
        using var c = store.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*), SUM(dt),
                SUM(cpu*dt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE dt END), MAX(cpu_max),
                SUM(mem_used*dt)/SUM(CASE WHEN mem_used IS NULL THEN NULL ELSE dt END), MAX(mem_max), MAX(mem_total),
                SUM(disk_r), SUM(disk_w), SUM(net_rx), SUM(net_tx),
                SUM(gpu*dt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE dt END), MAX(gpu_max),
                MIN(ts), MAX(ts),
                SUM(cpu_temp*dt)/SUM(CASE WHEN cpu_temp IS NULL THEN NULL ELSE dt END), MAX(cpu_temp_max),
                SUM(gpu_temp*dt)/SUM(CASE WHEN gpu_temp IS NULL THEN NULL ELSE dt END), MAX(gpu_temp_max),
                SUM(cpu_mhz*dt)/SUM(CASE WHEN cpu_mhz IS NULL THEN NULL ELSE dt END), MAX(cpu_mhz),
                SUM(gpu_mhz*dt)/SUM(CASE WHEN gpu_mhz IS NULL THEN NULL ELSE dt END), MAX(gpu_mhz),
                SUM(pkg_power*dt)/SUM(CASE WHEN pkg_power IS NULL THEN NULL ELSE dt END), MAX(pkg_power)
            FROM sys WHERE ts >= $f AND ts <= $to
            """;
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        using var r = q.ExecuteReader();
        r.Read();
        return new
        {
            samples = r.GetInt64(0),
            coveredMs = r.IsDBNull(1) ? 0 : r.GetInt64(1),
            cpuAvg = D(r, 2), cpuMax = D(r, 3),
            memAvg = D(r, 4), memMax = D(r, 5), memTotal = D(r, 6),
            diskR = D(r, 7), diskW = D(r, 8), netRx = D(r, 9), netTx = D(r, 10),
            gpuAvg = D(r, 11), gpuMax = D(r, 12),
            first = r.IsDBNull(13) ? (long?)null : r.GetInt64(13),
            last = r.IsDBNull(14) ? (long?)null : r.GetInt64(14),
            cpuTempAvg = D(r, 15), cpuTempMax = D(r, 16), gpuTempAvg = D(r, 17), gpuTempMax = D(r, 18),
            cpuMhzAvg = D(r, 19), cpuMhzMax = D(r, 20), gpuMhzAvg = D(r, 21), gpuMhzMax = D(r, 22),
            pkgPowerAvg = D(r, 23), pkgPowerMax = D(r, 24),
        };
    }

    // ---------------- Processes over a range ----------------

    public object TopProcesses(long from, long to, int limit)
    {
        using var c = store.OpenReader();
        long totalDt;
        using (var q0 = c.CreateCommand())
        {
            q0.CommandText = "SELECT COALESCE(SUM(dt),0) FROM sys WHERE ts >= $f AND ts <= $to";
            q0.Parameters.AddWithValue("$f", from);
            q0.Parameters.AddWithValue("$to", to);
            totalDt = (long)q0.ExecuteScalar()!;
        }
        if (totalDt == 0) return new { totalMs = 0L, items = Array.Empty<object>() };

        using var q = c.CreateCommand();
        // x = hours since range start, y = MB; linear regression gives memory growth (MB/hour).
        q.CommandText = """
            SELECT p.nid, n.name, n.descr, n.path, n.icon IS NOT NULL,
                SUM(p.mem*p.pdt)*1.0/SUM(p.pdt), MAX(COALESCE(p.mem_max, p.mem)),
                SUM(p.cpu*p.pdt)/$tot, MAX(COALESCE(p.cpu_max, p.cpu)),
                SUM(p.io_r), SUM(p.io_w), SUM(p.net_rx), SUM(p.net_tx),
                SUM(p.gpu*p.pdt)/$tot,
                SUM(p.pdt)*1.0/$tot, MAX(p.cnt), MIN(p.ts), MAX(p.ts),
                COUNT(*),
                SUM((p.ts-$f)/3600000.0), SUM(p.mem/1048576.0),
                SUM((p.ts-$f)/3600000.0 * p.mem/1048576.0),
                SUM(((p.ts-$f)/3600000.0)*((p.ts-$f)/3600000.0)),
                SUM((p.mem/1048576.0)*(p.mem/1048576.0)),
                SUM(p.mem*p.pdt)
            FROM proc p JOIN names n ON n.id = p.nid
            WHERE p.ts >= $f AND p.ts <= $to
            GROUP BY p.nid
            ORDER BY SUM(p.mem*p.pdt) DESC
            LIMIT $lim
            """;
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        q.Parameters.AddWithValue("$tot", (double)totalDt);
        q.Parameters.AddWithValue("$lim", limit);

        var items = new List<object>();
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            double n = r.GetInt64(18);
            double sx = r.GetDouble(19), sy = r.GetDouble(20), sxy = r.GetDouble(21), sxx = r.GetDouble(22), syy = r.GetDouble(23);
            double denX = n * sxx - sx * sx, denY = n * syy - sy * sy;
            double slope = denX > 1e-9 ? (n * sxy - sx * sy) / denX : 0;
            double r2 = denX > 1e-9 && denY > 1e-9 ? Math.Pow(n * sxy - sx * sy, 2) / (denX * denY) : 0;
            double spanH = (r.GetInt64(17) - r.GetInt64(16)) / 3_600_000.0;

            items.Add(new
            {
                nid = r.GetInt64(0),
                name = r.GetString(1),
                descr = r.IsDBNull(2) ? null : r.GetString(2),
                path = r.IsDBNull(3) ? null : r.GetString(3),
                hasIcon = r.GetBoolean(4),
                memAvg = D(r, 5), memMax = D(r, 6),
                cpuAvg = D(r, 7) is double ca ? Math.Round(ca, 3) : (double?)null,
                cpuMax = D(r, 8),
                ioR = D(r, 9), ioW = D(r, 10), netRx = D(r, 11), netTx = D(r, 12),
                gpuAvg = D(r, 13) is double ga ? Math.Round(ga, 3) : (double?)null,
                presence = Math.Round(D(r, 14) ?? 0, 3),
                instances = r.IsDBNull(15) ? 0 : r.GetInt32(15),
                first = r.GetInt64(16), last = r.GetInt64(17),
                memSlopeMBh = R1(slope), memR2 = Math.Round(r2, 3), spanHours = Math.Round(spanH, 2),
                memArea = D(r, 24),
            });
        }
        return new { totalMs = totalDt, items };
    }

    // ---------------- One process ----------------

    public object ProcessSeries(long nid, long from, long to, int points)
    {
        long b = Bucket(from, to, points);
        using var c = store.OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT ts/$b*$b AS t,
                SUM(mem*pdt)*1.0/SUM(pdt), MAX(COALESCE(mem_max, mem)),
                SUM(cpu*pdt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE pdt END), MAX(COALESCE(cpu_max, cpu)),
                SUM(io_r)*1000.0/SUM(pdt), SUM(io_w)*1000.0/SUM(pdt),
                SUM(net_rx)*1000.0/SUM(pdt), SUM(net_tx)*1000.0/SUM(pdt),
                SUM(gpu*pdt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE pdt END),
                MAX(cnt), SUM(ws*pdt)*1.0/SUM(pdt), SUM(commit_b*pdt)*1.0/SUM(pdt), MAX(pdt)
            FROM proc WHERE nid=$nid AND ts >= $f AND ts <= $to GROUP BY t ORDER BY t
            """;
        q.Parameters.AddWithValue("$b", b);
        q.Parameters.AddWithValue("$nid", nid);
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        var t = new List<long>();
        var cols = Enumerable.Range(0, 12).Select(_ => new List<double?>()).ToArray();
        long prevT = long.MinValue;
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            long ts = r.GetInt64(0);
            long maxDt = r.GetInt64(13);
            if (prevT != long.MinValue && ts - prevT > Math.Max(3 * b, 3 * maxDt + 5000))
            {
                t.Add(prevT + b);
                foreach (var col in cols) col.Add(null);
            }
            t.Add(ts);
            for (int i = 0; i < 12; i++) cols[i].Add(D(r, i + 1) is double v ? Math.Round(v, 2) : null);
            prevT = ts;
        }
        string? name = null, descr = null, path = null;
        using (var qn = c.CreateCommand())
        {
            qn.CommandText = "SELECT name, descr, path FROM names WHERE id=$id";
            qn.Parameters.AddWithValue("$id", nid);
            using var rn = qn.ExecuteReader();
            if (rn.Read()) { name = rn.GetString(0); descr = rn.IsDBNull(1) ? null : rn.GetString(1); path = rn.IsDBNull(2) ? null : rn.GetString(2); }
        }
        return new
        {
            nid, name, descr, path, bucket = b, t,
            mem = cols[0], memMax = cols[1], cpu = cols[2], cpuMax = cols[3],
            ioR = cols[4], ioW = cols[5], netRx = cols[6], netTx = cols[7],
            gpu = cols[8], count = cols[9], ws = cols[10], commit = cols[11],
        };
    }

    // ---------------- Insights ----------------

    List<object> TopAt(SqliteConnection c, long from, long to, string orderExpr, int n)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"""
            SELECT p.nid, n.name, n.descr, n.icon IS NOT NULL,
                SUM(p.mem*p.pdt)*1.0/SUM(p.pdt), SUM(p.cpu*p.pdt)/SUM(p.pdt), SUM(COALESCE(p.io_r,0)+COALESCE(p.io_w,0)),
                SUM(COALESCE(p.net_rx,0)+COALESCE(p.net_tx,0))
            FROM proc p JOIN names n ON n.id=p.nid
            WHERE p.ts >= $f AND p.ts <= $to GROUP BY p.nid ORDER BY {orderExpr} DESC LIMIT $n
            """;
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        q.Parameters.AddWithValue("$n", n);
        var list = new List<object>();
        using var r = q.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                nid = r.GetInt64(0), name = r.GetString(1), descr = r.IsDBNull(2) ? null : r.GetString(2), hasIcon = r.GetBoolean(3),
                mem = D(r, 4), cpu = D(r, 5), io = D(r, 6), net = D(r, 7),
            });
        return list;
    }

    public object Insights(long from, long to, int memAlertPercent)
    {
        using var c = store.OpenReader();

        // Peak RAM moment and peak CPU moment
        object? Peak(string col)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT ts, dt, {col}, mem_total FROM sys WHERE ts >= $f AND ts <= $to AND {col} IS NOT NULL ORDER BY {col} DESC LIMIT 1";
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            if (!r.Read()) return null;
            long ts = r.GetInt64(0);
            return new { ts, value = r.GetDouble(2), memTotal = D(r, 3), top = TopInstancesAt(c, ts, col.StartsWith("mem") ? "mem" : "cpu", 8) };
        }

        var peakMem = Peak("mem_max");
        var peakCpu = Peak("cpu_max");

        // Episodes where memory load >= threshold
        var episodes = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT ts, dt, mem_max*100.0/mem_total FROM sys WHERE ts >= $f AND ts <= $to AND mem_total > 0 ORDER BY ts";
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            long? start = null; long end = 0, lastTs = 0; double peak = 0;
            var raw = new List<(long s, long e, double p)>();
            while (r.Read())
            {
                long ts = r.GetInt64(0), dt = r.GetInt64(1);
                double pct = r.GetDouble(2);
                bool gap = start is not null && ts - lastTs > 3 * dt + 5000;
                if (pct >= memAlertPercent && !gap)
                {
                    start ??= ts;
                    end = ts + dt; peak = Math.Max(peak, pct);
                }
                else
                {
                    if (start is not null) raw.Add((start.Value, end, peak));
                    start = null; peak = 0;
                    if (pct >= memAlertPercent) { start = ts; end = ts + dt; peak = pct; }
                }
                lastTs = ts;
            }
            if (start is not null) raw.Add((start.Value, end, peak));
            foreach (var e in raw.OrderByDescending(x => x.e - x.s).Take(8).OrderByDescending(x => x.s))
                episodes.Add(new { start = e.s, end = e.e, peak = Math.Round(e.p, 1), top = TopAt(c, e.s, e.e, "SUM(p.mem*p.pdt)", 4) });
        }

        return new
        {
            peakMem, peakCpu, episodes,
            topCpu = TopAt(c, from, to, "SUM(p.cpu*p.pdt)", 5),
            topDisk = TopAt(c, from, to, "SUM(COALESCE(p.io_r,0)+COALESCE(p.io_w,0))", 5),
            topNet = TopAt(c, from, to, "SUM(COALESCE(p.net_rx,0)+COALESCE(p.net_tx,0))", 5),
        };
    }

    // ---------------- CSV export ----------------

    public void ExportCsv(long from, long to, string path, bool processes)
    {
        using var c = store.OpenReader();
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        using var q = c.CreateCommand();
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        if (!processes)
        {
            w.WriteLine("time,interval_s,cpu_pct,gpu_pct,ram_used_mb,ram_total_mb,commit_mb,disk_read_bytes,disk_write_bytes,net_rx_bytes,net_tx_bytes,processes");
            q.CommandText = "SELECT ts,dt,cpu,gpu,mem_used,mem_total,commit_used,disk_r,disk_w,net_rx,net_tx,procs FROM sys WHERE ts >= $f AND ts <= $to ORDER BY ts";
            using var r = q.ExecuteReader();
            while (r.Read())
                w.WriteLine(string.Join(',',
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.GetInt64(1) / 1000.0, Cell(r, 2), Cell(r, 3), Mb(r, 4), Mb(r, 5), Mb(r, 6),
                    Cell(r, 7), Cell(r, 8), Cell(r, 9), Cell(r, 10), Cell(r, 11)));
        }
        else
        {
            w.WriteLine("time,process,instances,cpu_pct,gpu_pct,memory_mb,working_set_mb,commit_mb,io_read_bytes,io_write_bytes,net_rx_bytes,net_tx_bytes");
            q.CommandText = """
                SELECT p.ts, n.name, p.cnt, p.cpu, p.gpu, p.mem, p.ws, p.commit_b, p.io_r, p.io_w, p.net_rx, p.net_tx
                FROM proc p JOIN names n ON n.id=p.nid WHERE p.ts >= $f AND p.ts <= $to ORDER BY p.ts, p.mem DESC
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
                w.WriteLine(string.Join(',',
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    '"' + r.GetString(1).Replace("\"", "\"\"") + '"', Cell(r, 2), Cell(r, 3), Cell(r, 4), Mb(r, 5), Mb(r, 6), Mb(r, 7),
                    Cell(r, 8), Cell(r, 9), Cell(r, 10), Cell(r, 11)));
        }
    }

    static string Cell(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
    static string Mb(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : (r.GetDouble(i) / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
