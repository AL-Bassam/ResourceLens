using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// TCP / UDP endpoint tables with the owning PID (iphlpapi GetExtendedTcpTable / GetExtendedUdpTable).
/// Works without administrator rights; one call returns every endpoint of one address family.
/// </summary>
internal static unsafe class ModNetTables
{
    const int AF_INET = 2, AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const int Listen = 2, Established = 5;

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(void* table, ref int size, bool order, int af, int cls, uint reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(void* table, ref int size, bool order, int af, int cls, uint reserved);

    /// <summary>Native buffer reused across calls (the per-sample snapshot must not allocate every 5 s).</summary>
    public sealed class Buffer : IDisposable
    {
        public IntPtr Ptr;
        public int Size;

        public void Grow(int size)
        {
            if (size <= Size) return;
            if (Ptr != IntPtr.Zero) Marshal.FreeHGlobal(Ptr);
            Size = (size + 4095) & ~4095;
            Ptr = Marshal.AllocHGlobal(Size);
        }

        public void Dispose()
        {
            if (Ptr != IntPtr.Zero) Marshal.FreeHGlobal(Ptr);
            Ptr = IntPtr.Zero; Size = 0;
        }
    }

    public sealed record Conn(string Proto, IPAddress LocalIp, int LocalPort, IPAddress? RemoteIp, int RemotePort, int State, uint Pid);

    static bool Fill(Buffer b, bool udp, int af, int cls)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int size = b.Size;
            uint r = udp ? GetExtendedUdpTable((void*)b.Ptr, ref size, false, af, cls, 0)
                         : GetExtendedTcpTable((void*)b.Ptr, ref size, false, af, cls, 0);
            if (r == 0) return b.Ptr != IntPtr.Zero;
            if (r != ERROR_INSUFFICIENT_BUFFER) return false;
            b.Grow(size + 2048); // the table can grow between the two calls
        }
        return false;
    }

    static int Port(uint dw) => (int)(((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF)); // network byte order in the low word
    static bool Loop4(uint addr) => (addr & 0xFF) == 127;                   // first octet (stored in network order)

    static bool Loop6(byte* a)
    {
        ulong hi = *(ulong*)a, lo = *(ulong*)(a + 8);
        if (hi == 0 && lo == 0x0100000000000000UL) return true;            // ::1
        return hi == 0 && (lo & 0xFFFFFFFF) == 0xFFFF0000 && a[12] == 127;  // ::ffff:127.x.x.x
    }

    /// <summary>
    /// PIDs owning an established TCP connection to a non-loopback address (IPv4 + IPv6): the cheap "who is online"
    /// test used when per-process byte counts (ETW, admin only) are not available.
    /// </summary>
    public static void EstablishedOwners(Buffer b, HashSet<uint> pids)
    {
        if (Fill(b, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                uint* row = (uint*)(p + 4 + i * 24); // state, localAddr, localPort, remoteAddr, remotePort, pid
                if (row[0] == Established && !Loop4(row[3]) && row[5] > 4) pids.Add(row[5]);
            }
        }
        if (Fill(b, false, AF_INET6, TCP_TABLE_OWNER_PID_CONNECTIONS))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                byte* row = p + 4 + i * 56; // local[16], scope, port, remote[16], scope, port, state, pid
                uint pid = *(uint*)(row + 52);
                if (*(uint*)(row + 48) == Established && !Loop6(row + 24) && pid > 4) pids.Add(pid);
            }
        }
    }

    static IPAddress V6(byte* a, uint scope)
    {
        var ip = new IPAddress(new ReadOnlySpan<byte>(a, 16), scope);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    /// <summary>Every TCP endpoint (connections and listeners) and every UDP listener, IPv4 and IPv6.</summary>
    public static List<Conn> Snapshot(Buffer b)
    {
        var list = new List<Conn>(256);
        if (Fill(b, false, AF_INET, TCP_TABLE_OWNER_PID_ALL))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                uint* r = (uint*)(p + 4 + i * 24);
                int state = (int)r[0];
                list.Add(new Conn("TCP", new IPAddress(r[1]), Port(r[2]), state == Listen ? null : new IPAddress(r[3]), Port(r[4]), state, r[5]));
            }
        }
        if (Fill(b, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                byte* r = p + 4 + i * 56;
                int state = *(int*)(r + 48);
                list.Add(new Conn("TCPv6", V6(r, *(uint*)(r + 16)), Port(*(uint*)(r + 20)),
                    state == Listen ? null : V6(r + 24, *(uint*)(r + 40)), Port(*(uint*)(r + 44)), state, *(uint*)(r + 52)));
            }
        }
        if (Fill(b, true, AF_INET, UDP_TABLE_OWNER_PID))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                uint* r = (uint*)(p + 4 + i * 12); // localAddr, localPort, pid
                list.Add(new Conn("UDP", new IPAddress(r[0]), Port(r[1]), null, 0, 0, r[2]));
            }
        }
        if (Fill(b, true, AF_INET6, UDP_TABLE_OWNER_PID))
        {
            byte* p = (byte*)b.Ptr;
            int n = *(int*)p;
            for (int i = 0; i < n; i++)
            {
                byte* r = p + 4 + i * 28; // local[16], scope, port, pid
                list.Add(new Conn("UDPv6", V6(r, *(uint*)(r + 16)), Port(*(uint*)(r + 20)), null, 0, 0, *(uint*)(r + 24)));
            }
        }
        return list;
    }

    public static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip);

    public static bool IsUnspecified(IPAddress ip) => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);

    public static string Endpoint(IPAddress ip, int port) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";

    public static string StateName(int s) => s switch
    {
        1 => "closed", 2 => "listen", 3 => "syn-sent", 4 => "syn-received", 5 => "established", 6 => "fin-wait-1",
        7 => "fin-wait-2", 8 => "close-wait", 9 => "closing", 10 => "last-ack", 11 => "time-wait", 12 => "delete-tcb",
        _ => "",
    };
}

