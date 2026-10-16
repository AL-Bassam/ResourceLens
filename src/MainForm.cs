using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ResourceLens;

/// <summary>Dashboard window: hosts the HTML UI in WebView2 and answers its JSON requests.</summary>
internal sealed class MainForm : Form
{
    const string Host = "resourcelens.app";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly TrayApp _app;
    readonly Api _api;
    readonly WebView2 _web;
    bool _ready, _checking;

    public MainForm(TrayApp app)
    {
        _app = app;
        _api = new Api(app);
        Text = "ResourceLens";
        Icon = TrayApp.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Size = new Size(Math.Min(1440, wa.Width - 60), Math.Min(920, wa.Height - 60));
        MinimumSize = new Size(900, 600);
        if (AppCheck.WindowCss is { } css)
        {
            // fixed size for README pictures (CSS pixels at this screen's scale)
            StartPosition = FormStartPosition.Manual;
            Location = Point.Empty;
            Size = new Size(css.Width * DeviceDpi / 96, css.Height * DeviceDpi / 96);
        }
        bool dark = app.Settings.Theme != "light";
        BackColor = dark ? Color.FromArgb(11, 13, 18) : Color.FromArgb(244, 246, 250);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        HandleCreated += (_, _) => Native.SetDarkTitleBar(Handle, dark);
        // Minimized: stop rendering entirely (no GPU/CPU spent on the dashboard while gaming).
        Resize += (_, _) =>
        {
            if (_web.CoreWebView2 is null) return;
            bool visible = WindowState != FormWindowState.Minimized;
            if (_web.Visible != visible) _web.Visible = visible;
        };
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            // No browser add-ons (ad-block lists, speech, spell-check data…) or shader cache: the page is local and
            // these would otherwise take ~25 MB of the user's disk.
            string args = "--disable-features=msSmartScreenProtection --disable-component-update --disable-gpu-shader-disk-cache";
            if (!_app.Settings.SmoothScrolling) args += " --disable-smooth-scrolling";
            string folder = Path.Combine(AppSettings.DataDir, "WebView2");
            PruneBrowserData(folder);
            var env = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions(args));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Write("webview2", ex);
            MessageBox.Show(this,
                "Microsoft Edge WebView2 Runtime is required.\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message,
                "ResourceLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResource;
        core.WebMessageReceived += OnMessage;
        if (AppCheck.ReportPath is not null) await core.AddScriptToExecuteOnDocumentCreatedAsync(AppCheck.ErrorHook);
        core.NavigationCompleted += (_, _) =>
        {
            _ready = true;
            if (AppCheck.ReportPath is not null && !_checking) { _checking = true; _ = AppCheck.Run(_app, _api, core, _app.ExitApp); }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.Navigate($"https://{Host}/index.html");
    }

    /// <summary>Removes browser components downloaded by earlier versions (the window is closed, so nothing holds them).</summary>
    static void PruneBrowserData(string folder)
    {
        string root = Path.Combine(folder, "EBWebView");
        foreach (var name in new[] { "Subresource Filter", "component_crx_cache", "Speech Recognition", "hyphen-data",
                     "GrShaderCache", "ShaderCache", "Trust Protection Lists", "ZxcvbnData", "OnDeviceHeadSuggestModel" })
        {
            try { if (Directory.Exists(Path.Combine(root, name))) Directory.Delete(Path.Combine(root, name), true); } catch { }
        }
    }

    // ---------------- static files + icons ----------------

    void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        string path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        Stream? stream = null;
        string mime = "application/octet-stream";

        if (path.StartsWith("icon/") && long.TryParse(path[5..], out long nid))
        {
            var bytes = _app.Store.GetIcon(nid);
            if (bytes is not null) { stream = new MemoryStream(bytes); mime = "image/png"; }
        }
        else if (path == "fileicon" && uri.Query.StartsWith("?p="))
        {
            var bytes = IconUtil.Png(Uri.UnescapeDataString(uri.Query[3..]));
            if (bytes is not null) { stream = new MemoryStream(bytes); mime = "image/png"; }
        }
        else
        {
            stream = UiResources.Open(path);
            mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".woff2" => "font/woff2",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                _ => mime,
            };
        }

        var env = _web.CoreWebView2.Environment;
        e.Response = stream is null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(stream, 200, "OK",
                $"Content-Type: {mime}\nCache-Control: {(path.StartsWith("icon/") || path == "fileicon" ? "max-age=86400" : "no-cache")}");
    }

    // ---------------- bridge ----------------

    void Post(object payload)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));
    }

    public void PushLive(LiveSnapshot s)
    {
        if (_ready) Post(new { @event = "live", data = s });
    }

    /// <summary>Settings changed outside the page (tray menu / hotkey).</summary>
    public void PushSettings(AppSettings s)
    {
        if (_ready) Post(new { @event = "settings", data = s });
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch { return; }
        int id = msg.GetProperty("id").GetInt32();
        string method = msg.GetProperty("method").GetString() ?? "";
        JsonElement p = msg.TryGetProperty("params", out var pp) ? pp : default;

        try
        {
            object? result = method switch
            {
                // UI-thread methods (dialogs, shell)
                "exportCsv" => ExportCsv(p),
                "exportJson" => await ExportJsonAsync(p),
                "openDataFolder" => Shell(AppSettings.DataDir),
                "openFileLocation" => ShowInExplorer(p.GetProperty("path").GetString()),
                "restartAsAdmin" => Do(Program.RestartElevated),
                "exitApp" => Do(_app.ExitApp),
                "saveSettings" => SaveSettings(p),
                // Data queries run off the UI thread
                _ => await Task.Run(() => _api.Query(method, p)),
            };
            Post(new { id, result });
        }
        catch (Exception ex)
        {
            Log.Write($"bridge {method}", ex);
            Store.Note(ex);
            Post(new { id, error = ex.Message });
        }
    }

    static long L(JsonElement p, string name) => p.GetProperty(name).GetInt64();
    static object? Do(Action a) { a(); return true; }

    object SaveSettings(JsonElement p)
    {
        var result = _api.SaveSettings(p);
        Native.SetDarkTitleBar(Handle, _app.Settings.Theme != "light");
        return result;
    }

    object? ExportCsv(JsonElement p)
    {
        bool processes = p.GetProperty("kind").GetString() == "processes";
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"ResourceLens-{(processes ? "processes" : "system")}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        _app.Queries.ExportCsv(L(p, "from"), L(p, "to"), dlg.FileName, processes);
        return dlg.FileName;
    }

    async Task<object?> ExportJsonAsync(JsonElement p)
    {
        bool full = p.GetProperty("level").GetString() == "full";
        bool titles = p.TryGetProperty("titles", out var t) && t.GetBoolean();
        using var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"ResourceLens-{(full ? "full" : "ai-summary")}-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        long from = L(p, "from"), to = L(p, "to");
        string path = dlg.FileName;
        // Large ranges can take a few seconds: build the file off the UI thread.
        return await Task.Run(() => _api.ExportJson(path, from, to, full, titles));
    }

    static object? Shell(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        return true;
    }

    static object? ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ready = false;
        _web.Dispose();
        base.OnFormClosed(e);
    }
}
