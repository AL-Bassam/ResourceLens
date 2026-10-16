using System.Diagnostics;
using System.Text;

namespace ResourceLens;

/// <summary>`ResourceLens.exe --selftest` writes a diagnostic report comparing the native readers with .NET APIs.</summary>
internal static class SelfTest
{
    /// <summary>`--bench`: average cost of each sampling component (ms and allocated KB per call).</summary>
    public static int Bench()
    {
        var sb = new StringBuilder();
        void Measure(string name, Action a, int n = 40)
        {
            a(); // warm up
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) a();
            sw.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            sb.AppendLine($"{name,-28} wall {sw.Elapsed.TotalMilliseconds / n,7:0.00} ms   cpu {cpu.TotalMilliseconds / n,7:0.00} ms   alloc {alloc / 1024.0 / n,8:0.0} KB");
        }
        try
        {
            using var scanner = new ProcessScanner();
            Measure("process scan", () => scanner.Scan());
            unsafe
            {
                var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(4 << 20);
                Measure("  raw NtQuery only", () => Native.NtQuerySystemInformation(Native.SystemProcessInformation, (void*)buf, 4 << 20, out _));
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            }
            Measure("GlobalMemoryStatusEx only", () => { var ms = new Native.MEMORYSTATUSEX { dwLength = 64 }; Native.GlobalMemoryStatusEx(ref ms); });
            Measure("GetPerformanceInfo only", () => Native.GetPerformanceInfo(out _, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()));
            Measure("system times+memory", () =>
            {
                Native.GetSystemTimes(out _, out _, out _);
                var ms = new Native.MEMORYSTATUSEX { dwLength = 64 };
                Native.GlobalMemoryStatusEx(ref ms);
                Native.GetPerformanceInfo(out _, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
            });
            using (var disk = new PdhCounters(true, false)) { Thread.Sleep(200); Measure("pdh disk", () => disk.Collect()); }
            using (var gpu = new PdhCounters(false, true)) { Thread.Sleep(200); Measure("pdh gpu engines", () => gpu.Collect(), 20); }
            Measure("NetworkInterface (managed)", () =>
            {
                long rx = 0;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up) rx += ni.GetIPStatistics().BytesReceived;
            }, 20);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "resourcelens-bench.txt"), sb.ToString());
        return 0;
    }

    /// <summary>`--probe-sensors`: which temperature/clock/fan sources answer on this PC, with raw values.</summary>
    public static int ProbeSensors()
    {
        var sb = new StringBuilder();
        try
        {
            var sw = Stopwatch.StartNew();
            using var s = new Sensors();
            sb.AppendLine($"init {sw.ElapsedMilliseconds} ms, admin={Native.IsUserAnAdmin()}, sources: {s.Sources}");
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1000);
                sw.Restart();
                var r = s.Read();
                sb.AppendLine($"read {i}: {sw.Elapsed.TotalMilliseconds:0.0} ms");
                sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(r, opts));
            }
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(s.Describe(), opts));
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "resourcelens-sensors.txt"), sb.ToString());
        return 0;
    }

    /// <summary>`--selftest-admin` (run elevated): per-process network via ETW + elevated autostart task, then cleans up.</summary>
    public static int RunAdmin()
    {
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            sb.AppendLine($"admin: {Native.IsUserAnAdmin()}");
            using (var etw = NetworkEtw.TryStart())
            {
                sb.AppendLine($"etw running: {etw.IsRunning} {etw.Error}");
                ok &= etw.IsRunning;
                if (etw.IsRunning)
                {
                    long total = 0;
                    var byPid = new Dictionary<uint, (long rx, long tx)>();
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(1000);
                        foreach (var (pid, v) in etw.TakeDeltas())
                        {
                            var cur = byPid.GetValueOrDefault(pid);
                            byPid[pid] = (cur.rx + v.rx, cur.tx + v.tx);
                            total += v.rx + v.tx;
                        }
                    }
                    sb.AppendLine($"etw: {byPid.Count} processes with traffic, {total / 1024.0:0} KB in 10 s");
                    foreach (var (pid, v) in byPid.OrderByDescending(x => x.Value.rx + x.Value.tx).Take(6))
                    {
                        string name = "?";
                        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
                        sb.AppendLine($"   pid {pid,6} {name,-24} down {v.rx / 1024.0,9:0.0} KB  up {v.tx / 1024.0,9:0.0} KB");
                    }
                    ok &= total > 0;
                }
            }
            bool existedBefore = Autostart.TaskExists();
            bool reg = Autostart.RegisterTask();
            bool exists = Autostart.TaskExists();
            sb.AppendLine($"scheduled task: existed before={existedBefore}, register={reg}, exists after={exists}");
            ok &= reg && exists;
            if (!existedBefore)
            {
                bool del = Autostart.UnregisterTask();
                sb.AppendLine($"scheduled task cleanup: removed={del}, exists now={Autostart.TaskExists()}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); ok = false; }
        sb.AppendLine(ok ? "RESULT OK" : "RESULT FAIL");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "resourcelens-selftest-admin.txt"), sb.ToString());
        return ok ? 0 : 1;
    }

    public static int Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            using var scanner = new ProcessScanner();
            var sw = Stopwatch.StartNew();
            var list = scanner.Scan();
            sb.AppendLine($"scan: {list.Count} processes in {sw.Elapsed.TotalMilliseconds:0.0} ms");

            using var me = Process.GetCurrentProcess();
            var mine = list.FirstOrDefault(e => e.Pid == (uint)me.Id);
            sb.AppendLine($"self name: {mine.Name} (expected {me.ProcessName}.exe)");
            sb.AppendLine($"self ws: {mine.WorkingSet} vs {me.WorkingSet64}");
            sb.AppendLine($"self private bytes: {mine.Commit} vs {me.PrivateMemorySize64}");
            sb.AppendLine($"self cpu100ns: {mine.CpuTime100ns} vs {me.TotalProcessorTime.Ticks}");
            sb.AppendLine($"self threads: {mine.Threads} vs {me.Threads.Count}; handles {mine.Handles} vs {me.HandleCount}");
            sb.AppendLine($"self privateWS: {mine.PrivateWorkingSet}");
            ok &= string.Equals(mine.Name, me.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase);
            ok &= Math.Abs(mine.Commit - me.PrivateMemorySize64) < 64 << 20;
            ok &= mine.PrivateWorkingSet > 0 && mine.PrivateWorkingSet <= mine.WorkingSet;

            foreach (var e in list.OrderByDescending(e => e.PrivateWorkingSet).Take(8))
            {
                string? path = Native.GetProcessPath(e.Pid);
                string? descr = path is null ? null : FileVersionInfo.GetVersionInfo(path).FileDescription;
                sb.AppendLine($"  {e.Name,-32} pid {e.Pid,6}  mem {e.PrivateWorkingSet / 1048576.0,8:0.0} MB  io r/w {e.ReadBytes / 1048576.0:0}/{e.WriteBytes / 1048576.0:0} MB  path={path ?? "err " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()} descr={descr}");
            }

            using var pdh = new PdhCounters(true, true);
            Thread.Sleep(1000);
            var r = pdh.Collect();
            sb.AppendLine($"pdh disk read {r.DiskReadBps:0} B/s write {r.DiskWriteBps:0} B/s, gpu {r.GpuTotal:0.0}% ({r.GpuByPid?.Count} pids)");

            sb.AppendLine($"admin: {Native.IsUserAnAdmin()}");
            using var etw = NetworkEtw.TryStart();
            sb.AppendLine($"etw running: {etw.IsRunning} {etw.Error}");
            if (etw.IsRunning)
            {
                Thread.Sleep(3000);
                var d = etw.TakeDeltas();
                sb.AppendLine($"etw pids with traffic: {d.Count}, bytes {d.Values.Sum(v => v.rx + v.tx)}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); ok = false; }
        sb.AppendLine(ok ? "RESULT OK" : "RESULT FAIL");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "resourcelens-selftest.txt"), sb.ToString());
        return ok ? 0 : 1;
    }
}
