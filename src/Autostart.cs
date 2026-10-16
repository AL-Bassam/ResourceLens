using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace ResourceLens;

/// <summary>
/// Normal autostart uses the HKCU Run key. "Elevated" autostart uses a logon Scheduled Task with highest
/// privileges (no UAC prompt at each logon), which enables per-process network tracking.
/// </summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ResourceLens";
    const string TaskName = "ResourceLens";

    public static string ExePath => Environment.ProcessPath!;
    static string Command => $"\"{ExePath}\" --background";

    public static bool RunKeyEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    static void SetRunKey(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, Command);
        else if (k.GetValue(ValueName) is not null) k.DeleteValue(ValueName, false);
    }

    public static bool TaskExists()
    {
        var r = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return r == 0;
    }

    /// <summary>Applies the autostart settings. Returns null on success or an error code for the UI.</summary>
    public static string? Apply(AppSettings s)
    {
        try
        {
            if (!s.Autostart)
            {
                SetRunKey(false);
                if (TaskExists()) return RunElevatedHelper("--unregister-task");
                return null;
            }
            if (!s.AutostartElevated)
            {
                SetRunKey(true);
                if (TaskExists()) return RunElevatedHelper("--unregister-task");
                return null;
            }
            SetRunKey(false);
            if (Native.IsUserAnAdmin()) return RegisterTask() ? null : "task-failed";
            return RunElevatedHelper("--register-task");
        }
        catch (Exception ex)
        {
            Log.Write("autostart", ex);
            return "error";
        }
    }

    /// <summary>
    /// At startup: keep autostart working without prompting. A missing elevated logon task is re-created when this
    /// instance is already elevated; otherwise the normal Run entry stands in, so recording still begins at boot.
    /// </summary>
    public static void Repair(AppSettings s)
    {
        try
        {
            if (!s.Autostart || !s.AutostartElevated) { Apply(s); return; }
            if (TaskExists()) { SetRunKey(false); return; }
            if (Native.IsUserAnAdmin() && RegisterTask()) { SetRunKey(false); Log.Write("autostart: elevated logon task was missing; re-created"); return; }
            SetRunKey(true);
            Log.Write("autostart: elevated logon task is missing; starting normally at logon until it is enabled again in Settings");
        }
        catch (Exception ex) { Log.Write("autostart repair", ex); }
    }

    static string? RunElevatedHelper(string arg)
    {
        if (Native.IsUserAnAdmin())
        {
            bool ok = arg == "--register-task" ? RegisterTask() : UnregisterTask();
            return ok ? null : "task-failed";
        }
        try
        {
            var p = Process.Start(new ProcessStartInfo(ExePath, arg) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(30_000);
            return p is { HasExited: true, ExitCode: 0 } ? null : "task-failed";
        }
        catch (System.ComponentModel.Win32Exception) { return "uac-cancelled"; }
    }

    public static bool RegisterTask()
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>ResourceLens - resource history monitor</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(ExePath)}</Command><Arguments>--background</Arguments></Exec>
              </Actions>
            </Task>
            """;
        string tmp = Path.Combine(Path.GetTempPath(), $"resourcelens-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static bool UnregisterTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return p.ExitCode;
    }
}
