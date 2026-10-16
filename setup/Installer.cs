using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ResourceLensSetup;

/// <summary>Per-user install of ResourceLens: no administrator rights, nothing outside the user's profile.</summary>
internal static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceLens";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ExeName = "ResourceLens.exe";

    public static string Version => typeof(Installer).Assembly.GetName().Version.ToString(3);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string DefaultDir => Path.Combine(LocalAppData, "Programs", "ResourceLens");
    static string DataDir => Path.Combine(LocalAppData, "ResourceLens");

    /// <summary>The folder of an existing install (to update in place), or null.</summary>
    public static string ExistingDir()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (k?.GetValue("InstallLocation") is string d && File.Exists(Path.Combine(d, ExeName))) return d;
        return File.Exists(Path.Combine(DefaultDir, ExeName)) ? DefaultDir : null;
    }

    /// <summary>Size of the app once unpacked (the gzip trailer stores it).</summary>
    public static long AppSize()
    {
        using var s = Payload();
        var tail = new byte[4];
        s.Seek(-4, SeekOrigin.End);
        s.Read(tail, 0, 4);
        return BitConverter.ToUInt32(tail, 0);
    }

    static Stream Payload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.gz")
        ?? throw new InvalidOperationException("payload missing");

    public static bool WebView2Present()
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client), (Registry.LocalMachine, @"SOFTWARE\" + client),
                     (Registry.CurrentUser, @"Software\" + client),
                 })
            using (var k = hive.OpenSubKey(path))
                if (k?.GetValue("pv") is string v && v.Length > 0 && v != "0.0.0.0") return true;
        return false;
    }

    public static void Install(string dir, bool autostart, bool desktop, string language, Action<int, string> progress)
    {
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, ExeName);

        progress(5, Strings.Closing);
        StopRunning(exe);

        progress(10, Strings.Copying);
        string tmp = exe + ".new";
        long total = Math.Max(1, AppSize()), done = 0;
        using (var src = Payload())
        using (var gz = new GZipStream(src, CompressionMode.Decompress))
        using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                done += n;
                progress(10 + (int)(done * 75 / total), Strings.Copying);
            }
        }
        if (File.Exists(exe)) File.Delete(exe);
        File.Move(tmp, exe);

        progress(88, Strings.Shortcuts);
        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ResourceLens.lnk"), exe);
        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ResourceLens.lnk");
        if (desktop) Shortcut(desk, exe);
        else if (File.Exists(desk)) File.Delete(desk);

        progress(94, Strings.Registering);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "ResourceLens");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "ResourceLens");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Preferences(exe, autostart, language);
        progress(100, Strings.Done);
    }

    public static void Launch(string dir) =>
        Process.Start(new ProcessStartInfo(Path.Combine(dir, ExeName)) { UseShellExecute = true, WorkingDirectory = dir });

    // ---------------------------------------------------------------- close a running copy

    static void StopRunning(string exe)
    {
        if (!File.Exists(exe) || !Locked(exe)) return;
        // Since 1.1 the app shuts down cleanly when asked (recording is saved first); older ones would just open.
        if (System.Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) && v >= new System.Version(1, 1))
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--exit") { UseShellExecute = false, CreateNoWindow = true });
                p.WaitForExit(35_000);
            }
            catch { }
        for (int i = 0; i < 20 && Locked(exe); i++) Thread.Sleep(250);
        if (!Locked(exe)) return;

        // Older versions: end the process; one running as administrator needs Windows' permission to be closed.
        foreach (var p in Process.GetProcessesByName("ResourceLens"))
        {
            string path = ImagePath(p.Id);
            if (path != null && !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
            try { p.Kill(); p.WaitForExit(5000); }
            catch (Win32Exception)
            {
                try
                {
                    using var k = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/F /PID {p.Id}")
                        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                    k?.WaitForExit(10_000);
                }
                catch (Win32Exception) { } // permission refused
            }
            catch (InvalidOperationException) { } // already gone
        }
        for (int i = 0; i < 20 && Locked(exe); i++) Thread.Sleep(250);
        if (Locked(exe)) throw new IOException(Strings.CloseFirst);
    }

    static bool Locked(string file)
    {
        try { using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>Executable path of a process, including one running as administrator (null if unknown).</summary>
    static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------------------------------------------------------- shortcuts / preferences

    static void Shortcut(string lnk, string exe)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        try
        {
            object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = s.GetType();
            void Set(string name, object value) => st.InvokeMember(name, BindingFlags.SetProperty, null, s, new[] { value });
            Set("TargetPath", exe);
            Set("WorkingDirectory", Path.GetDirectoryName(exe));
            Set("IconLocation", exe + ",0");
            Set("Description", Strings.Tagline);
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
            Marshal.FinalReleaseComObject(s);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>
    /// Autostart and (first install only) language go into the app's settings file; the app keeps its Run entry
    /// pointed at itself when autostart is on, so here it is only removed when off.
    /// </summary>
    static void Preferences(string exe, bool autostart, string language)
    {
        Directory.CreateDirectory(DataDir);
        string file = Path.Combine(DataDir, "settings.json");
        string on = autostart ? "true" : "false";
        string json = File.Exists(file) ? File.ReadAllText(file) : null;
        if (string.IsNullOrWhiteSpace(json))
            json = $"{{\n  \"autostart\": {on},\n  \"language\": \"{language}\"\n}}";
        else if (Regex.IsMatch(json, "\"autostart\"\\s*:\\s*(true|false)"))
            json = Regex.Replace(json, "(\"autostart\"\\s*:\\s*)(true|false)", "${1}" + on);
        else
        {
            int brace = json.LastIndexOf('}');
            string body = json.Substring(0, brace).TrimEnd();
            json = body + (body.EndsWith("{") ? "" : ",") + $"\n  \"autostart\": {on}\n}}";
        }
        File.WriteAllText(file, json, new UTF8Encoding(false));

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (autostart) run.SetValue("ResourceLens", $"\"{exe}\" --background");
        else run.DeleteValue("ResourceLens", false);
    }
}
