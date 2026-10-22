using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

/// <summary>
/// Writes a self-describing JSON report meant to be handed to an AI assistant (or any analysis tool).
/// "summary" keeps the timeline to ~1000 rows so it fits in a chat; "full" includes every snapshot.
/// </summary>
internal sealed class Exporter(Store store, Queries queries, AppSettings settings)
{
    public const int SummaryTimelineRows = 500;

    static readonly Dictionary<string, string> RoleNames = new()
    {
        ["main"] = "main process of the application",
        ["renderer"] = "renderer (a browser tab / web page / app view)",
        ["extension"] = "browser extension",
        ["gpu"] = "GPU / graphics process",
        ["network"] = "network service",
        ["storage"] = "storage service",
        ["audio"] = "audio service",
        ["video"] = "video capture service",
        ["media"] = "media decoder",
        ["utility"] = "utility helper process",
        ["crashpad"] = "crash reporter",
        ["service"] = "Windows service host",
        ["script"] = "script / command-line program",
        ["com"] = "COM surrogate (DLL host)",
        ["system"] = "Windows system process",
        ["app"] = "process",
    };

    static double Mb(double? bytes) => bytes is null ? 0 : Math.Round(bytes.Value / 1048576.0, 1);
    static double R(double? v, int d = 1) => v is null ? 0 : Math.Round(v.Value, d);
    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    static JsonElement J(object? o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    static double? Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Role(string? code) => code is null ? "process" : RoleNames.GetValueOrDefault(code, code);

    // Average bytes per item, measured on real exports (window titles add about a fifth to a timeline row).
    const int BaseBytes = 8_000, AppBytes = 340, ProcBytes = 290, MomentBytes = 500, RowBytes = 270, RowBytesNoTitles = 210;

    /// <summary>Approximate file size of both export levels for a range, from row counts only (nothing is built).</summary>
    public object Estimate(long from, long to, bool includeTitles)
    {
        using var c = store.OpenReader();
        long samples, coveredMs;
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*), COALESCE(SUM(dt), 0) FROM sys WHERE ts >= $f AND ts <= $to";
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            r.Read();
            samples = r.GetInt64(0);
            coveredMs = r.GetInt64(1);
        }
        if (samples == 0) return new { samples, summary = 0L, full = 0L };

