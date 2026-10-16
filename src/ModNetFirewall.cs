using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// Blocks an application's network access with a pair of Windows Firewall rules (inbound + outbound, program = exe
/// path) named "ResourceLens block: &lt;exe&gt;", so they are easy to find and remove in wf.msc too. Uses netsh: no COM
/// interop to keep alive in the trimmed build. Changing rules needs administrator rights; reading them does not.
/// </summary>
internal static unsafe class ModNetFirewall
{
    public const string Prefix = "ResourceLens block: ";

    /// <summary>Windows itself (or this app): cutting these off breaks DNS, updates, sign-in or the desktop.</summary>
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "MsMpEng.exe", "audiodg.exe", "ResourceLens.exe",
    };

    /// <summary>Kernel pseudo-processes have no .exe name and no program path a rule could match.</summary>
    public static bool IsProtected(string name) =>
        Protected.Contains(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public readonly record struct Result(bool Ok, string? Error = null, string? Detail = null);

    static readonly object Gate = new();
    static List<string>? _rules;   // exe names with a ResourceLens rule, from the last listing
    static DateTime _rulesAt;

    public static Result Block(string name, string path)
    {
        if (!Native.IsUserAnAdmin()) return new(false, "access");
        if (IsProtected(name)) return new(false, "critical", name);
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || !File.Exists(path)
            || !Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
            return new(false, "no-path", path);
        string rule = Prefix + name;
        lock (Gate)
        {
            Netsh($"advfirewall firewall delete rule name=\"{rule}\""); // an older rule may point at another copy of the exe
            foreach (var dir in new[] { "out", "in" })
            {
                var (code, output) = Netsh($"advfirewall firewall add rule name=\"{rule}\" dir={dir} action=block program=\"{path}\" enable=yes profile=any description=\"Added by ResourceLens\"");
                if (code != 0)
                {
                    Netsh($"advfirewall firewall delete rule name=\"{rule}\"");
                    return new(false, "failed", output.Trim());
                }
            }
            if (_rules is not null && !_rules.Contains(name, StringComparer.OrdinalIgnoreCase)) _rules.Add(name);
        }
        return new(true);
    }

    public static Result Unblock(string name)
    {
        if (!Native.IsUserAnAdmin()) return new(false, "access");
        if (name.Length == 0 || name.Contains('"')) return new(false, "failed");
        lock (Gate)
        {
            var (code, output) = Netsh($"advfirewall firewall delete rule name=\"{Prefix + name}\"");
            _rules?.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            // exit code 1 with "No rules match" means it was already gone: the wanted state either way
            if (code != 0 && List(true).Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, "failed", output.Trim());
        }
        return new(true);
    }

    /// <summary>Exe names that currently have a ResourceLens block rule (netsh listing, cached for 30 s).</summary>
    public static List<string> List(bool fresh = false)
    {
        lock (Gate)
        {
            if (!fresh && _rules is not null && DateTime.UtcNow - _rulesAt < TimeSpan.FromSeconds(30)) return new(_rules);
            // Every rule of ours has an outbound half; listing one direction halves netsh's work (~0.5 s).
            var (code, output) = Netsh("advfirewall firewall show rule name=all dir=out");
            if (code != 0 && output.Length == 0) return _rules is null ? new() : new(_rules);
            var found = new List<string>();
            // Labels ("Rule Name:") are localized, so look for the prefix itself rather than parsing fields.
            foreach (var line in output.Split('\n'))
            {
                int i = line.IndexOf(Prefix, StringComparison.Ordinal);
                if (i < 0) continue;
                string name = line[(i + Prefix.Length)..].Trim();
                if (name.Length > 0 && !found.Contains(name, StringComparer.OrdinalIgnoreCase)) found.Add(name);
            }
            _rules = found;
            _rulesAt = DateTime.UtcNow;
            return new(found);
        }
    }

    /// <summary>Last known list without running netsh (null before the first listing).</summary>
    public static List<string>? Cached() { lock (Gate) return _rules is null ? null : new(_rules); }

    [DllImport("kernel32.dll")]
    static extern int MultiByteToWideChar(uint codePage, uint flags, byte* src, int srcLen, char* dst, int dstLen);

    /// <summary>netsh writes its redirected output in the OEM code page (720 on Arabic Windows, 437 on English).</summary>
    static string Oem(byte[] b)
    {
        if (b.Length == 0) return "";
        fixed (byte* s = b)
        {
            int n = MultiByteToWideChar(1 /* CP_OEMCP */, 0, s, b.Length, null, 0);
            var chars = new char[n];
            fixed (char* d = chars) MultiByteToWideChar(1, 0, s, b.Length, d, n);
            return new string(chars);
        }
    }

    static (int Code, string Output) Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var ms = new MemoryStream();
            var read = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(20_000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
            read.Wait(2000); err.Wait(2000);
            return (p.ExitCode, Oem(ms.ToArray()));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
