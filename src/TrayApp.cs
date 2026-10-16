using System.Diagnostics;
using System.Reflection;

namespace ResourceLens;

/// <summary>Owns the collector, the tray icon and (on demand) the dashboard window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly Store _store;
    readonly Collector _collector;
    readonly Control _ui; // marshals collector events to the UI thread
    readonly RegisteredWaitHandle _showWait, _exitWait;
    AppSettings _settings;
    MainForm? _form;

    public Store Store => _store;
    public Collector Collector => _collector;
    public Queries Queries { get; }
    public AppSettings Settings => _settings;

    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

#if DEBUG
    DevServer? _dev;
#endif

    readonly bool _devMode;
    volatile bool _trimDue;
    DateTime _trimAt;

    public TrayApp(EventWaitHandle showEvent, EventWaitHandle exitEvent, bool openWindow, bool devMode = false)
    {
        _devMode = devMode;
        _ui = new Control();
        _ui.CreateControl();

        _settings = AppSettings.Load();
        Store.Damaged += () => _ui.BeginInvoke(() => Restart("database damaged"));
        Native.BecomeBackgroundFriendly();
        if (_settings.EfficiencyMode && !openWindow) Native.SetEfficiencyMode(true);
        _store = new Store();
        Queries = new Queries(_store);
        _collector = new Collector(_store, () => _settings);
        _collector.Updated += OnUpdated;
        _collector.Alert += (title, body) => _ui.BeginInvoke(() => ShowBalloon(title, body));
        _collector.Notice += (title, body) => _ui.BeginInvoke(() => ShowNotice(title, body));
        _collector.Start();
        _watchdog.Tick += (_, _) => CheckRecorder();
        _watchdog.Start();

        _tray = new NotifyIcon { Icon = AppIcon, Text = "ResourceLens", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        BuildMenu();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => _ui.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        // "ResourceLens.exe --exit" (the installer / uninstaller) asks for a clean shutdown.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent,
            (_, _) => _ui.BeginInvoke(ExitApp), null, Timeout.Infinite, true);

        if (Store.LastRepair is { } repair)
            ShowNotice(Ar ? "تم إصلاح سجل البيانات" : "History database repaired",
                Ar ? $"كان ملف السجل تالفاً، فاستُعيد منه {repair.Rows:N0} سجل وحُفظت النسخة التالفة باسم {Path.GetFileName(repair.Backup)}."
                   : $"The history file was damaged: {repair.Rows:N0} rows were recovered and the damaged copy was kept as {Path.GetFileName(repair.Backup)}.");

        // Register on first run, keep the Run entry pointing at this exe (it may have been moved), and recover a
        // missing elevated logon task without prompting.
        if (_settings.Autostart && ManagesAutostart)
            Task.Run(() => Autostart.Repair(_settings));
#if DEBUG
        if (devMode) { _dev = new DevServer(this); _collector.WantDetail = true; }
#endif

        _hotkey = new HotkeyWindow(() => _ui.BeginInvoke(ToggleOverlay));
        UpdateOverlay();

        if (openWindow) ShowWindow();
    }

    bool Ar => _settings.Language == "ar";

    // Dev-server and test runs (own data folder) leave the installed copy's autostart entry alone.
    bool ManagesAutostart => !_devMode && !AppSettings.IsTestDataDir;

    // ---------------- recorder watchdog ----------------

    // Recording is the point of the app: if the sampling loop gets stuck (a driver or system call that never
    // returns), note where in errors.log and start a fresh instance instead of silently not recording.
    readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 30_000 };
    readonly long _startedAt = Environment.TickCount64;
    bool _stallLogged;

    void CheckRecorder()
    {
        var (stage, ms) = _collector.Progress;
        if (ms < 120_000) { _stallLogged = false; return; }
        if (!_stallLogged) { _stallLogged = true; Log.Write($"watchdog: recording stuck for {ms / 1000}s at '{stage}'"); }
        // An instance the watchdog started waits 30 minutes before it may do the same (no restart loops).
        bool mayRestart = !Environment.GetCommandLineArgs().Contains("--restarted") || Environment.TickCount64 - _startedAt > 1_800_000;
        if (ms >= 300_000 && mayRestart && !_devMode) RestartRecorder(stage);
    }

    void RestartRecorder(string stage)
    {
        Log.Write($"watchdog: restarting (stuck at '{stage}')");
        try
        {
            // Inherits this process's token: an elevated instance stays elevated without a UAC prompt.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--background --wait-previous --restarted") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Write("watchdog restart", ex); return; }
        _tray.Visible = false;
        Environment.Exit(0); // a normal shutdown would wait for the stuck thread
    }

    // ---------------- gaming overlay ----------------

    OverlayWindow? _overlay;
    readonly HotkeyWindow _hotkey;

    void UpdateOverlay()
    {
        _hotkey.SetEnabled(_settings.OverlayHotkey);
        if (_settings.OverlayEnabled)
        {
            if (_overlay is null || _overlay.IsDisposed) { _overlay = new OverlayWindow(_settings); _overlay.Show(); }
            else _overlay.Apply(_settings);
        }
        else if (_overlay is not null) { _overlay.Close(); _overlay.Dispose(); _overlay = null; }
        BuildMenu();
    }

    /// <summary>Ctrl+Shift+O and the tray menu: show / hide the overlay (remembered in settings).</summary>
    public void ToggleOverlay()
    {
        var next = _settings.Clone();
        next.OverlayEnabled = !next.OverlayEnabled;
        ApplySettings(next);
        _form?.PushSettings(_settings);
    }

    void BuildMenu()
    {
        var menu = new ContextMenuStrip { RightToLeft = Ar ? RightToLeft.Yes : RightToLeft.No, ShowImageMargin = true };
        menu.Items.Add(Ar ? "فتح لوحة المراقبة" : "Open dashboard", null, (_, _) => ShowWindow());
        var ov = new ToolStripMenuItem(Ar ? "الشاشة العائمة (Ctrl+Shift+O)" : "Gaming overlay (Ctrl+Shift+O)") { Checked = _settings.OverlayEnabled };
        ov.Click += (_, _) => ToggleOverlay();
        menu.Items.Add(ov);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Ar ? "خروج (إيقاف التسجيل)" : "Exit (stop recording)", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = menu;
    }

    void OnUpdated(LiveSnapshot s)
    {
        string tip = $"ResourceLens\nCPU {s.Cpu ?? 0:0}%   RAM {s.MemUsed * 100.0 / Math.Max(1, s.MemTotal):0}%   GPU {s.Gpu ?? 0:0}%";
        if (s.Sensors is { } sn && (sn.CpuTemp is not null || sn.GpuTemp is not null))
            tip += $"\nCPU {sn.CpuTemp:0}°C   GPU {sn.GpuTemp:0}°C";
        _ui.BeginInvoke(() =>
        {
            _tray.Text = tip.Length > 127 ? tip[..127] : tip;
            _form?.PushLive(s);
        });
        if (_trimDue && DateTime.UtcNow >= _trimAt) { _trimDue = false; Native.TrimWorkingSet(); }
    }

    void ShowBalloon(string title, string body)
    {
        _tray.BalloonTipTitle = (Ar ? "استهلاك مرتفع: " : "High usage: ") + title;
        _tray.BalloonTipText = body;
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(8000);
    }

    void ShowNotice(string title, string body)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = body;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(10000);
    }

    /// <summary>Starts a fresh instance that takes over once this one has closed the database, then exits.</summary>
    void Restart(string why)
    {
        if (_devMode) return;
        Log.Write($"restarting ({why})");
        string args = (_form is null ? "--background " : "") + "--wait-previous";
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write("restart", ex); return; }
        ExitApp();
    }

    public void ShowWindow()
    {
        if (_form is null || _form.IsDisposed)
        {
            _collector.WantDetail = true;
            Native.SetEfficiencyMode(false);
            _form = new MainForm(this);
            // Closing the window frees the browser engine's memory; recording continues in the tray.
            _form.FormClosed += (_, _) =>
            {
                _form = null;
                _collector.WantDetail = false;
                if (_settings.EfficiencyMode) Native.SetEfficiencyMode(true);
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                // Give WebView2 a moment to unload, then return the freed pages to Windows.
                _trimAt = DateTime.UtcNow.AddSeconds(10);
                _trimDue = true;
            };
            _form.Show();
        }
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    public string? ApplySettings(AppSettings next)
    {
        var prev = _settings;
        next.Save();
        _settings = next;
        string? autostartError = null;
        if (ManagesAutostart && (prev.Autostart != next.Autostart || prev.AutostartElevated != next.AutostartElevated
            || (next.Autostart && !next.AutostartElevated && !Autostart.RunKeyEnabled())))
            autostartError = Autostart.Apply(next);
        // windows / menus must be touched on the UI thread (the dev server calls in from a worker thread)
        if (_ui.InvokeRequired) _ui.Invoke(UpdateOverlay); else UpdateOverlay();
        if (_form is null) Native.SetEfficiencyMode(next.EfficiencyMode);
        _collector.Poke();
        return autostartError;
    }

    public void ExitApp()
    {
        _form?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
            _watchdog.Dispose();
            _overlay?.Dispose();
            _hotkey.Dispose();
#if DEBUG
            _dev?.Dispose();
#endif
            _collector.Dispose();
            _store.Dispose();
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