        // processes per application alive in the range, biggest first (the export caps both counts)
        var perApp = new List<long>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM inst WHERE first_ts <= $to AND last_ts >= $f GROUP BY nid ORDER BY 1 DESC";
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read()) perApp.Add(r.GetInt64(0));
        }

        long Size(bool full)
        {
            int appLimit = full ? 1000 : 40, procLimit = full ? 300 : 8, moments = full ? 25 : 10;
            long rows = full ? samples : Math.Min(samples, SummaryTimelineRows);
            long apps = perApp.Take(appLimit).Sum(n => AppBytes + Math.Min(n, procLimit) * ProcBytes);
            long perMetric = Math.Min(moments, coveredMs / 120_000 + 1); // moments are at least 2 minutes apart
            return BaseBytes + apps + 6 * perMetric * MomentBytes + rows * (includeTitles ? RowBytes : RowBytesNoTitles);
        }
        return new { samples, summary = Size(false), full = Size(true) };
    }

    public void Write(string path, long from, long to, bool full, bool includeTitles)
    {
        using var fs = File.Create(path);
        // Compact output: whitespace only costs tokens when the file is given to an AI.
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var c = store.OpenReader();

        w.WriteStartObject();
        w.WriteString("report", "ResourceLens - Windows resource usage report");
        w.WriteNumber("schema_version", 1);
        w.WriteString("generated_at", Iso(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        w.WriteString("detail_level", full ? "full (every snapshot)" : "summary (timeline averaged to fit an AI chat)");

        WriteGuide(w);

        w.WritePropertyName("machine");
        JsonSerializer.Serialize(w, SystemInfo.Describe());

        // ---- period ----
        var sum = J(queries.Summary(from, to));
        var boots = J(queries.Boots());
        w.WriteStartObject("period");
        w.WriteString("from", Iso(from));
        w.WriteString("to", Iso(to));
        w.WriteNumber("length_minutes", Math.Round((to - from) / 60000.0, 1));
        w.WriteNumber("recorded_minutes", Math.Round((Num(sum, "coveredMs") ?? 0) / 60000.0, 1));
        w.WriteNumber("snapshots", Num(sum, "samples") ?? 0);
        w.WriteNumber("snapshot_interval_seconds", settings.IntervalSeconds);
        w.WriteString("note", "Gaps between snapshots mean the PC was off/asleep or ResourceLens was not running. " +
            $"Snapshots older than {settings.FullResolutionDays} days are stored as 1-minute averages (5-minute after two more weeks).");
        w.WriteStartArray("boot_sessions");
        foreach (var b in boots.GetProperty("boots").EnumerateArray())
        {
            long first = (long)Num(b, "first")!, last = (long)Num(b, "last")!;
            if (last < from || first > to) continue;
            w.WriteStartObject();
            w.WriteString("powered_on_at", Iso((long)Num(b, "boot")!));
            w.WriteString("recorded_from", Iso(first));
            w.WriteString("recorded_to", Iso(last));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("tracking");
        w.WriteBoolean("cpu", settings.TrackCpu);
        w.WriteBoolean("ram", settings.TrackMemory);
        w.WriteBoolean("disk", settings.TrackDisk);
        w.WriteBoolean("network", settings.TrackNetwork);
        w.WriteBoolean("gpu", settings.TrackGpu);
        w.WriteBoolean("per_process_network", settings.PerProcessNetwork && Native.IsUserAnAdmin());
        w.WriteNumber("applications_stored_per_metric_per_snapshot", settings.TopProcesses);
        w.WriteNumber("processes_stored_per_metric_per_snapshot", settings.TopInstances);
        w.WriteBoolean("window_titles_included", includeTitles);
        w.WriteEndObject();

        // ---- system totals ----
        WriteSystem(w, c, from, to, sum);

        // ---- findings ----
        WriteFindings(w, c, from, to, includeTitles);

        // ---- foreground usage: what the user was doing ----
        WriteForeground(w, c, from, to, includeTitles);

        // ---- applications with their processes ----
        WriteApplications(w, from, to, full ? 1000 : 40, full ? 300 : 8, includeTitles);

        // ---- top moments per metric ----
        w.WriteStartObject("top_moments");
        w.WriteString("note", "The highest snapshots for each resource (at least 2 minutes apart), with the foreground window and the processes responsible at that exact moment.");
        foreach (var (key, metric, unit) in new[] { ("cpu", "cpu", "pct"), ("ram", "mem", "mb"), ("disk", "disk", "mb_per_s"), ("network", "net", "mb_per_s"), ("gpu", "gpu", "pct"), ("temperature", "temp", "c") })
            WriteMoments(w, key, metric, unit, from, to, full ? 25 : 10, includeTitles);
        w.WriteEndObject();

        // ---- timeline ----
        WriteTimeline(w, c, from, to, full, includeTitles);

        w.WriteEndObject();
    }

    static void WriteGuide(Utf8JsonWriter w)
    {
        w.WriteStartObject("how_to_read");
        w.WriteString("purpose", "Find what consumed CPU, RAM, disk, network or GPU, when, and which application/process caused it. Please analyse and give practical advice.");
        w.WriteString("cpu_pct", "Share of the whole CPU (all cores) 0-100, like Task Manager.");
        w.WriteString("ram_mb", "For processes: private working set in MB (the 'Memory' column of Task Manager). For the system: physical RAM in use.");
        w.WriteString("commit_mb", "Committed virtual memory (RAM + page file) promised to programs; if it approaches the commit limit, programs can fail.");
        w.WriteString("disk_mb / disk_kbps", "Process I/O bytes (files, includes cached I/O); system values are physical disk throughput.");
        w.WriteString("net_mb / net_kbps", "Bytes received (down) and sent (up). Per-process network needs administrator mode.");
        w.WriteString("gpu_pct", "Busiest GPU engine utilisation, like Task Manager.");
        w.WriteString("avg / peak", "avg = time-weighted average over the period while the item was recorded; peak = highest single snapshot.");
        w.WriteString("presence_pct", "Share of the period in which the item was among the top consumers stored each snapshot (low = usually idle/small).");
        w.WriteString("ram_growth_mb_per_hour", "Linear trend of the process' RAM over the period; steady positive growth with a high trend_fit (0-1) suggests a memory leak.");
        w.WriteString("role", "What a process does inside its application (e.g. browser tab renderer, GPU process, Windows service host, script).");
        w.WriteEndObject();
    }

    void WriteSystem(Utf8JsonWriter w, SqliteConnection c, long from, long to, JsonElement sum)
    {
        var cpu = new List<double>(); var mem = new List<double>(); var gpu = new List<double>();
        double memTotal = 0, commitLimit = 0, commitMax = 0, commitSum = 0, commitDt = 0;
        double maxDiskR = 0, maxDiskW = 0, maxNetRx = 0, maxNetTx = 0, above80 = 0, above90 = 0;
        long cpuMaxAt = 0, memMaxAt = 0, gpuMaxAt = 0; double cpuMax = -1, memMax = -1, gpuMax = -1;
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT ts, dt, cpu, cpu_max, mem_used, mem_max, mem_total, commit_used, commit_limit,
                  disk_r*1000.0/dt, disk_w*1000.0/dt, net_rx*1000.0/dt, net_tx*1000.0/dt, gpu, gpu_max
                FROM sys WHERE ts >= $f AND ts <= $to
                """;
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                long ts = r.GetInt64(0); double dt = r.GetInt64(1);
                if (!r.IsDBNull(2)) cpu.Add(r.GetDouble(2));
                if (!r.IsDBNull(3) && r.GetDouble(3) > cpuMax) { cpuMax = r.GetDouble(3); cpuMaxAt = ts; }
                if (!r.IsDBNull(4)) mem.Add(r.GetDouble(4));
                if (!r.IsDBNull(5) && r.GetDouble(5) > memMax) { memMax = r.GetDouble(5); memMaxAt = ts; }
                if (!r.IsDBNull(6)) memTotal = Math.Max(memTotal, r.GetDouble(6));
                if (!r.IsDBNull(7)) { commitSum += r.GetDouble(7) * dt; commitDt += dt; commitMax = Math.Max(commitMax, r.GetDouble(7)); }
                if (!r.IsDBNull(8)) commitLimit = Math.Max(commitLimit, r.GetDouble(8));
                if (!r.IsDBNull(9)) maxDiskR = Math.Max(maxDiskR, r.GetDouble(9));
                if (!r.IsDBNull(10)) maxDiskW = Math.Max(maxDiskW, r.GetDouble(10));
                if (!r.IsDBNull(11)) maxNetRx = Math.Max(maxNetRx, r.GetDouble(11));
                if (!r.IsDBNull(12)) maxNetTx = Math.Max(maxNetTx, r.GetDouble(12));
                if (!r.IsDBNull(13)) gpu.Add(r.GetDouble(13));
                if (!r.IsDBNull(14) && r.GetDouble(14) > gpuMax) { gpuMax = r.GetDouble(14); gpuMaxAt = ts; }
                if (memTotal > 0 && !r.IsDBNull(5))
                {
                    double pct = r.GetDouble(5) * 100 / memTotal;
                    if (pct >= 80) above80 += dt;
                    if (pct >= 90) above90 += dt;
                }
            }
        }
        static double P(List<double> v, double p)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            return v[(int)Math.Clamp(Math.Round(p * (v.Count - 1)), 0, v.Count - 1)];
        }

        w.WriteStartObject("system");
        w.WriteStartObject("cpu_pct");
        w.WriteNumber("avg", R(Num(sum, "cpuAvg")));
        w.WriteNumber("median", R(P(cpu, 0.5)));
        w.WriteNumber("p95", R(P(cpu, 0.95)));
        w.WriteNumber("peak", R(cpuMax));
        if (cpuMaxAt > 0) w.WriteString("peak_at", Iso(cpuMaxAt));
        w.WriteEndObject();

        w.WriteStartObject("ram");
        w.WriteNumber("total_mb", Mb(memTotal));
        w.WriteNumber("used_avg_mb", Mb(Num(sum, "memAvg")));
        w.WriteNumber("used_p95_mb", Mb(P(mem, 0.95)));
        w.WriteNumber("used_peak_mb", Mb(memMax));
        if (memTotal > 0)
        {
            w.WriteNumber("used_avg_pct", R((Num(sum, "memAvg") ?? 0) * 100 / memTotal));
            w.WriteNumber("used_peak_pct", R(memMax * 100 / memTotal));
        }
        if (memMaxAt > 0) w.WriteString("peak_at", Iso(memMaxAt));
        w.WriteNumber("minutes_above_80_pct", Math.Round(above80 / 60000, 1));
        w.WriteNumber("minutes_above_90_pct", Math.Round(above90 / 60000, 1));
        w.WriteNumber("commit_limit_mb", Mb(commitLimit));
        w.WriteNumber("commit_avg_mb", Mb(commitDt > 0 ? commitSum / commitDt : 0));
        w.WriteNumber("commit_peak_mb", Mb(commitMax));
        w.WriteEndObject();

        w.WriteStartObject("disk");
        w.WriteNumber("read_total_mb", Mb(Num(sum, "diskR")));
        w.WriteNumber("write_total_mb", Mb(Num(sum, "diskW")));
        w.WriteNumber("read_peak_mb_per_s", Mb(maxDiskR));
        w.WriteNumber("write_peak_mb_per_s", Mb(maxDiskW));
        w.WriteEndObject();

        w.WriteStartObject("network");
        w.WriteNumber("down_total_mb", Mb(Num(sum, "netRx")));
        w.WriteNumber("up_total_mb", Mb(Num(sum, "netTx")));
        w.WriteNumber("down_peak_mb_per_s", Math.Round(maxNetRx / 1048576.0, 2));
        w.WriteNumber("up_peak_mb_per_s", Math.Round(maxNetTx / 1048576.0, 2));
        w.WriteEndObject();

        w.WriteStartObject("gpu_pct");
        w.WriteNumber("avg", R(Num(sum, "gpuAvg")));
        w.WriteNumber("p95", R(P(gpu, 0.95)));
        w.WriteNumber("peak", R(gpuMax));
        if (gpuMaxAt > 0) w.WriteString("peak_at", Iso(gpuMaxAt));
        w.WriteEndObject();

        if (Num(sum, "cpuTempMax") is not null || Num(sum, "gpuTempMax") is not null)
        {
            w.WriteStartObject("temperature_c");
            if (Num(sum, "cpuTempAvg") is double cta) { w.WriteNumber("cpu_avg", R(cta)); w.WriteNumber("cpu_peak", R(Num(sum, "cpuTempMax"))); }
            if (Num(sum, "gpuTempAvg") is double gta) { w.WriteNumber("gpu_avg", R(gta)); w.WriteNumber("gpu_peak", R(Num(sum, "gpuTempMax"))); }
            w.WriteNumber("minutes_cpu_or_gpu_at_or_above_85c", TempMinutes(c, from, to, 85));
            w.WriteEndObject();
        }
        if (Num(sum, "cpuMhzAvg") is not null || Num(sum, "gpuMhzAvg") is not null)
        {
            w.WriteStartObject("clock_mhz");
            if (Num(sum, "cpuMhzAvg") is double cm) { w.WriteNumber("cpu_avg", Math.Round(cm)); w.WriteNumber("cpu_peak", Math.Round(Num(sum, "cpuMhzMax") ?? 0)); }
            if (Num(sum, "gpuMhzAvg") is double gm) { w.WriteNumber("gpu_avg", Math.Round(gm)); w.WriteNumber("gpu_peak", Math.Round(Num(sum, "gpuMhzMax") ?? 0)); }
            w.WriteEndObject();
        }
        if (Num(sum, "pkgPowerAvg") is double pa)
        {
            w.WriteStartObject("power_w");
            w.WriteNumber("package_avg", R(pa));
            w.WriteNumber("package_peak", R(Num(sum, "pkgPowerMax")));
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    static double TempMinutes(SqliteConnection c, long from, long to, double limit)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(SUM(dt),0) FROM sys WHERE ts >= $f AND ts <= $to AND MAX(COALESCE(cpu_temp_max,0), COALESCE(gpu_temp_max,0)) >= $l";
        q.Parameters.AddWithValue("$f", from);
        q.Parameters.AddWithValue("$to", to);
        q.Parameters.AddWithValue("$l", limit);
        return Math.Round(Convert.ToDouble(q.ExecuteScalar()) / 60000.0, 1);
    }

    static void WriteProc(Utf8JsonWriter w, JsonElement p, string valueKey, string valueName, Func<double?, double> conv, bool includeTitles)
    {
        w.WriteStartObject();
        w.WriteString("app", Str(p, "name"));
        if (Str(p, "descr") is { } d) w.WriteString("description", d);
        if (Str(p, "role") is { } role) w.WriteString("role", Role(role));
        if (Str(p, "detail") is { } det) w.WriteString("detail", det);
        if (Num(p, "pid") is { } pid) w.WriteNumber("pid", (long)pid);
        if (includeTitles && Str(p, "title") is { } t) w.WriteString("window_title", t);
        w.WriteNumber(valueName, conv(Num(p, valueKey)));
        w.WriteEndObject();
    }

    void WriteFindings(Utf8JsonWriter w, SqliteConnection c, long from, long to, bool includeTitles)
    {
        var ins = J(queries.Insights(from, to, settings.AlertMemoryPercent));
        var procs = J(queries.TopProcesses(from, to, 1000));
        w.WriteStartObject("findings");

        foreach (var (key, prop, metric) in new[] { ("ram_peak", "peakMem", "mem"), ("cpu_peak", "peakCpu", "cpu") })
        {
            if (!ins.TryGetProperty(prop, out var pk) || pk.ValueKind != JsonValueKind.Object) continue;
            w.WriteStartObject(key);
            long ts = (long)Num(pk, "ts")!;
            w.WriteString("at", Iso(ts));
            if (metric == "mem")
            {
                w.WriteNumber("used_mb", Mb(Num(pk, "value")));
                if (Num(pk, "memTotal") is double tot and > 0) w.WriteNumber("used_pct", R(Num(pk, "value") * 100 / tot));
            }
            else w.WriteNumber("cpu_pct", R(Num(pk, "value")));
            w.WriteStartArray("top_processes_at_that_moment");
            foreach (var p in pk.GetProperty("top").EnumerateArray())
                WriteProc(w, p, metric, metric == "mem" ? "ram_mb" : "cpu_pct", metric == "mem" ? Mb : v => R(v), includeTitles);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteStartArray("memory_pressure_episodes");
        foreach (var e in ins.GetProperty("episodes").EnumerateArray())
        {
            w.WriteStartObject();
            long s = (long)Num(e, "start")!, en = (long)Num(e, "end")!;
            w.WriteString("start", Iso(s));
            w.WriteString("end", Iso(en));
            w.WriteNumber("minutes", Math.Round((en - s) / 60000.0, 1));
            w.WriteNumber("peak_ram_pct", R(Num(e, "peak")));
            w.WriteNumber("threshold_pct", settings.AlertMemoryPercent);
            w.WriteStartArray("top_apps");
            foreach (var p in e.GetProperty("top").EnumerateArray()) WriteProc(w, p, "mem", "avg_ram_mb", Mb, false);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("possible_memory_leaks");
        foreach (var p in procs.GetProperty("items").EnumerateArray())
        {
            double slope = Num(p, "memSlopeMBh") ?? 0, r2 = Num(p, "memR2") ?? 0, span = Num(p, "spanHours") ?? 0, pres = Num(p, "presence") ?? 0;
            if (!(slope >= 20 && r2 >= 0.6 && span >= 0.5 && pres >= 0.5)) continue;
            w.WriteStartObject();
            w.WriteString("app", Str(p, "name"));
            if (Str(p, "descr") is { } d) w.WriteString("description", d);
            w.WriteNumber("ram_growth_mb_per_hour", R(slope));
            w.WriteNumber("trend_fit", Math.Round(r2, 2));
            w.WriteNumber("ram_avg_mb", Mb(Num(p, "memAvg")));
            w.WriteNumber("ram_peak_mb", Mb(Num(p, "memMax")));
            w.WriteNumber("observed_hours", R(span, 2));
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("alerts_fired");
        foreach (var e in J(queries.Events(from, to)).EnumerateArray())
        {
            w.WriteStartObject();
            w.WriteString("at", Iso((long)Num(e, "ts")!));
            w.WriteString("kind", Str(e, "kind") == "mem" ? "high RAM" : "high CPU");
            w.WriteNumber("value_pct", R(Num(e, "value")));
            if (e.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Array)
            {
                w.WriteStartArray("top_processes");
                foreach (var p in top.EnumerateArray())
                {
                    w.WriteStartObject();
                    w.WriteString("app", Str(p, "app"));
                    if (Str(p, "role") is { } role) w.WriteString("role", Role(role));
                    if (Str(p, "detail") is { } det) w.WriteString("detail", det);
                    w.WriteNumber("ram_mb", Mb(Num(p, "mem")));
                    w.WriteNumber("cpu_pct", R(Num(p, "cpu")));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    void WriteForeground(Utf8JsonWriter w, SqliteConnection c, long from, long to, bool includeTitles)
    {
        w.WriteStartObject("foreground_activity");
        w.WriteString("note", "Which application/window was in front (what the user was doing) and for how long.");
        w.WriteStartArray("by_app");
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT n.name, n.descr, SUM(s.dt), AVG(s.cpu), AVG(s.mem_used)
                FROM sys s JOIN inst i ON i.id = s.fg_iid JOIN names n ON n.id = i.nid
                WHERE s.ts >= $f AND s.ts <= $to GROUP BY n.id ORDER BY 3 DESC LIMIT 25
                """;
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                w.WriteStartObject();
                w.WriteString("app", r.GetString(0));
                if (!r.IsDBNull(1)) w.WriteString("description", r.GetString(1));
                w.WriteNumber("minutes", Math.Round(r.GetInt64(2) / 60000.0, 1));
                w.WriteNumber("system_cpu_avg_pct_meanwhile", R(r.IsDBNull(3) ? null : r.GetDouble(3)));
                w.WriteNumber("system_ram_avg_mb_meanwhile", Mb(r.IsDBNull(4) ? null : r.GetDouble(4)));
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        if (includeTitles)
        {
            w.WriteStartArray("by_window_title");
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT st.text, n.name, SUM(s.dt) FROM sys s JOIN strings st ON st.id = s.fg_title
                LEFT JOIN inst i ON i.id = s.fg_iid LEFT JOIN names n ON n.id = i.nid
                WHERE s.ts >= $f AND s.ts <= $to GROUP BY s.fg_title ORDER BY 3 DESC LIMIT 30
                """;
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                w.WriteStartObject();
                w.WriteString("window_title", r.GetString(0));
                if (!r.IsDBNull(1)) w.WriteString("app", r.GetString(1));
                w.WriteNumber("minutes", Math.Round(r.GetInt64(2) / 60000.0, 1));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    void WriteApplications(Utf8JsonWriter w, long from, long to, int appLimit, int procLimit, bool includeTitles)
    {
        var res = J(queries.TopProcesses(from, to, appLimit));
        var instances = queries.InstancesByApp(null, from, to);
        w.WriteStartObject("applications");
        w.WriteString("note", "Sorted by RAM used over time. Each application lists its individual processes (PIDs) with what they are.");
        w.WriteStartArray("items");
        foreach (var p in res.GetProperty("items").EnumerateArray())
        {
            w.WriteStartObject();
            w.WriteString("app", Str(p, "name"));
            if (Str(p, "descr") is { } d) w.WriteString("description", d);
            if (Str(p, "path") is { } path) w.WriteString("path", path);
            w.WriteNumber("max_simultaneous_processes", Num(p, "instances") ?? 0);
            w.WriteNumber("presence_pct", R((Num(p, "presence") ?? 0) * 100));
            w.WriteNumber("ram_avg_mb", Mb(Num(p, "memAvg")));
            w.WriteNumber("ram_peak_mb", Mb(Num(p, "memMax")));
            if ((Num(p, "spanHours") ?? 0) >= 0.25) { w.WriteNumber("ram_growth_mb_per_hour", R(Num(p, "memSlopeMBh"))); w.WriteNumber("trend_fit", Math.Round(Num(p, "memR2") ?? 0, 2)); }
            w.WriteNumber("cpu_avg_pct", R(Num(p, "cpuAvg"), 2));
            w.WriteNumber("cpu_peak_pct", R(Num(p, "cpuMax")));
            w.WriteNumber("disk_read_mb", Mb(Num(p, "ioR")));
            w.WriteNumber("disk_write_mb", Mb(Num(p, "ioW")));
            if (Num(p, "netRx") is not null) { w.WriteNumber("net_down_mb", Mb(Num(p, "netRx"))); w.WriteNumber("net_up_mb", Mb(Num(p, "netTx"))); }
            if ((Num(p, "gpuAvg") ?? 0) > 0) w.WriteNumber("gpu_avg_pct", R(Num(p, "gpuAvg"), 2));

            var inst = J(instances.GetValueOrDefault((long)Num(p, "nid")!) ?? new List<object>());
            if (inst.GetArrayLength() > 0)
            {
                w.WriteStartArray("processes");
                foreach (var i in inst.EnumerateArray().Take(procLimit))
                {
                    w.WriteStartObject();
                    if (Num(i, "pid") is { } pid) w.WriteNumber("pid", (long)pid);
                    w.WriteString("role", Role(Str(i, "role")));
                    if (Str(i, "detail") is { } det) w.WriteString("detail", det);
                    if (includeTitles && Str(i, "title") is { } t) w.WriteString("last_window_title", t);
                    if (Num(i, "created") is { } cr and > 0) w.WriteString("started_at", Iso((long)cr));
                    w.WriteNumber("ram_avg_mb", Mb(Num(i, "memAvg")));
                    w.WriteNumber("ram_peak_mb", Mb(Num(i, "memMax")));
                    w.WriteNumber("cpu_avg_pct", R(Num(i, "cpuAvg"), 2));
                    w.WriteNumber("cpu_peak_pct", R(Num(i, "cpuMax")));
                    w.WriteNumber("disk_read_mb", Mb(Num(i, "ioR")));
                    w.WriteNumber("disk_write_mb", Mb(Num(i, "ioW")));
                    if (Num(i, "netRx") is not null) { w.WriteNumber("net_down_mb", Mb(Num(i, "netRx"))); w.WriteNumber("net_up_mb", Mb(Num(i, "netTx"))); }
                    w.WriteNumber("presence_pct", R((Num(i, "presence") ?? 0) * 100));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    void WriteMoments(Utf8JsonWriter w, string key, string metric, string unit, long from, long to, int count, bool includeTitles)
    {
        var res = J(queries.Moments(metric, from, to, "top", 0, 400, null));
        var picked = new List<JsonElement>();
        foreach (var m in res.GetProperty("rows").EnumerateArray())
        {
            long ts = (long)Num(m, "ts")!;
            if ((Num(m, "value") ?? 0) <= 0) break;
            if (picked.Any(p => Math.Abs((long)Num(p, "ts")! - ts) < 120_000)) continue;
            picked.Add(m);
            if (picked.Count >= count) break;
        }
        Func<double?, double> conv = unit switch { "mb" => Mb, "mb_per_s" => v => Math.Round((v ?? 0) / 1048576.0, 2), _ => v => R(v) };
        string valueName = $"{key}_{unit}";
        string procMetric = metric switch { "disk" => "io", "net" => "net", "temp" => "cpu", _ => metric };
        if (metric == "temp") valueName = "temperature_c";
        w.WriteStartArray(key);
        foreach (var m in picked)
        {
            w.WriteStartObject();
            w.WriteString("at", Iso((long)Num(m, "ts")!));
            w.WriteNumber(valueName, conv(Num(m, "value")));
            if (m.TryGetProperty("fg", out var fg) && fg.ValueKind == JsonValueKind.Object)
            {
                w.WriteString("foreground_app", Str(fg, "name"));
                if (includeTitles && Str(fg, "title") is { } t) w.WriteString("foreground_title", t);
            }
            w.WriteStartArray("responsible_processes");
            foreach (var p in m.GetProperty("top").EnumerateArray())
                if (metric == "temp") WriteProc(w, p, "cpu", "cpu_pct", v => R(v), includeTitles);
                else WriteProc(w, p, procMetric, valueName, conv, includeTitles);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    void WriteTimeline(Utf8JsonWriter w, SqliteConnection c, long from, long to, bool full, bool includeTitles)
    {
        // biggest CPU and RAM process for every snapshot
        var topCpu = new Dictionary<long, string>();
        var topMem = new Dictionary<long, string>();
        foreach (var (dict, col) in new[] { (topCpu, "COALESCE(cpu_max, cpu)"), (topMem, "COALESCE(mem_max, mem)") })
        {
            using var q = c.CreateCommand();
            q.CommandText = $"""
                SELECT x.ts, n.name, i.role, i.detail, x.v FROM (
                  SELECT ts, iid, {col} AS v, ROW_NUMBER() OVER (PARTITION BY ts ORDER BY {col} DESC) AS rn
                  FROM iproc WHERE ts >= $f AND ts <= $to) x
                JOIN inst i ON i.id = x.iid JOIN names n ON n.id = i.nid WHERE x.rn = 1
                """;
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                string label = r.GetString(1);
                if (!r.IsDBNull(2) && r.GetString(2) is not ("app" or "system")) label += " / " + r.GetString(2);
                if (!r.IsDBNull(3)) label += " (" + Trim(r.GetString(3), 60) + ")";
                label += dict == topCpu ? $" {R(r.IsDBNull(4) ? null : r.GetDouble(4))}%" : $" {Mb(r.IsDBNull(4) ? null : r.GetDouble(4))} MB";
                dict[r.GetInt64(0)] = label;
            }
        }

        long count;
        using (var qc = c.CreateCommand())
        {
            qc.CommandText = "SELECT COUNT(*) FROM sys WHERE ts >= $f AND ts <= $to";
            qc.Parameters.AddWithValue("$f", from);
            qc.Parameters.AddWithValue("$to", to);
            count = (long)qc.ExecuteScalar()!;
        }
        int group = full ? 1 : (int)Math.Max(1, Math.Ceiling(count / (double)SummaryTimelineRows));

        w.WriteStartObject("timeline");
        w.WriteString("note", group == 1
            ? "One row per snapshot."
            : $"Each row averages {group} consecutive snapshots (peak columns keep the highest one); 'top_*' columns describe the snapshot with the highest CPU/RAM inside the row.");
        w.WriteStartArray("columns");
        foreach (var col in new[] { "time", "cpu_pct", "cpu_peak_pct", "ram_used_mb", "ram_peak_mb", "commit_mb", "disk_read_kb_per_s", "disk_write_kb_per_s",
                     "net_down_kb_per_s", "net_up_kb_per_s", "gpu_pct", "cpu_temp_c", "gpu_temp_c", "cpu_mhz", "gpu_mhz",
                     "foreground_app", "foreground_window", "top_cpu_process", "top_ram_process" })
            w.WriteStringValue(col);
        w.WriteEndArray();
        w.WriteStartArray("rows");

        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT s.ts, s.dt, s.cpu, s.cpu_max, s.mem_used, s.mem_max, s.commit_used,
                  s.disk_r, s.disk_w, s.net_rx, s.net_tx, s.gpu, n.name, st.text,
                  s.cpu_temp_max, s.gpu_temp_max, s.cpu_mhz, s.gpu_mhz
                FROM sys s LEFT JOIN inst i ON i.id = s.fg_iid LEFT JOIN names n ON n.id = i.nid
                LEFT JOIN strings st ON st.id = s.fg_title
                WHERE s.ts >= $f AND s.ts <= $to ORDER BY s.ts
                """;
            q.Parameters.AddWithValue("$f", from);
            q.Parameters.AddWithValue("$to", to);
            using var r = q.ExecuteReader();

            var acc = new Acc();
            while (r.Read())
            {
                acc.Add(r);
                if (acc.N >= group) { acc.Write(w, includeTitles, topCpu, topMem); acc = new Acc(); }
            }
            if (acc.N > 0) acc.Write(w, includeTitles, topCpu, topMem);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Accumulates consecutive snapshots into one timeline row.</summary>
    sealed class Acc
    {
        public int N;
        long _ts, _dt, _cpuMaxTs, _memMaxTs;
        double _cpuW, _cpuDt, _cpuMax = -1, _memW, _memDt, _memMax = -1, _commitW, _commitDt, _gpuW, _gpuDt;
        double? _dr, _dw, _nr, _nt, _ct, _gt;
        double _cmW, _cmN, _gmW, _gmN;
        readonly Dictionary<string, int> _fgApp = new();
        readonly Dictionary<string, int> _fgTitle = new();

        public void Add(SqliteDataReader r)
        {
            long ts = r.GetInt64(0); double dt = r.GetInt64(1);
            if (N++ == 0) _ts = ts;
            _dt += (long)dt;
            if (!r.IsDBNull(2)) { _cpuW += r.GetDouble(2) * dt; _cpuDt += dt; }
            if (!r.IsDBNull(3) && r.GetDouble(3) > _cpuMax) { _cpuMax = r.GetDouble(3); _cpuMaxTs = ts; }
            if (!r.IsDBNull(4)) { _memW += r.GetDouble(4) * dt; _memDt += dt; }
            if (!r.IsDBNull(5) && r.GetDouble(5) > _memMax) { _memMax = r.GetDouble(5); _memMaxTs = ts; }
            if (!r.IsDBNull(6)) { _commitW += r.GetDouble(6) * dt; _commitDt += dt; }
            if (!r.IsDBNull(7)) _dr = (_dr ?? 0) + r.GetDouble(7);
            if (!r.IsDBNull(8)) _dw = (_dw ?? 0) + r.GetDouble(8);
            if (!r.IsDBNull(9)) _nr = (_nr ?? 0) + r.GetDouble(9);
            if (!r.IsDBNull(10)) _nt = (_nt ?? 0) + r.GetDouble(10);
            if (!r.IsDBNull(11)) { _gpuW += r.GetDouble(11) * dt; _gpuDt += dt; }
            if (!r.IsDBNull(12)) _fgApp[r.GetString(12)] = _fgApp.GetValueOrDefault(r.GetString(12)) + 1;
            if (!r.IsDBNull(13)) _fgTitle[r.GetString(13)] = _fgTitle.GetValueOrDefault(r.GetString(13)) + 1;
            if (!r.IsDBNull(14)) _ct = Math.Max(_ct ?? 0, r.GetDouble(14));
            if (!r.IsDBNull(15)) _gt = Math.Max(_gt ?? 0, r.GetDouble(15));
            if (!r.IsDBNull(16)) { _cmW += r.GetDouble(16); _cmN++; }
            if (!r.IsDBNull(17)) { _gmW += r.GetDouble(17); _gmN++; }
        }

        public void Write(Utf8JsonWriter w, bool titles, Dictionary<long, string> topCpu, Dictionary<long, string> topMem)
        {
            double sec = Math.Max(0.001, _dt / 1000.0);
            void NumOrNull(double? v, int d) { if (v is null) w.WriteNullValue(); else w.WriteNumberValue(Math.Round(v.Value, d)); }
            w.WriteStartArray();
            w.WriteStringValue(Iso(_ts));
            NumOrNull(_cpuDt > 0 ? _cpuW / _cpuDt : null, 1);
            NumOrNull(_cpuMax >= 0 ? _cpuMax : null, 1);
            NumOrNull(_memDt > 0 ? _memW / _memDt / 1048576 : null, 0);
            NumOrNull(_memMax >= 0 ? _memMax / 1048576 : null, 0);
            NumOrNull(_commitDt > 0 ? _commitW / _commitDt / 1048576 : null, 0);
            NumOrNull(_dr / 1024 / sec, 0);
            NumOrNull(_dw / 1024 / sec, 0);
            NumOrNull(_nr / 1024 / sec, 1);
            NumOrNull(_nt / 1024 / sec, 1);
            NumOrNull(_gpuDt > 0 ? _gpuW / _gpuDt : null, 1);
            NumOrNull(_ct, 0);
            NumOrNull(_gt, 0);
            NumOrNull(_cmN > 0 ? _cmW / _cmN : null, 0);
            NumOrNull(_gmN > 0 ? _gmW / _gmN : null, 0);
            if (_fgApp.Count > 0) w.WriteStringValue(_fgApp.MaxBy(x => x.Value).Key); else w.WriteNullValue();
            if (titles && _fgTitle.Count > 0) w.WriteStringValue(Trim(_fgTitle.MaxBy(x => x.Value).Key, 90)); else w.WriteNullValue();
            if (topCpu.TryGetValue(_cpuMaxTs, out var tc)) w.WriteStringValue(tc); else w.WriteNullValue();
            if (topMem.TryGetValue(_memMaxTs, out var tm)) w.WriteStringValue(tm); else w.WriteNullValue();
            w.WriteEndArray();
        }
    }
}