/// <summary>
/// Reverse DNS for remote addresses, resolved in the background (a few pool threads at most) and cached with a size
/// cap. Lookups never block an API call: an address shows as an IP until its name arrives.
/// </summary>
internal sealed unsafe class ModNetDns : IDisposable
{
    const int MaxEntries = 2048, MaxQueue = 256, MaxWorkers = 3;
    const int NI_NAMEREQD = 0x04;

    [DllImport("ws2_32.dll")]
    static extern int WSAStartup(ushort version, byte* data);

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode)]
    static extern int GetNameInfoW(byte* sa, int saLen, char* host, int hostLen, char* serv, int servLen, int flags);

    // getnameinfo directly: Dns.GetHostEntry(IPAddress) would also forward-resolve every name it finds.
    static readonly Lazy<bool> Winsock = new(() => { byte* d = stackalloc byte[512]; return WSAStartup(0x0202, d) == 0; });

    readonly Dictionary<IPAddress, string> _cache = new(); // "" = the address has no name
    readonly Queue<IPAddress> _queue = new();
    readonly HashSet<IPAddress> _queued = new();
    int _workers;
    volatile bool _disposed;

    /// <summary>Address worth a lookup: not loopback, unspecified, link-local or multicast.</summary>
    public static bool Resolvable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ModNetTables.IsUnspecified(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast;
        Span<byte> b = stackalloc byte[4];
        ip.TryWriteBytes(b, out _);
        return b[0] < 224 && !(b[0] == 169 && b[1] == 254);
    }

    /// <summary>The cached host name, or null (then queued for a background lookup).</summary>
    public string? Get(IPAddress ip)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(ip, out var h)) return h.Length > 0 ? h : null;
            if (_disposed || _queued.Count >= MaxQueue || !_queued.Add(ip)) return null;
            _queue.Enqueue(ip);
            if (_workers < MaxWorkers)
            {
                _workers++;
                ThreadPool.UnsafeQueueUserWorkItem(_ => Work(), null);
            }
        }
        return null;
    }

    void Work()
    {
        while (true)
        {
            IPAddress ip;
            lock (_cache)
            {
                if (_disposed || _queue.Count == 0) { _workers--; return; }
                ip = _queue.Dequeue();
            }
            string host = "";
            try { host = Resolve(ip) ?? ""; } catch { }
            lock (_cache)
            {
                _queued.Remove(ip);
                if (_cache.Count >= MaxEntries) _cache.Clear();
                _cache[ip] = host;
            }
        }
    }

    static string? Resolve(IPAddress ip)
    {
        if (!Winsock.Value) return null;
        byte* sa = stackalloc byte[28];
        new Span<byte>(sa, 28).Clear();
        int len;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            *(ushort*)sa = 2;                                     // sockaddr_in: family, port, addr
            ip.TryWriteBytes(new Span<byte>(sa + 4, 4), out _);
            len = 16;
        }
        else
        {
            *(ushort*)sa = 23;                                    // sockaddr_in6: family, port, flowinfo, addr, scope
            ip.TryWriteBytes(new Span<byte>(sa + 8, 16), out _);
            *(uint*)(sa + 24) = (uint)ip.ScopeId;
            len = 28;
        }
        char* host = stackalloc char[1025];
        if (GetNameInfoW(sa, len, host, 1025, null, 0, NI_NAMEREQD) != 0) return null;
        string name = new(host);
        return name.Length == 0 || IPAddress.TryParse(name, out _) ? null : name;
    }

    public void Dispose()
    {
        lock (_cache) { _disposed = true; _queue.Clear(); _queued.Clear(); }
    }
}
