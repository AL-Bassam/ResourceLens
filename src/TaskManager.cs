using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ResourceLens;

/// <summary>Task-Manager-style actions: end task / tree, priority, efficiency mode.</summary>
internal static unsafe class ProcessControl
{
    /// <summary>Killing these crashes or locks Windows; Task Manager also refuses or warns.</summary>
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "Secure System", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "LsaIso", "fontdrvhost", "dwm",
    };

    static object Fail(string code, string? detail = null) => new { ok = false, error = code, detail };

    static object? Guard(Process p)
    {
        if (p.Id == Environment.ProcessId) return Fail("self");
        if (p.Id <= 4 || Critical.Contains(p.ProcessName)) return Fail("critical", p.ProcessName);
        return null;
    }

    static string Code(Exception ex) => ex switch
    {
        Win32Exception { NativeErrorCode: 5 } => "access-denied",
        ArgumentException => "not-running",
        InvalidOperationException => "not-running",
        _ => "failed",
    };

    public static object Kill(int pid, bool tree)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (Guard(p) is { } bad) return bad;
            p.Kill(tree);
            return new { ok = true, killed = 1 };
        }
        catch (Exception ex) { return Fail(Code(ex), ex.Message); }
    }

    /// <summary>"End task" on an application: every process with that executable name.</summary>
    public static object KillApp(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe);
        if (Critical.Contains(name) || name.Equals("svchost", StringComparison.OrdinalIgnoreCase)) return Fail("critical", name);
        int killed = 0, failed = 0; string? err = null;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (Guard(p) is not null) { failed++; continue; }
                try { p.Kill(true); killed++; }
                catch (Exception ex) { failed++; err ??= Code(ex); }
            }
        }
        return killed > 0 ? new { ok = true, killed, failed } : Fail(err ?? "not-running");
    }

    public static object SetPriority(int pid, string level)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.Id <= 4) return Fail("critical");
            p.PriorityClass = level switch
            {
                "realtime" => ProcessPriorityClass.RealTime,
                "high" => ProcessPriorityClass.High,
                "above" => ProcessPriorityClass.AboveNormal,
                "below" => ProcessPriorityClass.BelowNormal,
                "idle" => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal,
            };
            return new { ok = true };
        }
        catch (Exception ex) { return Fail(Code(ex), ex.Message); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int cls, void* info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(IntPtr h, uint cls);

    /// <summary>Windows 11 Efficiency mode for another process: EcoQoS + idle priority (exactly what Task Manager does).</summary>
    public static object SetEfficiency(int pid, bool on)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return Fail("critical");
        IntPtr h = OpenProcess(0x0200 /* PROCESS_SET_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return Fail(Marshal.GetLastWin32Error() == 5 ? "access-denied" : "not-running");
        try
        {
            uint* st = stackalloc uint[3];
            st[0] = 1; st[1] = on ? 1u : 0u; st[2] = on ? 1u : 0u;
            bool a = SetProcessInformation(h, 4, st, 12);
            bool b = SetPriorityClass(h, on ? 0x40u /* IDLE */ : 0x20u /* NORMAL */);
            return a || b ? new { ok = true } : Fail("failed");
        }
        finally { Native.CloseHandle(h); }
    }
}

/// <summary>Startup apps, like Task Manager's "Startup apps" page, with enable/disable via StartupApproved.</summary>
internal static class StartupApps
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    sealed record Source(string Id, RegistryKey Hive, string? RunKey, string? Folder, string ApprovedName);

    static IEnumerable<Source> Sources() => new[]
    {
        new Source("hkcu", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", null, "Run"),
        new Source("hklm", Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", null, "Run"),
        new Source("hklm32", Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", null, "Run32"),
        new Source("folder", Registry.CurrentUser, null, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "StartupFolder"),
        new Source("folderall", Registry.LocalMachine, null, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "StartupFolder"),
    };

    public static List<object> List()
    {
        var list = new List<object>();
        foreach (var s in Sources())
        {
            var entries = new List<(string Name, string Command, string? Path)>();
            try
            {
                if (s.RunKey is not null)
                {
                    using var k = s.Hive.OpenSubKey(s.RunKey);
                    foreach (var n in k?.GetValueNames() ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrEmpty(n)) continue;
                        string cmd = k!.GetValue(n)?.ToString() ?? "";
                        entries.Add((n, cmd, ExeFromCommand(cmd)));
                    }
                }
                else if (s.Folder is not null && Directory.Exists(s.Folder))
                {
                    foreach (var f in Directory.GetFiles(s.Folder))
                    {
                        if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(f) ?? f : f;
                        entries.Add((Path.GetFileName(f), f, target));
                    }
                }
            }
            catch { }

            foreach (var (name, cmd, path) in entries)
            {
                bool enabled = true;
                try
                {
                    using var ak = s.Hive.OpenSubKey(Approved + s.ApprovedName);
                    if (ak?.GetValue(name) is byte[] b && b.Length > 0) enabled = (b[0] & 1) == 0;
                }
                catch { }
                string? publisher = null, descr = null;
                if (path is not null && File.Exists(path))
                {
                    try
                    {
                        var v = FileVersionInfo.GetVersionInfo(path);
                        publisher = string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
                        descr = string.IsNullOrWhiteSpace(v.FileDescription) ? null : v.FileDescription.Trim();
                    }
                    catch { }
                }
                list.Add(new
                {
                    id = $"{s.Id}|{name}", name = Path.GetFileNameWithoutExtension(name), descr, command = ProcessDetails.Redact(cmd), path,
                    publisher, source = s.Id, enabled, needsAdmin = s.Hive == Registry.LocalMachine,
                });
            }
        }
        return list;
    }

    public static object SetEnabled(string id, bool enabled)
    {
        int bar = id.IndexOf('|');
        if (bar < 0) return new { ok = false, error = "failed" };
        var s = Sources().FirstOrDefault(x => x.Id == id[..bar]);
        if (s is null) return new { ok = false, error = "failed" };
        string name = id[(bar + 1)..];
        try
        {
            using var k = s.Hive.CreateSubKey(Approved + s.ApprovedName, true);
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            k.SetValue(name, data, RegistryValueKind.Binary);
            return new { ok = true };
        }
        catch (UnauthorizedAccessException) { return new { ok = false, error = "access-denied" }; }
        catch (System.Security.SecurityException) { return new { ok = false, error = "access-denied" }; }
        catch (Exception ex) { return new { ok = false, error = "failed", detail = ex.Message }; }
    }

    static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int e = cmd.IndexOf('"', 1);
            return e > 1 ? cmd[1..e] : null;
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
    }

    static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            object shell = Activator.CreateInstance(t)!;
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
                try { return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string; }
                finally { Marshal.FinalReleaseComObject(sc); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }
}

