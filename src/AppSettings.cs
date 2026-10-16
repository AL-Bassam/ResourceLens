using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceLens;

public sealed class AppSettings
{
    /// <summary>Seconds between snapshots (1 - 600).</summary>
    public int IntervalSeconds { get; set; } = 5;

    public bool TrackCpu { get; set; } = true;
    public bool TrackMemory { get; set; } = true;
    public bool TrackDisk { get; set; } = true;
    public bool TrackNetwork { get; set; } = true;
    public bool TrackGpu { get; set; } = true;

    /// <summary>Temperatures, clock speeds, power, fans and GPU memory.</summary>
    public bool TrackSensors { get; set; } = true;

    /// <summary>Record per-process history (top consumers each snapshot).</summary>
    public bool TrackProcesses { get; set; } = true;

    /// <summary>Per-process network via ETW. Needs administrator rights.</summary>
    public bool PerProcessNetwork { get; set; } = true;

    /// <summary>How many top processes (per metric) are stored each snapshot.</summary>
    public int TopProcesses { get; set; } = 20;

    /// <summary>How many individual processes (PIDs) are stored per metric each snapshot.</summary>
    public int TopInstances { get; set; } = 30;

    /// <summary>Record the title of the window in the foreground with every snapshot.</summary>
    public bool RecordWindowTitles { get; set; } = true;

    /// <summary>Days during which every snapshot is kept as-is before being compacted to 1-minute averages.</summary>
    public int FullResolutionDays { get; set; } = 7;

    /// <summary>Run in Windows "efficiency mode" (EcoQoS) while the dashboard is closed.</summary>
    public bool EfficiencyMode { get; set; } = true;

    public int RetentionDays { get; set; } = 30;

    public bool Autostart { get; set; } = true;
    public bool AutostartElevated { get; set; } = false;

    public bool AlertsEnabled { get; set; } = true;
    public int AlertMemoryPercent { get; set; } = 90;
    public int AlertCpuPercent { get; set; } = 95;
    public int AlertSustainSeconds { get; set; } = 60;
    public int AlertTempC { get; set; } = 90;

    /// <summary>
    /// Dashboard items the user chose to hide ("page:perf", "insights:leaks", "moments:temp", …).
    /// Display only: recording is never affected.
    /// </summary>
    public List<string> Hidden { get; set; } = new();

    // ---- Gaming overlay (always-on-top, click-through mini panel) ----
    public bool OverlayEnabled { get; set; } = false;
    /// <summary>tl, tr, bl, br</summary>
    public string OverlayCorner { get; set; } = "tl";
    public List<string> OverlayItems { get; set; } = new() { "cpu", "cpuTemp", "cpuClock", "gpu", "gpuTemp", "ram", "vram", "time" };
    /// <summary>Background opacity in percent.</summary>
    public int OverlayOpacity { get; set; } = 65;
    /// <summary>Text size in percent.</summary>
    public int OverlayScale { get; set; } = 100;
    /// <summary>Ctrl+Shift+O shows / hides the overlay.</summary>
    public bool OverlayHotkey { get; set; } = true;

    /// <summary>Animated (smooth) wheel scrolling in the dashboard. Off = the page follows the wheel instantly.</summary>
    public bool SmoothScrolling { get; set; } = false;

    public string Language { get; set; } = "ar";
    public string Theme { get; set; } = "dark";

    /// <summary>History/settings folder. RESOURCELENS_DATADIR overrides it (used for side-by-side test runs).</summary>
    [JsonIgnore] public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("RESOURCELENS_DATADIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceLens");

    /// <summary>A test run on its own data folder: it must never touch the real install's autostart entry.</summary>
    [JsonIgnore] public static bool IsTestDataDir => Environment.GetEnvironmentVariable("RESOURCELENS_DATADIR") is { Length: > 0 };

    [JsonIgnore] static string FilePath => Path.Combine(DataDir, "settings.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Normalize()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 600);
        TopProcesses = Math.Clamp(TopProcesses, 5, 100);
        RetentionDays = Math.Clamp(RetentionDays, 1, 365);
        TopInstances = Math.Clamp(TopInstances, 5, 200);
        FullResolutionDays = Math.Clamp(FullResolutionDays, 1, 365);
        AlertMemoryPercent = Math.Clamp(AlertMemoryPercent, 50, 99);
        AlertCpuPercent = Math.Clamp(AlertCpuPercent, 50, 100);
        AlertSustainSeconds = Math.Clamp(AlertSustainSeconds, 0, 3600);
        AlertTempC = Math.Clamp(AlertTempC, 60, 110);
        Hidden = (Hidden ?? new()).Where(h => !string.IsNullOrWhiteSpace(h) && !h.StartsWith("page:overview") && !h.StartsWith("page:settings"))
            .Distinct().Take(200).ToList();
        if (OverlayCorner is not ("tl" or "tr" or "bl" or "br")) OverlayCorner = "tl";
        OverlayItems = (OverlayItems ?? new()).Where(i => OverlayWindow.AllItems.Contains(i)).Distinct().ToList();
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0, 100);
        OverlayScale = Math.Clamp(OverlayScale, 60, 200);
        if (Language is not ("ar" or "en")) Language = "ar";
        if (Theme is not ("dark" or "light" or "system")) Theme = "dark";
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        var d = new AppSettings();
        d.Save();
        return d;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, FilePath, true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JsonOpts);

    public static AppSettings FromJson(JsonElement e)
    {
        var s = e.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
        s.Normalize();
        return s;
    }
}
