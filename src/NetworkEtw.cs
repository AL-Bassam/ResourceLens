using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// Per-process network bytes using the Microsoft-Windows-Kernel-Network ETW provider.
/// Real-time ETW sessions require administrator rights; when unavailable this class reports IsRunning=false.
/// </summary>
internal sealed unsafe class NetworkEtw : IDisposable
{
    const string SessionName = "ResourceLens-Network";
    static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    const uint EVENT_TRACE_CONTROL_STOP = 1;
    const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
    const int PropsSize = 120;
    const int PropsTotal = PropsSize + 2048;
    const int LogFileSize = 448;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern uint StartTraceW(out ulong handle, string name, IntPtr props);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern uint ControlTraceW(ulong handle, string name, IntPtr props, uint code);

    [DllImport("advapi32.dll")]
    static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint controlCode, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll")]
    static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    static extern uint CloseTrace(ulong handle);

    static NetworkEtw? _current;

    ulong _sessionHandle;
    ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    IntPtr _logFile;
    Thread? _thread;
    readonly object _lock = new();
    Dictionary<uint, (long rx, long tx)> _acc = new();

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public static NetworkEtw TryStart()
    {
        var n = new NetworkEtw();
        try { n.Start(); }
        catch (Exception ex) { n.Error = ex.Message; n.Dispose(); }
        return n;
    }

    static IntPtr AllocProps()
    {
        IntPtr p = Marshal.AllocHGlobal(PropsTotal);
        new Span<byte>((void*)p, PropsTotal).Clear();
        byte* b = (byte*)p;
        *(uint*)(b + 0) = PropsTotal;                 // Wnode.BufferSize
        *(uint*)(b + 40) = 1;                         // Wnode.ClientContext = QPC
        *(uint*)(b + 44) = WNODE_FLAG_TRACED_GUID;    // Wnode.Flags
        *(uint*)(b + 48) = 64;                        // BufferSize (KB)
        *(uint*)(b + 52) = 4;                         // MinimumBuffers
        *(uint*)(b + 56) = 16;                        // MaximumBuffers
        *(uint*)(b + 64) = EVENT_TRACE_REAL_TIME_MODE;
        *(uint*)(b + 68) = 1;                         // FlushTimer (s)
        *(uint*)(b + 116) = PropsSize;                // LoggerNameOffset
        return p;
    }

    static void StopSession()
    {
        IntPtr p = AllocProps();
        try { ControlTraceW(0, SessionName, p, EVENT_TRACE_CONTROL_STOP); }
        finally { Marshal.FreeHGlobal(p); }
    }

    void Start()
    {
        if (!Native.IsUserAnAdmin()) throw new UnauthorizedAccessException("Administrator rights required");

        StopSession(); // stale session from a previous crash
        IntPtr props = AllocProps();
        try
        {
            uint st = StartTraceW(out _sessionHandle, SessionName, props);
            if (st != 0) throw new InvalidOperationException($"StartTrace failed ({st})");
        }
        finally { Marshal.FreeHGlobal(props); }

        // IPv4 (0x10) + IPv6 (0x20) keywords
        uint en = EnableTraceEx2(_sessionHandle, KernelNetworkProvider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, 0x30, 0, 0, IntPtr.Zero);
        if (en != 0) { StopSession(); throw new InvalidOperationException($"EnableTraceEx2 failed ({en})"); }

        _logFile = Marshal.AllocHGlobal(LogFileSize);
        new Span<byte>((void*)_logFile, LogFileSize).Clear();
        byte* lf = (byte*)_logFile;
        *(IntPtr*)(lf + 8) = Marshal.StringToHGlobalUni(SessionName);   // LoggerName
        *(uint*)(lf + 28) = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
        *(IntPtr*)(lf + 424) = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEvent; // EventRecordCallback

        _current = this;
        _traceHandle = OpenTraceW(_logFile);
        if (_traceHandle == INVALID_PROCESSTRACE_HANDLE) { StopSession(); throw new InvalidOperationException("OpenTrace failed"); }

        _thread = new Thread(() =>
        {
            ulong h = _traceHandle;
            ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
        })
        { IsBackground = true, Name = "ETW network", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        IsRunning = true;
    }

    [UnmanagedCallersOnly]
    static void OnEvent(byte* record)
    {
        var self = _current;
        if (self is null) return;
        ushort id = *(ushort*)(record + 40);
        ushort len = *(ushort*)(record + 86);
        byte* data = *(byte**)(record + 96);
        if (data == null || len < 8) return;

        bool send = id is 10 or 26 or 42 or 58;
        bool recv = id is 11 or 27 or 43 or 59;
        if (!send && !recv) return;

        uint pid = *(uint*)data;
        uint size = *(uint*)(data + 4);
        lock (self._lock)
        {
            var cur = self._acc.GetValueOrDefault(pid);
            self._acc[pid] = send ? (cur.rx, cur.tx + size) : (cur.rx + size, cur.tx);
        }
    }

    /// <summary>Bytes per PID since the previous call.</summary>
    public Dictionary<uint, (long rx, long tx)> TakeDeltas()
    {
        lock (_lock)
        {
            var d = _acc;
            _acc = new Dictionary<uint, (long rx, long tx)>(Math.Max(16, d.Count));
            return d;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        if (_sessionHandle != 0) { StopSession(); _sessionHandle = 0; }
        if (_traceHandle != INVALID_PROCESSTRACE_HANDLE) { CloseTrace(_traceHandle); _traceHandle = INVALID_PROCESSTRACE_HANDLE; }
        _thread?.Join(2000);
        if (_logFile != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(*(IntPtr*)((byte*)_logFile + 8));
            Marshal.FreeHGlobal(_logFile);
            _logFile = IntPtr.Zero;
        }
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