/// <summary>Windows services, like Task Manager's "Services" page, with start / stop / restart.</summary>
internal static unsafe class ServicesInfo
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumServicesStatusExW(IntPtr scm, int level, uint type, uint state, IntPtr buf, int size, out int needed, out int count, ref int resume, string? group);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(IntPtr svc, int control, byte* status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, byte* status);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string src, StringBuilder outBuf, int cch, IntPtr reserved);

    static readonly Dictionary<string, string?> DescrCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<object> List()
    {
        var list = new List<object>();
        IntPtr scm = OpenSCManagerW(null, null, 0x0004 /* ENUMERATE */);
        if (scm == IntPtr.Zero) return list;
        try
        {
            int resume = 0;
            EnumServicesStatusExW(scm, 0, 0x30, 3, IntPtr.Zero, 0, out int needed, out _, ref resume, null);
            IntPtr buf = Marshal.AllocHGlobal(needed + 8192);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, 0, 0x30, 3, buf, needed + 8192, out _, out int count, ref resume, null)) return list;
                int stride = IntPtr.Size * 2 + 40;
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buf + i * stride;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e)) ?? "";
                    string disp = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, IntPtr.Size)) ?? name;
                    int state = Marshal.ReadInt32(e, IntPtr.Size * 2 + 4);
                    int pid = Marshal.ReadInt32(e, IntPtr.Size * 2 + 28);
                    int start = 3; bool delayed = false; string? descr = null, account = null;
                    try
                    {
                        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                        if (k?.GetValue("Start") is int st) start = st;
                        delayed = k?.GetValue("DelayedAutostart") is int d && d == 1;
                        account = k?.GetValue("ObjectName") as string;
                        descr = Describe(name, k?.GetValue("Description") as string);
                    }
                    catch { }
                    list.Add(new
                    {
                        name, display = disp, descr, pid = pid == 0 ? (int?)null : pid, account,
                        status = state switch { 1 => "stopped", 2 => "starting", 3 => "stopping", 4 => "running", 5 => "continuing", 6 => "pausing", 7 => "paused", _ => "unknown" },
                        startType = start switch { 0 => "boot", 1 => "system", 2 => delayed ? "auto-delayed" : "auto", 4 => "disabled", _ => "manual" },
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
        return list;
    }

    static string? Describe(string name, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        lock (DescrCache)
        {
            if (DescrCache.TryGetValue(name, out var c)) return c;
            string? res = raw;
            if (raw.StartsWith('@'))
            {
                var sb = new StringBuilder(1024);
                res = SHLoadIndirectString(raw, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
            }
            return DescrCache[name] = res;
        }
    }

    static int State(IntPtr svc)
    {
        byte* st = stackalloc byte[28];
        return QueryServiceStatus(svc, st) ? *(int*)(st + 4) : 0;
    }

    public static object Control(string name, string action)
    {
        IntPtr scm = OpenSCManagerW(null, null, 0x0001 /* CONNECT */);
        if (scm == IntPtr.Zero) return new { ok = false, error = "access-denied" };
        try
        {
            IntPtr svc = OpenServiceW(scm, name, 0x0010 | 0x0020 | 0x0004 /* START | STOP | QUERY_STATUS */);
            if (svc == IntPtr.Zero) return new { ok = false, error = Marshal.GetLastWin32Error() == 5 ? "access-denied" : "failed" };
            try
            {
                if (action is "stop" or "restart")
                {
                    byte* st = stackalloc byte[28];
                    if (State(svc) != 1 && !ControlService(svc, 1, st))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 1062) return new { ok = false, error = err == 5 ? "access-denied" : "failed", detail = new Win32Exception(err).Message };
                    }
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (State(svc) != 1 && DateTime.UtcNow < until) Thread.Sleep(250);
                }
                if (action is "start" or "restart")
                {
                    if (!StartServiceW(svc, 0, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 1056) return new { ok = false, error = err == 5 ? "access-denied" : "failed", detail = new Win32Exception(err).Message };
                    }
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (State(svc) == 2 && DateTime.UtcNow < until) Thread.Sleep(250);
                }
                return new { ok = true };
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
