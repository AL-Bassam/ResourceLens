using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceLensSetup;

/// <summary>
/// Opened from inside another app's MSIX container (a Store messaging app opening the received setup, the Claude
/// desktop app…), Windows redirects AppData files and HKCU registry writes into that app's private storage: the
/// install would be invisible to Windows (no "Installed apps" entry, no autostart). Start again outside it.
/// </summary>
internal static class Sandbox
{
    const string OutsideFlag = "--outside";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint size, uint flags);

    public static bool IsRedirected(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".probe-" + Guid.NewGuid().ToString("N"));
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            var sb = new StringBuilder(1024);
            if (GetFinalPathNameByHandle(fs.SafeFileHandle, sb, (uint)sb.Capacity, 0) == 0) return false;
            string real = sb.ToString();
            if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) real = @"\\" + real.Substring(8);
            else if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real.Substring(4);
            return !real.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Starts this setup again, same arguments, through WMI (outside any container). False if already tried or failed.</summary>
    public static bool RelaunchOutside(string[] args)
    {
        if (args.Contains(OutsideFlag)) return false;
        try
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using var cls = new ManagementClass("Win32_Process");
            using var input = cls.GetMethodParameters("Create");
            input["CommandLine"] = ($"\"{exe}\" " + string.Join(" ", args.Select(Quote)) + " " + OutsideFlag).Replace("  ", " ");
            input["CurrentDirectory"] = Path.GetDirectoryName(exe);
            using var result = cls.InvokeMethod("Create", input, null);
            return Convert.ToUInt32(result["ReturnValue"]) == 0;
        }
        catch { return false; }
    }

    static string Quote(string a) => a.Length > 0 && a.IndexOf(' ') < 0 && a.IndexOf('"') < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
