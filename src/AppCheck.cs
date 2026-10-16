using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ResourceLens;

/// <summary>
/// `ResourceLens.exe --check=report.txt` (release verification, best run with RESOURCELENS_DATADIR on a test copy):
/// calls every read-only API method and serializes the result exactly as the window bridge does, writes both export
/// formats, opens every page in the real window, records JavaScript errors and page screenshots, then exits.
/// Catches what a trimmed build can break (reflection-based JSON, WebView2 COM interop) before users do.
/// </summary>
internal static class AppCheck
{
    public static string? ReportPath { get; private set; }

    /// <summary>"--shots": also save a picture of every page (README screenshots), with sample counters added.</summary>
    public static bool Shots { get; private set; }

    /// <summary>"--check-size=1440x900": window size in CSS pixels for the pictures.</summary>
    public static Size? WindowCss { get; private set; }

    public static void Parse(string[] args)
    {
        ReportPath = args.FirstOrDefault(a => a.StartsWith("--check="))?["--check=".Length..];
        Shots = args.Contains("--shots");
        if (args.FirstOrDefault(a => a.StartsWith("--check-size="))?["--check-size=".Length..].Split('x') is [var w, var h]
            && int.TryParse(w, out int cw) && int.TryParse(h, out int ch)) WindowCss = new Size(cw, ch);
    }

    public const string ErrorHook =
        "window.__errs=[];addEventListener('error',e=>__errs.push(String(e.message)));" +
        "addEventListener('unhandledrejection',e=>__errs.push(String(e.reason&&e.reason.message||e.reason)));";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly string[] Pages = { "overview", "perf", "moments", "live", "processes", "insights", "startup", "services", "network", "rules", "counters", "settings" };

