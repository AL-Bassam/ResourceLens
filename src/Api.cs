using System.Text.Json;

namespace ResourceLens;

/// <summary>Data methods shared by the WebView2 bridge and the debug dev server.</summary>
internal sealed class Api(TrayApp app)
{
    static long L(JsonElement p, string name) => p.GetProperty(name).GetInt64();
    static int I(JsonElement p, string name, int def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) ? v.GetInt32() : def;

    public object? Query(string method, JsonElement p) => method switch
    {
        "init" => new
        {
            settings = app.Settings,
            version = Application.ProductVersion.Split('+')[0],
            isAdmin = Native.IsUserAnAdmin(),
            boot = app.Collector.BootTime,
            dataDir = AppSettings.DataDir,
            dbSize = app.Store.FileSize(),
            exePath = Environment.ProcessPath,
            networkStatus = app.Collector.NetworkStatus,
            autostartRunKey = Autostart.RunKeyEnabled(),
            live = app.Collector.Latest,
            ranges = app.Queries.Boots(),
            sensorSources = app.Collector.SensorSources,
            cpuName = HardwareInfo.Cpu().Name,
            gpuName = string.Join(", ", HardwareInfo.Gpus().Select(g => g.Name)),
        },
        "live" => app.Collector.Latest,
        "ranges" => app.Queries.Boots(),
        "summary" => app.Queries.Summary(L(p, "from"), L(p, "to")),
        "system" => app.Queries.SystemSeries(L(p, "from"), L(p, "to"), I(p, "points", 900)),
        "processes" => app.Queries.TopProcesses(L(p, "from"), L(p, "to"), I(p, "limit", 200)),
        "process" => app.Queries.ProcessSeries(L(p, "nid"), L(p, "from"), L(p, "to"), I(p, "points", 600)),
        "insights" => app.Queries.Insights(L(p, "from"), L(p, "to"), app.Settings.AlertMemoryPercent),
        "dbInfo" => new { dbSize = app.Store.FileSize() },
        "moments" => app.Queries.Moments(S(p, "metric", "cpu"), L(p, "from"), L(p, "to"), S(p, "order", "top"),
            I(p, "offset", 0), I(p, "limit", 100), p.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : null),
        "moment" => app.Queries.Moment(L(p, "t"), I(p, "span", 1), S(p, "metric", "cpu")),
        "appInstances" => app.Queries.AppInstances(L(p, "nid"), L(p, "from"), L(p, "to")),
        "appStack" => app.Queries.AppStack(L(p, "nid"), L(p, "from"), L(p, "to"), I(p, "points", 300), S(p, "metric", "mem")),
        "instance" => app.Queries.InstanceSeries(L(p, "iid"), L(p, "from"), L(p, "to"), I(p, "points", 500)),
        "events" => app.Queries.Events(L(p, "from"), L(p, "to")),
        // ---- Task Manager features ----
        "perf" => Perf().Snapshot(),
        "kill" => ProcessControl.Kill(I(p, "pid", 0), p.TryGetProperty("tree", out var tr) && tr.GetBoolean()),
        "killApp" => ProcessControl.KillApp(S(p, "name", "")),
        "priority" => ProcessControl.SetPriority(I(p, "pid", 0), S(p, "level", "normal")),
        "efficiency" => ProcessControl.SetEfficiency(I(p, "pid", 0), p.TryGetProperty("on", out var on) && on.GetBoolean()),
        "startupList" => StartupApps.List(),
        "startupSet" => StartupApps.SetEnabled(S(p, "id", ""), p.TryGetProperty("enabled", out var en) && en.GetBoolean()),
        "services" => ServicesInfo.List(),
        "serviceControl" => ServicesInfo.Control(S(p, "name", ""), S(p, "action", "")),
        "searchOnline" => Do(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.bing.com/search?q=" + Uri.EscapeDataString(S(p, "q", ""))) { UseShellExecute = true })),
#if DEBUG
        // test hooks: write exports to a given path without a dialog
        "exportJsonTo" => ExportJson(S(p, "path", ""), L(p, "from"), L(p, "to"), S(p, "level", "summary") == "full", p.TryGetProperty("titles", out var tt) && tt.GetBoolean()),
        "exportCsvTo" => Do(() => app.Queries.ExportCsv(L(p, "from"), L(p, "to"), S(p, "path", ""), S(p, "kind", "system") == "processes")),
#endif
        "exportEstimate" => new Exporter(app.Store, app.Queries, app.Settings).Estimate(L(p, "from"), L(p, "to"), p.TryGetProperty("titles", out var et) && et.GetBoolean()),
        "clearData" => Clear(),
        "saveSettings" => SaveSettings(p),
        _ => Features.Query(method, p), // add-on modules (network, rules, counters)
    };

    object Clear() { app.Store.ClearAll(); return true; }

    // One shared 1-second sampler for the Performance page; dropped a minute after the page stops asking.
    static PerfMonitor? _perf;
    static readonly object PerfGate = new();
    static readonly System.Threading.Timer PerfReaper = new(_ =>
    {
        lock (PerfGate)
        {
            if (_perf is not null && (DateTime.UtcNow - _perf.LastUsed).TotalSeconds > 60) { _perf.Dispose(); _perf = null; }
        }
    }, null, 30_000, 30_000);

    static PerfMonitor Perf()
    {
        lock (PerfGate) return _perf ??= new PerfMonitor();
    }
    static object Do(Action a) { a(); return true; }
    static string S(JsonElement p, string name, string def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;

    public object ExportJson(string path, long from, long to, bool full, bool titles)
    {
        new Exporter(app.Store, app.Queries, app.Settings).Write(path, from, to, full, titles);
        return new { path, size = new FileInfo(path).Length };
    }

    public object SaveSettings(JsonElement p)
    {
        var next = AppSettings.FromJson(p.GetProperty("settings"));
        string? autostartError = app.ApplySettings(next);
        return new { settings = app.Settings, autostartError, autostartRunKey = Autostart.RunKeyEnabled() };
    }
}
