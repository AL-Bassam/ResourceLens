using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ResourceLens;

/// <summary>Static hardware facts shown on the Performance page (what Task Manager shows under each device).</summary>
internal static unsafe class HardwareInfo
{
    // ------------------------------------------------------------------ CPU

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relation, byte* buffer, ref uint length);

    [DllImport("kernel32.dll")]
    static extern bool IsProcessorFeaturePresent(int feature);

    public sealed record CpuInfo(string? Name, int Sockets, int Cores, int Logical, long L1, long L2, long L3, double? BaseMhz, bool? Virtualization);

    static CpuInfo? _cpu;

    public static CpuInfo Cpu()
    {
        if (_cpu is not null) return _cpu;
        int cores = 0, sockets = 0; long l1 = 0, l2 = 0, l3 = 0;
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        if (len > 0)
        {
            byte* buf = (byte*)NativeMemory.Alloc(len);
            try
            {
                if (GetLogicalProcessorInformationEx(0xFFFF, buf, ref len))
                {
                    for (uint off = 0; off < len;)
                    {
                        int rel = *(int*)(buf + off);
                        uint size = *(uint*)(buf + off + 4);
                        if (rel == 0) cores++;               // RelationProcessorCore
                        else if (rel == 3) sockets++;        // RelationProcessorPackage
                        else if (rel == 2)                   // RelationCache: Level byte at +8, CacheSize at +12
                        {
                            byte level = *(buf + off + 8);
                            uint cs = *(uint*)(buf + off + 12);
                            if (level == 1) l1 += cs; else if (level == 2) l2 += cs; else if (level == 3) l3 += cs;
                        }
                        off += size;
                        if (size == 0) break;
                    }
                }
            }
            finally { NativeMemory.Free(buf); }
        }
        string? name = null; double? mhz = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (k?.GetValue("ProcessorNameString") as string)?.Trim();
            if (k?.GetValue("~MHz") is int m) mhz = m;
        }
        catch { }
        bool? virt = null;
        try { virt = IsProcessorFeaturePresent(21); } catch { } // PF_VIRT_FIRMWARE_ENABLED
        return _cpu = new CpuInfo(name, Math.Max(1, sockets), cores, Environment.ProcessorCount, l1, l2, l3, mhz, virt);
    }

    static bool? _lenovo;

    /// <summary>Lenovo hardware: its fan sensors need the app to run as administrator.</summary>
    public static bool IsLenovo => _lenovo ??= ReadLenovo();

    static bool ReadLenovo()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (k?.GetValue("SystemManufacturer") as string)?.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Memory (SMBIOS type 17)

    [DllImport("kernel32.dll")]
    static extern uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);

    [DllImport("kernel32.dll")]
    static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

    public sealed record MemoryInfo(long InstalledBytes, int? SpeedMts, string? Type, string? FormFactor, int SlotsUsed, int SlotsTotal);

    static MemoryInfo? _mem;

    public static MemoryInfo Memory()
    {
        if (_mem is not null) return _mem;
        long installed = GetPhysicallyInstalledSystemMemory(out ulong kb) ? (long)kb * 1024 : 0;
        int? speed = null; string? type = null, form = null; int used = 0, total = 0;
        try
        {
            const uint RSMB = 0x52534D42;
            uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
            if (size > 0)
            {
                byte* buf = (byte*)NativeMemory.Alloc(size);
                try
                {
                    GetSystemFirmwareTable(RSMB, 0, buf, size);
                    uint tableLen = *(uint*)(buf + 4);
                    byte* p = buf + 8, end = buf + 8 + tableLen;
                    while (p + 4 <= end)
                    {
                        byte t = p[0], hl = p[1];
                        if (t == 127) break;
                        if (t == 17 && hl >= 0x17)
                        {
                            total++;
                            ushort sizeMb = *(ushort*)(p + 0x0C);
                            if (sizeMb != 0 && sizeMb != 0xFFFF)
                            {
                                used++;
                                form ??= FormFactor(p[0x0E]);
                                type ??= MemType(p[0x12]);
                                ushort sp = hl >= 0x22 ? *(ushort*)(p + 0x20) : (ushort)0; // configured speed
                                if (sp == 0) sp = *(ushort*)(p + 0x15);
                                if (sp is > 0 and < 0xFFFF) speed = Math.Max(speed ?? 0, sp);
                            }
                        }
                        // skip formatted area + string set (terminated by double NUL)
                        byte* s = p + hl;
                        while (s + 1 < end && !(s[0] == 0 && s[1] == 0)) s++;
                        p = s + 2;
                    }
                }
                finally { NativeMemory.Free(buf); }
            }
        }
        catch { }
        return _mem = new MemoryInfo(installed, speed, type, form, used, total);
    }

    static string? FormFactor(byte f) => f switch { 9 => "DIMM", 0x0D => "SODIMM", 0x0B => "Row of chips", 0x0F => "FB-DIMM", 0x10 => "Die", _ => null };

    static string? MemType(byte t) => t switch
    {
        0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4",
        0x22 => "DDR5", 0x23 => "LPDDR5", 0x24 => "HBM3", _ => null,
    };

    // ------------------------------------------------------------------ Disks

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    public sealed record DiskInfo(int Number, string? Model, bool? Ssd, string? Bus, long Size, List<string> Volumes);

    static List<DiskInfo>? _disks;
    static DateTime _disksAt;

    public static List<DiskInfo> Disks()
    {
        if (_disks is not null && (DateTime.UtcNow - _disksAt).TotalMinutes < 5) return _disks;
        var volumes = new Dictionary<int, List<string>>();
        byte* buf = stackalloc byte[256];
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
            try
            {
                using var h = CreateFileW(@"\\.\" + d.Name.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                if (DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 256, out _, IntPtr.Zero))
                {
                    int n = *(int*)buf;
                    for (int i = 0; i < n; i++)
                    {
                        int disk = *(int*)(buf + 8 + i * 24);
                        if (!volumes.TryGetValue(disk, out var l)) volumes[disk] = l = new();
                        l.Add(d.Name.TrimEnd('\\'));
                    }
                }
            }
            catch { }
        }
        var list = new List<DiskInfo>();
        byte* q = stackalloc byte[12];
        byte* outBuf = stackalloc byte[1024];
        for (int i = 0; i < 16; i++)
        {
            using var h = CreateFileW($@"\\.\PhysicalDrive{i}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            string? model = null, bus = null; bool? ssd = null; long size = 0;
            // StorageDeviceProperty (0) / PropertyStandardQuery (0)
            *(int*)q = 0; *(int*)(q + 4) = 0;
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, 12, outBuf, 1024, out _, IntPtr.Zero))
            {
                int vendorOff = *(int*)(outBuf + 12), productOff = *(int*)(outBuf + 16);
                int busType = *(int*)(outBuf + 28);
                string? vendor = vendorOff > 0 ? Marshal.PtrToStringAnsi((IntPtr)(outBuf + vendorOff))?.Trim() : null;
                string? product = productOff > 0 ? Marshal.PtrToStringAnsi((IntPtr)(outBuf + productOff))?.Trim() : null;
                model = string.Join(' ', new[] { vendor, product }.Where(x => !string.IsNullOrWhiteSpace(x)));
                bus = busType switch { 7 => "USB", 11 => "SATA", 17 => "NVMe", 3 => "ATA", 10 => "SAS", 12 => "SD", 13 => "MMC", 15 => "Virtual", _ => null };
            }
            // StorageDeviceSeekPenaltyProperty (7): IncursSeekPenalty at +8
            *(int*)q = 7; *(int*)(q + 4) = 0;
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, 12, outBuf, 64, out _, IntPtr.Zero))
                ssd = *(outBuf + 8) == 0;
            // DRIVE_GEOMETRY_EX works without read access: DiskSize follows the 24-byte DISK_GEOMETRY
            if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, outBuf, 1024, out _, IntPtr.Zero))
                size = *(long*)(outBuf + 24);
            list.Add(new DiskInfo(i, string.IsNullOrWhiteSpace(model) ? null : model, ssd, bus, size, volumes.GetValueOrDefault(i) ?? new()));
        }
        _disksAt = DateTime.UtcNow;
        return _disks = list;
    }

    // ------------------------------------------------------------------ Network adapters

    public sealed record NetInfo(string Name, string Description, string Type, long SpeedBps, string? IPv4, string? IPv6, bool Up);

    static List<NetInfo>? _nets;
    static DateTime _netsAt;

    public static List<NetInfo> Networks()
    {
        if (_nets is not null && (DateTime.UtcNow - _netsAt).TotalSeconds < 30) return _nets;
        var list = new List<NetInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up && ni.OperationalStatus != OperationalStatus.Dormant) continue;
                // Skip the filter-driver pseudo interfaces ("…-QoS Packet Scheduler-0000") and WAN miniports.
                string desc = ni.Description;
                if (ni.Speed <= 0 || desc.Contains("Miniport", StringComparison.OrdinalIgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(desc, @"-\d{4}$") ||
                    desc.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)) continue;
                var ip = ni.GetIPProperties();
                string? v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                string? v6 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)?.Address.ToString();
                string type = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    NetworkInterfaceType.Ppp => "PPP",
                    _ => ni.NetworkInterfaceType.ToString(),
                };
                list.Add(new NetInfo(ni.Name, ni.Description, type, ni.Speed, v4, v6, ni.OperationalStatus == OperationalStatus.Up));
            }
        }
        catch { }
        _netsAt = DateTime.UtcNow;
        return _nets = list;
    }

    /// <summary>PDH instance names mangle adapter descriptions: "(" -> "[", ")" -> "]", "#" -> "_", "/" -> "_".</summary>
    public static string PdhName(string description) =>
        description.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_');

    // ------------------------------------------------------------------ GPU

    /// <param name="Luid">Matches the GPU performance counters ("0x00000000_0x0000C3B1"); null when Windows did not say.</param>
    public sealed record GpuInfo(string Name, long? DedicatedBytes, long SharedBytes, string? Driver, string? DriverDate, string? Luid);

    static readonly object GpuGate = new();
    static List<GpuInfo>? _gpus;
    static HashSet<string> _npus = new();
    static DateTime _gpusAt;

    /// <summary>
    /// The graphics adapters present now, in Windows' order (Task Manager's GPU 0, GPU 1…). Driver details come from
    /// the display class key, which also keeps entries for removed cards, so it is only used to describe live adapters.
    /// </summary>
    public static List<GpuInfo> Gpus()
    {
        lock (GpuGate)
        {
            if (_gpus is not null && (DateTime.UtcNow - _gpusAt).TotalSeconds < 60) return _gpus;
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref ms);
            long shared = (long)(ms.ullTotalPhys / 2);
            var drivers = DisplayDrivers();
            var list = new List<GpuInfo>();
            var npus = new HashSet<string>();
            try
            {
                foreach (var a in Sensors.GpuAdapter.Enumerate())
                    using (a)
                    {
                        if (a.IsNpu) npus.Add(a.Luid);
                        if (!a.IsGpu) continue;
                        // a reinstalled driver can leave an older entry with the same name: take the newest
                        var d = drivers.Where(x => a.Name is not null && x.Names.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                            .OrderByDescending(x => System.Version.TryParse(x.Version, out var v) ? v : new System.Version()).FirstOrDefault();
                        list.Add(new GpuInfo(a.Name ?? d?.Names[0] ?? "GPU", d?.Vram, shared, d?.Version, d?.Date, a.Luid));
                    }
            }
            catch (Exception ex) { Log.Write("gpus", ex); }
            // Without adapter enumeration (very old drivers), fall back to the class key as it is.
            if (list.Count == 0) list = drivers.Select(d => new GpuInfo(d.Names[0], d.Vram, shared, d.Version, d.Date, null)).ToList();
            _npus = npus;
            _gpusAt = DateTime.UtcNow;
            return _gpus = list;
        }
    }

    /// <summary>NPU engines appear among the GPU counters; they are left out of GPU utilisation.</summary>
    public static bool IsNpu(string luid)
    {
        if (_gpus is null) Gpus();
        return _npus.Count > 0 && _npus.Contains(luid);
    }

    sealed record DisplayDriver(string[] Names, long? Vram, string? Version, string? Date);

    static List<DisplayDriver> DisplayDrivers()
    {
        var list = new List<DisplayDriver>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? System.Array.Empty<string>())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls!.OpenSubKey(sub);
                string? name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                // The adapter name Windows reports can be the driver's own string (REG_BINARY UTF-16 in older drivers).
                string? adapterString = k?.GetValue("HardwareInformation.AdapterString") switch
                {
                    string s => s,
                    byte[] b => System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0'),
                    _ => null,
                };
                long? vram = k?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                    int i => (uint)i,
                    _ => null,
                };
                if (vram is null && k?.GetValue("HardwareInformation.MemorySize") is { } m)
                    vram = m switch { int i => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b), _ => null };
                var names = string.IsNullOrWhiteSpace(adapterString) ? new[] { name.Trim() } : new[] { name.Trim(), adapterString.Trim() };
                list.Add(new DisplayDriver(names, vram, k?.GetValue("DriverVersion") as string, k?.GetValue("DriverDate") as string));
            }
        }
        catch { }
        return list;
    }
}