    public static async Task Run(TrayApp app, Api api, CoreWebView2 core, Action exit)
    {
        var sb = new StringBuilder();
        string dir = Path.GetDirectoryName(Path.GetFullPath(ReportPath!))!;
        try
        {
            await Task.Delay(12_000); // a few samples + first page load
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), from = now - 30L * 86_400_000;

            string Call(string method, string p)
            {
                try
                {
                    var r = api.Query(method, JsonDocument.Parse(p).RootElement);
                    string s = JsonSerializer.Serialize(r, Json);
                    sb.AppendLine($"api {method}: ok ({s.Length} chars)");
                    return s;
                }
                catch (Exception ex) { sb.AppendLine($"api {method}: FAIL {ex.GetType().Name}: {ex.Message}"); return "null"; }
            }
            await Task.Run(() =>
            {
                string range = $"\"from\":{from},\"to\":{now}";
                foreach (var m in new[] { "init", "live", "ranges", "dbInfo", "perf", "startupList", "services" }) Call(m, "{}");
                foreach (var m in new[] { "summary", "system", "insights", "events" }) Call(m, "{" + range + "}");
                string procs = Call("processes", "{" + range + "}");
                long nid = FindId(procs, "nid");
                Call("process", $"{{\"nid\":{nid},{range}}}");
                Call("appInstances", $"{{\"nid\":{nid},{range}}}");
                Call("appStack", $"{{\"nid\":{nid},{range}}}");
                Call("moments", "{\"metric\":\"cpu\"," + range + "}");
                string moment = Call("moment", $"{{\"t\":{now - 60_000},\"span\":1,\"metric\":\"mem\"}}");
                Call("instance", $"{{\"iid\":{FindId(moment, "iid")},{range}}}");
                // add-on modules (read-only calls)
                foreach (var m in new[] { "network.summary", "network.connections", "network.activity", "network.config", "rules.list",
                    "rules.apps", "rules.probalance.get", "rules.events", "rules.powerPlans", "rules.cores", "counters.presets", "counters.live" })
                    Call(m, "{}");
                Call("network.usage", "{\"days\":7}");
                Call("counters.history", "{\"range\":\"24h\"}");
                foreach (bool full in new[] { false, true })
                {
                    string f = Path.Combine(dir, full ? "check-full.json" : "check-summary.json");
                    try { api.ExportJson(f, from, now, full, false); sb.AppendLine($"export json {(full ? "full" : "summary")}: ok ({new FileInfo(f).Length} bytes)"); }
                    catch (Exception ex) { sb.AppendLine($"export json: FAIL {ex.Message}"); }
                }
                try { app.Queries.ExportCsv(from, now, Path.Combine(dir, "check.csv"), true); sb.AppendLine("export csv: ok"); }
                catch (Exception ex) { sb.AppendLine($"export csv: FAIL {ex.Message}"); }
                // settings: save through the bridge, read back from disk, compare
                string before = JsonSerializer.Serialize(app.Settings, Json);
                Call("saveSettings", "{\"settings\":" + before + "}");
                string after = JsonSerializer.Serialize(AppSettings.Load(), Json);
                sb.AppendLine(before == after ? "settings round-trip: ok" : $"settings round-trip: FAIL\n  {before}\n  {after}");
            });

            foreach (var page in Pages)
            {
                await core.ExecuteScriptAsync($"showPage('{page}')");
                await Task.Delay(Shots ? 4000 : 2500);
                if (Shots && page == "counters")
                {
                    // a few quick-add counters so the page shows live values
                    for (int i = 0; i < 4; i++)
                    {
                        await core.ExecuteScriptAsync($"document.querySelectorAll('#cnPresets [data-preset]:not([disabled])')[0]?.click()");
                        await Task.Delay(1500);
                    }
                    await Task.Delay(8000);
                }
                if (Shots || page is "overview" or "perf" or "settings")
                {
                    using (var fs = File.Create(Path.Combine(dir, $"check-{page}.png")))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                    if (Shots && page == "overview")
                    {
                        // second picture of the overview: scrolled down to the history charts
                        await core.ExecuteScriptAsync("(() => { const c = document.querySelector('.content'), k = document.querySelector('#ch-cpu').closest('.card');" +
                            " c.scrollTop += k.getBoundingClientRect().top - c.getBoundingClientRect().top - 190; })()");
                        await Task.Delay(1500);
                        using (var fs = File.Create(Path.Combine(dir, "check-overview-charts.png")))
                            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                        await core.ExecuteScriptAsync("document.querySelector('.content').scrollTop = 0");
                    }
                }
            }
            // gaming overlay: show it, photograph its corner of the screen, hide it again
            if (!app.Settings.OverlayEnabled) app.ToggleOverlay();
            await Task.Delay(3000);
            var area = Screen.PrimaryScreen!.Bounds;
            using (var shot = new Bitmap(420, 320))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Left, area.Top, 0, 0, shot.Size, CopyPixelOperation.SourceCopy);
                shot.Save(Path.Combine(dir, "check-overlay.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            app.ToggleOverlay();
            sb.AppendLine("overlay: shown and hidden");

            string result = await core.ExecuteScriptAsync(
                "JSON.stringify({errors: window.__errs || [], charts: document.querySelectorAll('[_echarts_instance_]').length," +
                " toast: (document.querySelector('#toast:not(.hidden)') || {}).textContent || ''})");
            sb.AppendLine("ui: " + JsonSerializer.Deserialize<string>(result));
        }
        catch (Exception ex) { sb.AppendLine("check: FAIL " + ex); }
        File.WriteAllText(ReportPath!, sb.ToString());
        exit();
    }

    /// <summary>First numeric property with this name anywhere in a JSON document (0 when absent).</summary>
    static long FindId(string json, string name)
    {
        static long Walk(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetInt64();
                    long v = Walk(p.Value, name);
                    if (v != 0) return v;
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) { long v = Walk(x, name); if (v != 0) return v; }
            return 0;
        }
        try { return Walk(JsonDocument.Parse(json).RootElement, name); } catch { return 0; }
    }
}
