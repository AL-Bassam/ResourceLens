using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// Reads every process in one kernel call (NtQuerySystemInformation / SystemProcessInformation).
/// No per-process handles are opened, so it works for protected/system processes without admin.
/// </summary>
internal sealed unsafe class ProcessScanner : IDisposable
{
    public struct Entry
    {
        public uint Pid;
        public long CreateTime;
        public string Name;
        public long CpuTime100ns;        // user + kernel
        public long PrivateWorkingSet;   // what Task Manager shows as "Memory"
        public long WorkingSet;
        public long Commit;              // private bytes
        public long ReadBytes;
        public long WriteBytes;
        public long OtherBytes;
        public int Handles;
        public int Threads;
        public int BasePriority;         // 4 idle, 6 below normal, 8 normal, 10 above normal, 13 high, 24 realtime
        public uint ParentPid;
        public uint SessionId;
    }

    IntPtr _buffer;
    int _size = 1 << 20;
    readonly Dictionary<string, string> _nameIntern = new(StringComparer.Ordinal);

    public ProcessScanner() => _buffer = Marshal.AllocHGlobal(_size);

    public List<Entry> Scan()
    {
        int status;
        while (true)
        {
            status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, (void*)_buffer, _size, out int needed);
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
            Marshal.FreeHGlobal(_buffer);
            _size = Math.Max(needed + (256 << 10), _size * 2);
            _buffer = Marshal.AllocHGlobal(_size);
        }
        if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed 0x{status:X8}");

        var list = new List<Entry>(400);
        byte* p = (byte*)_buffer;
        while (true)
        {
            uint next = *(uint*)p;
            uint pid = (uint)(*(nint*)(p + 0x50));
            if (pid != 0)
            {
                ushort nameLen = *(ushort*)(p + 0x38);
                char* nameBuf = *(char**)(p + 0x40);
                string name = nameBuf != null && nameLen > 0
                    ? Intern(new string(nameBuf, 0, nameLen / 2))
                    : (pid == 4 ? "System" : "Unknown");

                list.Add(new Entry
                {
                    Pid = pid,
                    Threads = (int)*(uint*)(p + 0x04),
                    PrivateWorkingSet = *(long*)(p + 0x08),
                    CreateTime = *(long*)(p + 0x20),
                    CpuTime100ns = *(long*)(p + 0x28) + *(long*)(p + 0x30),
                    Name = name,
                    Handles = (int)*(uint*)(p + 0x60),
                    BasePriority = *(int*)(p + 0x48),
                    ParentPid = (uint)(*(nint*)(p + 0x58)),
                    SessionId = *(uint*)(p + 0x64),
                    WorkingSet = (long)*(nuint*)(p + 0x90),
                    Commit = (long)*(nuint*)(p + 0xC8),
                    ReadBytes = *(long*)(p + 0xE8),
                    WriteBytes = *(long*)(p + 0xF0),
                    OtherBytes = *(long*)(p + 0xF8),
                });
            }
            if (next == 0) break;
            p += next;
        }
        return list;
    }

    string Intern(string s)
    {
        if (_nameIntern.TryGetValue(s, out var v)) return v;
        if (_nameIntern.Count > 5000) _nameIntern.Clear();
        _nameIntern[s] = s;
        return s;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }
}
