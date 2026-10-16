using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResourceLens;

/// <summary>
/// Turns a raw process (exe + command line) into a human description of what that process is doing inside
/// its application: a browser tab renderer, the GPU process, a Windows service inside svchost, a script, ...
/// Role codes are translated by the UI; Detail is free text.
/// </summary>
internal static partial class ProcessDetails
{
    public readonly record struct Info(string Role, string? Detail, string? CommandLine);

    static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe", "pythonw.exe", "py.exe", "node.exe", "java.exe", "javaw.exe", "powershell.exe", "pwsh.exe",
        "cmd.exe", "wscript.exe", "cscript.exe", "dotnet.exe", "ruby.exe", "php.exe", "perl.exe", "bun.exe", "deno.exe",
        "rundll32.exe", "mshta.exe", "bash.exe", "wsl.exe",
    };

    /// <summary>Descriptions for system processes whose path is not readable without admin rights.</summary>
    public static readonly Dictionary<string, string> KnownSystem = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "Windows Kernel (System)",
        ["Registry"] = "Windows Registry",
        ["Memory Compression"] = "Windows Memory Compression",
        ["Secure System"] = "Virtualization-based security",
        ["smss.exe"] = "Session Manager",
        ["csrss.exe"] = "Client Server Runtime",
        ["wininit.exe"] = "Windows Start-Up Application",
        ["winlogon.exe"] = "Windows Logon",
        ["services.exe"] = "Services and Controller app",
        ["lsass.exe"] = "Local Security Authority",
        ["svchost.exe"] = "Service Host",
        ["MsMpEng.exe"] = "Microsoft Defender Antivirus",
        ["NisSrv.exe"] = "Microsoft Defender Network Inspection",
        ["MpDefenderCoreService.exe"] = "Microsoft Defender Core Service",
        ["SecurityHealthService.exe"] = "Windows Security Health Service",
        ["dwm.exe"] = "Desktop Window Manager",
        ["fontdrvhost.exe"] = "Font Driver Host",
        ["audiodg.exe"] = "Windows Audio Device Graph",
        ["spoolsv.exe"] = "Print Spooler",
        ["SearchIndexer.exe"] = "Windows Search Indexer",
        ["WmiPrvSE.exe"] = "WMI Provider Host",
        ["SgrmBroker.exe"] = "System Guard Runtime Monitor",
        ["LsaIso.exe"] = "Credential Guard",
        ["vmmem"] = "Virtual machine / WSL memory",
        ["vmmemWSL"] = "WSL virtual machine memory",
        ["vmcompute.exe"] = "Hyper-V Host Compute Service",
        ["MemCompression"] = "Windows Memory Compression",
    };

    public static Info Describe(string exe, string? cmd, List<(string Name, string Display)>? services)
    {
        if (services is { Count: > 0 })
            return new Info("service", string.Join(" · ", services.Select(s => $"{s.Display} ({s.Name})")), Redact(Trim(cmd, 300)));

        if (string.IsNullOrWhiteSpace(cmd)) return new Info(KnownSystem.ContainsKey(exe) ? "system" : "app", null, null);

        string[] args = Native.SplitArgs(cmd);
        string? safeCmd = Redact(Trim(cmd, 500));

        // ---- Chromium / Electron family (browsers, WebView2, Discord, VS Code, Claude, Teams, ...) ----
        string? type = Flag(args, "--type");
        if (type is not null || args.Any(a => a.StartsWith("--webview-exe-name=", StringComparison.OrdinalIgnoreCase)))
        {
            string? host = Flag(args, "--webview-exe-name");
            string? hostNote = host is null ? null : $"WebView2 · {host}";
            string role = type switch
            {
                null => "main",
                "renderer" when args.Contains("--extension-process") => "extension",
                "renderer" => "renderer",
                "gpu-process" => "gpu",
                "crashpad-handler" => "crashpad",
                "utility" => UtilityRole(Flag(args, "--utility-sub-type")),
                _ => "utility",
            };
            string? detail = role == "utility" ? PrettySubType(Flag(args, "--utility-sub-type") ?? type) : null;
            return new Info(role, Join(hostNote, detail), safeCmd);
        }

        // ---- Firefox content processes ----
        if (args.Contains("-contentproc"))
        {
            string last = args[^1].ToLowerInvariant();
            return new Info(last switch { "tab" => "renderer", "gpu" => "gpu", "socket" => "network", "rdd" => "media", _ => "utility" },
                last is "tab" or "gpu" or "socket" ? null : last, safeCmd);
        }

        // ---- COM surrogate: name the hosted COM class ----
        if (exe.Equals("dllhost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = GuidRegex().Match(cmd);
            return new Info("com", m.Success ? ComName(m.Value) ?? m.Value : null, safeCmd);
        }

        // ---- Script hosts: show what script / command is running ----
        if (ScriptHosts.Contains(exe))
            return new Info("script", Redact(ScriptDetail(exe, args)), safeCmd);

        // ---- Anything else: its arguments, if any ----
        string rest = string.Join(' ', args.Skip(1).Where(a => !string.IsNullOrWhiteSpace(a)));
        return new Info("app", string.IsNullOrWhiteSpace(rest) ? null : Redact(Trim(rest, 140)), safeCmd);
    }

    static string UtilityRole(string? sub) => sub switch
    {
        "network.mojom.NetworkService" => "network",
        "storage.mojom.StorageService" => "storage",
        "audio.mojom.AudioService" => "audio",
        "video_capture.mojom.VideoCaptureService" => "video",
        _ => "utility",
    };

    static string? PrettySubType(string? sub)
    {
        if (string.IsNullOrEmpty(sub)) return null;
        int i = sub.LastIndexOf('.');
        return i > 0 ? sub[(i + 1)..] : sub; // e.g. "DataDecoderService"
    }

    static string? ScriptDetail(string exe, string[] args)
    {
        var a = args.Skip(1).ToList();
        if (a.Count == 0) return null;
        string lower = exe.ToLowerInvariant();
        if (lower is "powershell.exe" or "pwsh.exe")
        {
            int f = a.FindIndex(x => x.Equals("-File", StringComparison.OrdinalIgnoreCase) || x.Equals("-f", StringComparison.OrdinalIgnoreCase));
            if (f >= 0 && f + 1 < a.Count) return Path.GetFileName(a[f + 1]);
            if (a.Any(x => x.StartsWith("-enc", StringComparison.OrdinalIgnoreCase) || x.StartsWith("-e", StringComparison.OrdinalIgnoreCase) && x.Length <= 3)) return "[encoded command]";
            int c = a.FindIndex(x => x.Equals("-Command", StringComparison.OrdinalIgnoreCase) || x.Equals("-c", StringComparison.OrdinalIgnoreCase));
            if (c >= 0 && c + 1 < a.Count) return Trim(string.Join(' ', a.Skip(c + 1)), 100);
        }
        if (lower is "cmd.exe")
            return Trim(string.Join(' ', a.SkipWhile(x => !x.StartsWith("/c", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("/k", StringComparison.OrdinalIgnoreCase)).Skip(1)), 100);
        if (lower is "python.exe" or "pythonw.exe" or "py.exe")
        {
            int m = a.IndexOf("-m");
            if (m >= 0 && m + 1 < a.Count) return "-m " + a[m + 1];
        }
        if (lower is "java.exe" or "javaw.exe")
        {
            int j = a.IndexOf("-jar");
            if (j >= 0 && j + 1 < a.Count) return Path.GetFileName(a[j + 1]);
            var main = a.FirstOrDefault(x => !x.StartsWith('-') && !x.Contains(';') && !x.Contains('\\'));
            if (main is not null) return main;
        }
        // first non-option argument = the script / target, plus a few following arguments
        int idx = a.FindIndex(x => !x.StartsWith('-') && !x.StartsWith('/'));
        if (idx < 0) return Trim(string.Join(' ', a), 100);
        string target = a[idx];
        string shown = target.Contains('\\') || target.Contains('/') ? Path.GetFileName(target.TrimEnd('\\', '/')) : target;
        // npm/npx/yarn shims: show the command after the cli script
        if (shown.EndsWith("-cli.js", StringComparison.OrdinalIgnoreCase) && idx + 1 < a.Count) shown += " " + string.Join(' ', a.Skip(idx + 1).Take(3));
        else if (idx + 1 < a.Count) shown += " " + string.Join(' ', a.Skip(idx + 1).Take(2));
        return Trim(shown, 100);
    }

    static readonly Dictionary<string, string?> ComCache = new(StringComparer.OrdinalIgnoreCase);

    static string? ComName(string guid)
    {
        lock (ComCache)
        {
            if (ComCache.TryGetValue(guid, out var cached)) return cached;
            string? name = null;
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey($@"AppID\{guid}")) name = k?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(name))
                    using (var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{guid}")) name = k?.GetValue(null) as string;
            }
            catch { }
            return ComCache[guid] = string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }

    static string? Flag(string[] args, string name)
    {
        foreach (var a in args)
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
        return null;
    }

    static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} · {b}";

    static string? Trim(string? s, int max)
    {
        if (s is null) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Hide secrets that sometimes appear in command lines (tokens, passwords, keys).</summary>
    public static string? Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = SecretFlagRegex().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        return SecretQueryRegex().Replace(s, m => m.Groups[1].Value + "=***");
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"(--?[\w.-]*(?:token|secret|password|passwd|pwd|apikey|api-key|api_key|auth|credential|cookie|session)[\w.-]*)([=\s:]+)(""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlagRegex();

    [GeneratedRegex(@"\b(token|access_token|key|secret|password|sig|signature|auth|code)=([^&\s""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryRegex();
}

/// <summary>Cached PID -> services map, refreshed only when an unknown service host shows up.</summary>
internal sealed class ServiceMap
{
    Dictionary<uint, List<(string Name, string Display)>> _map = new();
    DateTime _last = DateTime.MinValue;

    public List<(string Name, string Display)>? Get(uint pid, bool isServiceHost)
    {
        if (_map.TryGetValue(pid, out var list)) return list;
        if (isServiceHost && (DateTime.UtcNow - _last).TotalSeconds > 5)
        {
            _last = DateTime.UtcNow;
            try { _map = Native.GetServicesByPid(); } catch { }
            return _map.GetValueOrDefault(pid);
        }
        return null;
    }
}
