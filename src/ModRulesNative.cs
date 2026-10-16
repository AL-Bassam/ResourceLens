using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ResourceLens;

/// <summary>
/// The Windows side of process rules: priority class, CPU affinity, EcoQoS, I/O priority, terminate, power plans and the
/// CPU topology. Every process call first checks the creation time, so a PID reused since the sample is never touched.
/// </summary>
internal static unsafe class RulesOs
{
    public enum Result { Ok, Denied, Gone, Failed, Skipped }

    const uint PROCESS_TERMINATE = 0x0001, PROCESS_SET_INFORMATION = 0x0200, PROCESS_QUERY_INFORMATION = 0x0400,
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessPowerThrottling = 4, ProcessIoPriority = 33;
    const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    public const uint IdleClass = 0x40, BelowNormalClass = 0x4000, NormalClass = 0x20, AboveNormalClass = 0x8000,
        HighClass = 0x80, RealtimeClass = 0x100;

    /// <summary>Rule levels to priority classes; anything else (realtime included) means "leave it".</summary>
    public static uint? PriorityClass(string? level) => level switch
    {
        "idle" => IdleClass, "below" => BelowNormalClass, "normal" => NormalClass, "above" => AboveNormalClass, "high" => HighClass,
        _ => null,
    };

    public static string ClassName(uint cls) => cls switch
    {
        IdleClass => "idle", BelowNormalClass => "below", AboveNormalClass => "above", HighClass => "high", RealtimeClass => "realtime",
        _ => "normal",
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetPriorityClass(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(IntPtr h, uint cls);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessAffinityMask(IntPtr h, out nuint mask, out nuint system);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessAffinityMask(IntPtr h, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int cls, void* info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, void* info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("ntdll.dll")]
    static extern int NtSetInformationProcess(IntPtr h, int cls, void* info, int size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, void* info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    /// <summary>Opens a process only if it is still the running instance that was sampled.</summary>
    static IntPtr Open(uint pid, long created, uint access, out Result err)
    {
        IntPtr h = OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            // Windows also says "access denied" for a process that has just exited: only a live one needs administrator.
            err = Marshal.GetLastWin32Error() == 5 && Alive(pid, created) ? Result.Denied : Result.Gone;
            return IntPtr.Zero;
        }
        if (!Same(h, created))
        {
            CloseHandle(h);
            err = Result.Gone;
            return IntPtr.Zero;
        }
        err = Result.Ok;
        return h;
    }

    static bool Same(IntPtr h, long created) =>
        (created == 0 || GetProcessTimes(h, out long c, out _, out _, out _) && c == created)
        && GetExitCodeProcess(h, out uint code) && code == 259 /* STILL_ACTIVE */;

    static bool Alive(uint pid, long created)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5; // not even queryable: a protected, running process
        try { return Same(h, created); }
        finally { CloseHandle(h); }
    }

    static Result Err(int win32) => win32 == 5 ? Result.Denied : Result.Failed;
    static Result First(Result cur, Result next) => cur == Result.Ok ? next : cur;

    /// <summary>One rule on one process. Every part is attempted; the first failure is what gets reported.</summary>
    public static Result Apply(uint pid, long created, uint? cls, ulong? mask, bool? eco, int? io)
    {
        IntPtr h = Open(pid, created, PROCESS_SET_INFORMATION, out var res);
        if (h == IntPtr.Zero) return res;
        try
        {
            if (eco is bool on)
            {
                // PROCESS_POWER_THROTTLING_STATE: version 1, control EXECUTION_SPEED; state on = EcoQoS, off = never throttle
                uint* st = stackalloc uint[3];
                st[0] = 1; st[1] = 1; st[2] = on ? 1u : 0u;
                if (!SetProcessInformation(h, ProcessPowerThrottling, st, 12)) res = First(res, Err(Marshal.GetLastWin32Error()));
            }
            if (cls is uint c && !SetPriorityClass(h, c)) res = First(res, Err(Marshal.GetLastWin32Error()));
            if (mask is ulong m)
            {
                // Only processors that exist (and that the process' group has) can be set.
                ulong allowed = GetProcessAffinityMask(h, out _, out nuint sys) ? m & sys : m;
                if (allowed != 0 && !SetProcessAffinityMask(h, (nuint)allowed)) res = First(res, Err(Marshal.GetLastWin32Error()));
            }
            if (io is int v)
            {
                int status = NtSetInformationProcess(h, ProcessIoPriority, &v, sizeof(int));
                if (status < 0) res = First(res, status == STATUS_ACCESS_DENIED ? Result.Denied : Result.Failed);
            }
            return res;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>ProBalance: lowers a normal / above-normal process to <paramref name="target"/> and returns its class.</summary>
    public static Result Restrain(uint pid, long created, uint target, out uint orig)
    {
        orig = 0;
        IntPtr h = Open(pid, created, PROCESS_SET_INFORMATION, out var res);
        if (h == IntPtr.Zero) return res;
        try
        {
            orig = GetPriorityClass(h);
            if (orig == 0) return Err(Marshal.GetLastWin32Error());
            if (orig is not (NormalClass or AboveNormalClass)) return Result.Skipped; // already low, or raised on purpose
            return SetPriorityClass(h, target) ? Result.Ok : Err(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Puts the original class back, unless something else changed the priority meanwhile (then theirs stays).</summary>
    public static Result Restore(uint pid, long created, uint target, uint orig)
    {
        IntPtr h = Open(pid, created, PROCESS_SET_INFORMATION, out var res);
        if (h == IntPtr.Zero) return res;
        try
        {
            if (GetPriorityClass(h) != target) return Result.Skipped;
            return SetPriorityClass(h, orig) ? Result.Ok : Err(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(h); }
    }

    public static Result Kill(uint pid, long created)
    {
        if (pid <= 4 || pid == (uint)Environment.ProcessId) return Result.Skipped;
        IntPtr h = Open(pid, created, PROCESS_TERMINATE, out var res);
        if (h == IntPtr.Zero) return res;
        try { return TerminateProcess(h, 1) ? Result.Ok : Err(Marshal.GetLastWin32Error()); }
        finally { CloseHandle(h); }
    }

#if DEBUG
    /// <summary>Test hook: what a process currently has (Get-Process shows neither EcoQoS nor I/O priority).</summary>
    public static object Inspect(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return new { ok = false, error = Marshal.GetLastWin32Error() == 5 ? "access-denied" : "not-running" };
        try
        {
            uint cls = GetPriorityClass(h);
            GetProcessAffinityMask(h, out nuint mask, out nuint sys);
            uint* st = stackalloc uint[3];
            st[0] = 1; st[1] = 0; st[2] = 0;
            bool eco = GetProcessInformation(h, ProcessPowerThrottling, st, 12);
            int io = -1;
            NtQueryInformationProcess(h, ProcessIoPriority, &io, sizeof(int), out _);
            return new
            {
                ok = true, priority = ClassName(cls), affinity = ((ulong)mask).ToString("X"), system = ((ulong)sys).ToString("X"),
                ecoControl = eco ? st[1] : (uint?)null, ecoState = eco ? st[2] : (uint?)null, io,
            };
        }
        finally { CloseHandle(h); }
    }
#endif

    // ---------------- Power plans ----------------

    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr root, Guid* scheme, Guid* subGroup, uint access, uint index, byte* buffer, ref uint size);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadFriendlyName(IntPtr root, Guid* scheme, Guid* subGroup, Guid* setting, byte* buffer, ref uint size);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr root, Guid* scheme);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return *(Guid*)p; }
        finally { LocalFree(p); }
    }

    /// <summary>0 on success, otherwise the Win32 error code.</summary>
    public static uint SetPlan(Guid g) => PowerSetActiveScheme(IntPtr.Zero, &g);

    public static List<(Guid Guid, string Name)> Plans()
    {
        var list = new List<(Guid Guid, string Name)>();
        for (uint i = 0; i < 64; i++)
        {
            Guid buf = default;
            uint size = (uint)sizeof(Guid);
            if (PowerEnumerate(IntPtr.Zero, null, null, 16 /* ACCESS_SCHEME */, i, (byte*)&buf, ref size) != 0) break;
            var g = buf;
            if (!list.Exists(x => x.Guid == g)) list.Add((g, PlanName(g) ?? g.ToString()));
        }
        return list.Count > 0 ? list : PowercfgList();
    }

    /// <summary>The name Windows shows for a plan (already in the user's language).</summary>
    public static string? PlanName(Guid g)
    {
        uint size = 0;
        uint rc = PowerReadFriendlyName(IntPtr.Zero, &g, null, null, null, ref size);
        if (rc is not (0 or 234 /* ERROR_MORE_DATA */) || size < 2 || size > 4096) return null;
        byte* buf = stackalloc byte[(int)size];
        if (PowerReadFriendlyName(IntPtr.Zero, &g, null, null, buf, ref size) != 0) return null;
        string s = new string((char*)buf, 0, (int)size / 2).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    /// <summary>Fallback where PowerEnumerate fails: parse "powercfg /list" (API calls only, never per sample).</summary>
    static List<(Guid Guid, string Name)> PowercfgList()
    {
        var list = new List<(Guid Guid, string Name)>();
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/d /c chcp 65001 >nul & powercfg /list")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return list;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (Match m in Regex.Matches(text, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)"))
                if (Guid.TryParse(m.Groups[1].Value, out var g) && !list.Exists(x => x.Guid == g)) list.Add((g, m.Groups[2].Value.Trim()));
        }
        catch (Exception ex) { Log.Write("rules: powercfg", ex); }
        return list;
    }

    // ---------------- CPU topology ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(byte* info, uint length, out uint returned, IntPtr process, uint flags);

    public sealed record CpuInfo(int Index, int Core, int Eff);

    /// <summary>Logical processors of group 0 (what an affinity mask addresses), with their core and efficiency class.</summary>
    public static List<CpuInfo> Cpus()
    {
        var list = new List<CpuInfo>();
        GetSystemCpuSetInformation(null, 0, out uint need, IntPtr.Zero, 0);
        if (need is > 0 and < 1 << 20)
        {
            byte* b = (byte*)NativeMemory.Alloc(need);
            try
            {
                if (GetSystemCpuSetInformation(b, need, out uint got, IntPtr.Zero, 0))
                    for (uint off = 0; off + 20 <= got;)
                    {
                        // SYSTEM_CPU_SET_INFORMATION: Size, Type, Id, Group, LogicalProcessorIndex, CoreIndex, LLC, NUMA, EfficiencyClass
                        uint size = *(uint*)(b + off);
                        if (size < 20) break;
                        if (*(int*)(b + off + 4) == 0 && *(ushort*)(b + off + 12) == 0)
                            list.Add(new CpuInfo(b[off + 14], b[off + 15], b[off + 18]));
                        off += size;
                    }
            }
            finally { NativeMemory.Free(b); }
        }
        if (list.Count == 0)
            for (int i = 0; i < Math.Min(64, Environment.ProcessorCount); i++) list.Add(new CpuInfo(i, i, 0));
        list.Sort((a, c) => a.Index.CompareTo(c.Index));
        return list;
    }
}
