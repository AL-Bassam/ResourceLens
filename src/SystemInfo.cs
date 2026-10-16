using Microsoft.Win32;

namespace ResourceLens;

/// <summary>Hardware / OS description included in exported reports (read from the registry, no WMI).</summary>
internal static class SystemInfo
{
    public static object Describe()
    {
        string? Reg(RegistryKey root, string path, string name)
        {
            try { using var k = root.OpenSubKey(path); return k?.GetValue(name)?.ToString()?.Trim(); } catch { return null; }
        }

        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? product = Reg(Registry.LocalMachine, nt, "ProductName");
        string? build = Reg(Registry.LocalMachine, nt, "CurrentBuild");
        if (product is not null && int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string os = $"{product} {Reg(Registry.LocalMachine, nt, "DisplayVersion")} (build {build}.{Reg(Registry.LocalMachine, nt, "UBR")})";

        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref ms);

        var gpus = new List<object>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls!.OpenSubKey(sub);
                string? name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                long? vram = k?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes),
                    byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes),
                    int i => (uint)i,
                    _ => null,
                };
                if (vram is null && k?.GetValue("HardwareInformation.MemorySize") is { } mem)
                    vram = mem switch { int i => (uint)i, byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes), _ => null };
                gpus.Add(new { name, dedicated_vram_mb = vram is > 0 ? vram / 1048576 : null, driver = k?.GetValue("DriverVersion") as string });
            }
        }
        catch { }

        object? disk = null;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            disk = new { drive = d.Name, total_gb = Math.Round(d.TotalSize / 1073741824.0, 1), free_gb = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1) };
        }
        catch { }

        const string bios = @"HARDWARE\DESCRIPTION\System\BIOS";
        return new
        {
            device = $"{Reg(Registry.LocalMachine, bios, "SystemManufacturer")} {Reg(Registry.LocalMachine, bios, "SystemProductName")}".Trim(),
            os,
            cpu = Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"),
            logical_processors = Environment.ProcessorCount,
            ram_total_mb = (long)(ms.ullTotalPhys / 1048576),
            commit_limit_mb = (long)(ms.ullTotalPageFile / 1048576),
            gpus,
            system_disk = disk,
        };
    }
}
