using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ResourceLens;

internal static class Program
{
    // A custom data folder (test runs) gets its own single-instance scope.
    static readonly string Scope = Environment.GetEnvironmentVariable("RESOURCELENS_DATADIR") is { Length: > 0 } d
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..8]
        : "";
    static readonly string MutexName = @"Local\ResourceLens.Instance" + Scope;
    static readonly string ShowEventName = @"Local\ResourceLens.Show" + Scope;
    static readonly string ExitEventName = @"Local\ResourceLens.Exit" + Scope;

    [STAThread]
    static int Main(string[] args)
    {
        // Inside another app's container our data folder would be redirected (split database): restart outside it.
        // Test runs on their own data folder are left alone.
        if (!AppSettings.IsTestDataDir && Sandbox.IsRedirected(AppSettings.DataDir) && Sandbox.RelaunchOutside(args)) return 0;

        // Elevated helper modes (launched by Autostart via UAC)
        if (args.Contains("--register-task")) return Autostart.RegisterTask() ? 0 : 1;
        if (args.Contains("--unregister-task")) return Autostart.UnregisterTask() ? 0 : 1;

        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--selftest-admin")) return SelfTest.RunAdmin();
        if (args.Contains("--bench")) return SelfTest.Bench();
        if (args.Contains("--probe-sensors")) return SelfTest.ProbeSensors();
        if (args.Contains("--exit")) return RequestExit() ? 0 : 1;
        if (args.Contains("--uninstall")) return Uninstaller.Run(args.Contains("--quiet"));

        bool background = args.Contains("--background");
        AppCheck.Parse(args);

        Mutex? mutex = null;
        bool owner;
        try { mutex = new Mutex(true, MutexName, out owner); }
        catch (UnauthorizedAccessException) { owner = false; } // held by an elevated instance

        // A restarted instance waits for the old one to close the database and exit.
        if (!owner && args.Contains("--wait-previous") && mutex is not null)
        {
            try { owner = mutex.WaitOne(30_000); } catch (AbandonedMutexException) { owner = true; }
        }

        if (!owner)
        {
            if (!background) SignalShow();
            return 0;
        }

        // Events that later launches use to bring the window up / ask for a clean exit; open to non-elevated launches too.
        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        using var showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, sec);
        using var exitEvent = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ExitEventName, out _, sec);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Write("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

        bool dev = args.Contains("--devserver");
        using (var app = new TrayApp(showEvent, exitEvent, openWindow: !background && !dev, devMode: dev))
            Application.Run(app);
        // Only now is the database closed: a waiting successor may take over.
        try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        return 0;
    }

    /// <summary>"--exit": asks the running instance to shut down cleanly and waits until it is gone (installer).</summary>
    internal static bool RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) return true; // not running
        // The copies running now (by name: an elevated instance's mutex cannot be opened from here).
        var running = Process.GetProcessesByName("ResourceLens").Where(p => p.Id != Environment.ProcessId).ToList();
        using (ev) ev.Set();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (running.All(p => { try { return p.HasExited; } catch { return true; } })) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    static void SignalShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { }
    }

    public static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--wait-previous") { UseShellExecute = true, Verb = "runas" });
            Application.Exit();
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC cancelled */ }
    }
}
