using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceLens;

/// <summary>
/// Started from inside another app's MSIX container (the Claude desktop app, a Store messaging app opening a received
/// file…), Windows silently redirects %LOCALAPPDATA% / %APPDATA% writes into that app's private storage, while existing
/// files are still read and written in place. The history database would then be split between two places and get
/// damaged by the next normal start. Detect it and start again outside the container.
/// </summary>
internal static class Sandbox
{
    const string OutsideFlag = "--outside";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint size, uint flags);

    /// <summary>True when a file created in <paramref name="dir"/> really lands somewhere else.</summary>
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
            if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) real = @"\\" + real[8..];
            else if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real[4..];
            return !real.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts this exe again with the same arguments through WMI, whose new processes are outside any container.
    /// Returns false if that was already tried (the flag) or failed; the caller then carries on here.
    /// </summary>
    public static bool RelaunchOutside(string[] args)
    {
        if (args.Contains(OutsideFlag)) { Log.Write("sandbox: still redirected after restarting outside; continuing"); return false; }
        try
        {
            using var cls = new ManagementClass("Win32_Process");
            using var input = cls.GetMethodParameters("Create");
            input["CommandLine"] = $"\"{Environment.ProcessPath}\" {string.Join(' ', args.Select(Quote))} {OutsideFlag}".TrimEnd();
            input["CurrentDirectory"] = AppContext.BaseDirectory;
            using var result = cls.InvokeMethod("Create", input, null);
            return Convert.ToUInt32(result["ReturnValue"]) == 0;
        }
        catch (Exception ex) { Log.Write("sandbox relaunch", ex); return false; }
    }

    static string Quote(string a) => a.Length > 0 && !a.Contains(' ') && !a.Contains('"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
