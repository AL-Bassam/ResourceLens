using System.Runtime.InteropServices;
using System.Text;

namespace ResourceLens;

internal static unsafe class Native
{
    // ---------------- Memory / CPU ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    // ---------------- Processes ----------------

    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------- PDH (language independent counters) ----------------

    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public uint Padding;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    // ---------------- Process details ----------------

    public const int ProcessCommandLineInformation = 60;

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, void* buffer, int length, out int returnLength);

    /// <summary>Command line of another process (same-user processes without admin; all with admin).</summary>
    public static string? GetCommandLine(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte* buf = (byte*)NativeMemory.Alloc((nuint)size);
                try
                {
                    int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                    if (st == STATUS_INFO_LENGTH_MISMATCH || st == unchecked((int)0xC0000023)) { size = Math.Max(needed, size * 2); continue; }
                    if (st < 0) return null;
                    ushort len = *(ushort*)buf;              // UNICODE_STRING.Length (bytes)
                    char* str = *(char**)(buf + 8);          // UNICODE_STRING.Buffer
                    return str == null ? null : new string(str, 0, len / 2);
                }
                finally { NativeMemory.Free(buf); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static string[] SplitArgs(string cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return Array.Empty<string>();
        IntPtr argv = CommandLineToArgvW(cmdLine, out int argc);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var res = new string[argc];
            for (int i = 0; i < argc; i++) res[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return res;
        }
        finally { LocalFree(argv); }
    }

    // ---------------- Services (which services live in each svchost) ----------------

    const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    const int SC_ENUM_PROCESS_INFO = 0;
    const uint SERVICE_WIN32 = 0x30;
    const uint SERVICE_ACTIVE = 0x1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? group);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    /// <summary>PID -> list of (service name, display name) for running Win32 services.</summary>
    public static Dictionary<uint, List<(string Name, string Display)>> GetServicesByPid()
    {
        var map = new Dictionary<uint, List<(string, string)>>();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return map;
        try
        {
            int resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0, out int needed, out _, ref resume, null);
            if (needed <= 0) return map;
            IntPtr buf = Marshal.AllocHGlobal(needed + 4096);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buf, needed + 4096, out _, out int count, ref resume, null))
                    return map;
                // ENUM_SERVICE_STATUS_PROCESSW: 2 pointers + SERVICE_STATUS_PROCESS (9 DWORDs) => 16 + 36 = 52, padded to 56
                int stride = IntPtr.Size * 2 + 36 + 4;
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buf + i * stride;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e)) ?? "";
                    string disp = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, IntPtr.Size)) ?? name;
                    uint pid = (uint)Marshal.ReadInt32(e, IntPtr.Size * 2 + 28); // dwProcessId
                    if (pid == 0) continue;
                    if (!map.TryGetValue(pid, out var list)) map[pid] = list = new();
                    list.Add((name, disp));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
        return map;
    }

    // ---------------- Foreground window ----------------

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hwnd, char* text, int max);

    public static string? GetWindowTitle(IntPtr hwnd)
    {
        char* buf = stackalloc char[260];
        int n = GetWindowTextW(hwnd, buf, 260);
        return n > 0 ? new string(buf, 0, n) : null;
    }

    // ---------------- Being a good background citizen ----------------

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, void* info, int size);

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint min, nint max, uint flags);

    const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    const int ProcessMemoryPriority = 0;
    const int ProcessPowerThrottling = 4;

    /// <summary>Below-normal CPU priority + low memory priority: games and streaming always win.</summary>
    public static void BecomeBackgroundFriendly()
    {
        var self = GetCurrentProcess();
        SetPriorityClass(self, BELOW_NORMAL_PRIORITY_CLASS);
        uint memPriority = 2; // MEMORY_PRIORITY_LOW
        SetProcessInformation(self, ProcessMemoryPriority, &memPriority, sizeof(uint));
    }

    /// <summary>Windows 11 "Efficiency mode" (EcoQoS) for this process: on while no window is shown.</summary>
    public static void SetEfficiencyMode(bool on)
    {
        uint* state = stackalloc uint[3];
        state[0] = 1;                 // PROCESS_POWER_THROTTLING_CURRENT_VERSION
        state[1] = 0x1;               // EXECUTION_SPEED
        state[2] = on ? 0x1u : 0u;
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, state, 12);
    }

    /// <summary>Hand unused pages back to Windows (after the dashboard window closes).</summary>
    public static void TrimWorkingSet() => SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);

    // ---------------- Misc ----------------

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsUserAnAdmin();

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
    }
}
