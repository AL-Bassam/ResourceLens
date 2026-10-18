using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>Temperatures, clocks, fans and GPU memory gathered from every source available on this PC.</summary>
public sealed class SensorReading
{
    public double? CpuTemp { get; set; }        // °C
    public string? CpuTempSource { get; set; }
    public double? CpuMhz { get; set; }
    public double? CpuBaseMhz { get; set; }
    public double? CpuPower { get; set; }       // W
    public double? PackagePower { get; set; }   // W (whole APU / GPU board)
    public double? GpuTemp { get; set; }        // °C
    public double? GpuHotspot { get; set; }     // °C
    public string? GpuTempSource { get; set; }
    public double? GpuMhz { get; set; }
    public double? GpuMemMhz { get; set; }
    public double? GpuPower { get; set; }       // W
    public double? GpuDedicatedUsed { get; set; } // bytes
    public double? GpuSharedUsed { get; set; }    // bytes
    public int? Fan1Rpm { get; set; }
    public int? Fan2Rpm { get; set; }
    public double? SocTemp { get; set; }
    public Dictionary<string, double>? ThermalZones { get; set; }
    /// <summary>Every graphics adapter on its own; the Gpu* values above are the main one's (the discrete GPU if any).</summary>
    public List<GpuSensor>? Gpus { get; set; }
}

public sealed class GpuSensor
{
    public string Luid { get; set; } = "";
    public double? Temp { get; set; }
    public string? TempSource { get; set; }
    public double? Hotspot { get; set; }
    public double? CoreMhz { get; set; }
    public double? MemMhz { get; set; }
    public double? Power { get; set; }
    public int? FanRpm { get; set; }
    public double? DedicatedUsed { get; set; }
    public double? SharedUsed { get; set; }
}

/// <summary>
/// Sources, in order of preference:
///  * AMD ADL "PMLog" (atiadlxx.dll): APU/GPU temperatures, clocks, power — no admin needed.
///  * D3DKMT adapter/node perf data (what Task Manager uses): GPU temperature, fan, clocks — no admin.
///  * Lenovo GameZone WMI (Legion devices): real CPU/GPU temperature and fan RPM — needs admin.
///  * ACPI thermal zones (PDH): rough CPU/board temperature — no admin.
///  * PDH processor performance: current CPU clock.
/// </summary>
internal sealed unsafe class Sensors : IDisposable
{
    IntPtr _query, _freq, _perf, _tz, _gpuDed, _gpuShared;
    readonly Adl? _adl;
    readonly Lenovo? _lenovo;
    readonly List<GpuAdapter> _adapters = new();
    readonly HashSet<string> _integrated = new();
    /// <summary>The adapter the AMD library describes (null when it cannot be told apart from another AMD GPU).</summary>
    readonly string? _adlLuid;
    readonly bool _adlIsApu;

    // Some boards report a thermal zone that never changes (a fixed 27.8 °C, say). A zone that has not moved
    // after the first minutes is left out; it comes back as soon as it changes.
    readonly Dictionary<string, double> _zoneFirst = new();
    readonly HashSet<string> _zoneMoved = new();
    readonly long _startedAt = Environment.TickCount64;
    const int ZoneSettleMs = 5 * 60_000;

    public string Sources { get; }

    public Sensors()
    {
        var src = new List<string>();
#if DEBUG
        // Simulates other hardware: "basic" = no vendor libraries (a typical Intel/NVIDIA PC), "none" = no sensors (a VM).
        string test = Environment.GetEnvironmentVariable("RESOURCELENS_TEST_SENSORS") ?? "";
        if (test == "none") { Sources = "none (test)"; return; }
        bool vendor = test != "basic";
#else
        const bool vendor = true;
#endif
        SensorHub.Step = "setup: pdh";
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0)
        {
            Add(@"\Processor Information(_Total)\Processor Frequency", out _freq);
            Add(@"\Processor Information(_Total)\% Processor Performance", out _perf);
            Add(@"\Thermal Zone Information(*)\High Precision Temperature", out _tz);
            Add(@"\GPU Adapter Memory(*)\Dedicated Usage", out _gpuDed);
            Add(@"\GPU Adapter Memory(*)\Shared Usage", out _gpuShared);
            Native.PdhCollectQueryData(_query);
            src.Add("pdh");
        }
        SensorHub.Step = "setup: amd-adl";
        try { _adl = vendor ? Adl.TryCreate() : null; if (_adl is not null) src.Add("amd-adl"); } catch (Exception ex) { Log.Write("adl", ex); }
        SensorHub.Step = "setup: d3dkmt";
        var gpus = new List<GpuAdapter>();
        try
        {
            foreach (var a in GpuAdapter.Enumerate())
            {
                if (a.IsGpu)
                {
                    gpus.Add(a);
                    if (a.Integrated) _integrated.Add(a.Luid);
                }
                if (a.IsGpu && a.HasPerf) _adapters.Add(a); else a.Dispose();
            }
            if (_adapters.Count > 0) src.Add($"d3dkmt({_adapters.Count})");
        }
        catch (Exception ex) { Log.Write("d3dkmt", ex); }
        if (_adl is not null)
        {
            // An APU's library also reports the CPU: then it describes the integrated GPU, and a discrete GPU next to it
            // (a Ryzen laptop with a GeForce) keeps its own readings.
            _adlIsApu = _adl.Read()?.ContainsKey(Adl.TEMP_CPU) == true;
            var amd = gpus.Where(a => a.Name is { } n && (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))).ToList();
            if (amd.Count > 1) amd = amd.Where(a => a.Integrated == _adlIsApu).ToList();
            if (amd.Count == 1) _adlLuid = amd[0].Luid;
            else if (gpus.Count == 1) _adlLuid = gpus[0].Luid;
            if (_adlIsApu && _adlLuid is not null) _integrated.Add(_adlLuid);
        }
        SensorHub.Step = "setup: lenovo-wmi";
        try { _lenovo = vendor ? Lenovo.TryCreate() : null; if (_lenovo is not null) src.Add("lenovo-wmi"); } catch (Exception ex) { Log.Write("lenovo", ex); }
        Sources = string.Join(", ", src);
    }

    /// <summary>"luid_0x00000000_0x0000C3B1_phys_0" (a PDH instance) -> "0x00000000_0x0000C3B1".</summary>
    internal static string? LuidOf(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.Ordinal);
        return i >= 0 && instance.Length >= i + 26 ? instance.Substring(i + 5, 21) : null;
    }

    void Add(string path, out IntPtr counter)
    {
        if (Native.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out counter) != 0) counter = IntPtr.Zero;
    }

    double? Single(IntPtr c)
    {
        if (c == IntPtr.Zero) return null;
        return Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) == 0 ? v.doubleValue : null;
    }

    List<(string, double)> Array(IntPtr c)
    {
        var res = new List<(string, double)>();
        if (c == IntPtr.Zero) return res;
        uint size = 0;
        if (Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero) != Native.PDH_MORE_DATA) return res;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return res;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)buf;
            for (int i = 0; i < count; i++)
                if (items[i].FmtValue.CStatus == 0)
                    res.Add((Marshal.PtrToStringUni(items[i].szName) ?? "", items[i].FmtValue.doubleValue));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }

    public SensorReading Read()
    {
        var r = new SensorReading();
        SensorHub.Step = "pdh";
        if (_query != IntPtr.Zero)
        {
            Native.PdhCollectQueryData(_query);
            // Task Manager's "Speed": nominal (base) clock × % Processor Performance.
            double? baseMhz = HardwareInfo.Cpu().BaseMhz ?? Single(_freq), perf = Single(_perf);
            if (baseMhz is > 0)
            {
                r.CpuBaseMhz = baseMhz;
                if (perf is > 0) r.CpuMhz = Math.Round(baseMhz.Value * perf.Value / 100);
            }
            var zones = Array(_tz).Select(z => (Name: z.Item1.Replace("\\_TZ.", "").Trim('\\'), C: Math.Round(z.Item2 / 10 - 273.15, 1)))
                .Where(z => z.C is > 5 and < 125).ToList();
            foreach (var z in zones)
                if (!_zoneFirst.TryAdd(z.Name, z.C) && Math.Abs(z.C - _zoneFirst[z.Name]) >= 0.5) _zoneMoved.Add(z.Name);
            if (Environment.TickCount64 - _startedAt > ZoneSettleMs) zones = zones.Where(z => _zoneMoved.Contains(z.Name)).ToList();
            if (zones.Count > 0)
            {
                r.ThermalZones = zones.ToDictionary(z => z.Name, z => z.C);
                r.CpuTemp = zones.Max(z => z.C);
                r.CpuTempSource = "acpi";
            }
        }

        // D3DKMT: vendor-neutral GPU telemetry (Task Manager's source), one entry per adapter
        SensorHub.Step = "d3dkmt";
        var gpus = new Dictionary<string, GpuSensor>();
        GpuSensor Gpu(string luid) => gpus.TryGetValue(luid, out var g) ? g : gpus[luid] = new GpuSensor { Luid = luid };
        foreach (var a in _adapters)
        {
            var p = a.Read();
            var g = Gpu(a.Luid);
            if (p.Temp is > 0) { g.Temp = p.Temp; g.TempSource = "d3dkmt"; }
            if (p.CoreMhz is > 0) g.CoreMhz = p.CoreMhz;
            if (p.MemMhz is > 0) g.MemMhz = p.MemMhz;
            if (p.FanRpm is > 0) g.FanRpm = p.FanRpm;
        }
        if (_query != IntPtr.Zero)
        {
            foreach (var (inst, v) in Array(_gpuDed)) if (LuidOf(inst) is { } l) Gpu(l).DedicatedUsed = v;
            foreach (var (inst, v) in Array(_gpuShared)) if (LuidOf(inst) is { } l) Gpu(l).SharedUsed = v;
        }

        // AMD ADL: richer and more accurate on Radeon / Ryzen APUs
        SensorHub.Step = "amd-adl";
        var adlGpu = _adlLuid is not null ? Gpu(_adlLuid) : _adapters.Count == 0 ? new GpuSensor() : null;
        if (_adl is not null)
        {
            var s = _adl.Read();
            if (s is not null)
            {
                double? Get(int i) => s.TryGetValue(i, out var v) ? v : null;
                if (Get(Adl.TEMP_CPU) is double ct and > 0) { r.CpuTemp = ct; r.CpuTempSource = "amd"; }
                if (Get(Adl.TEMP_SOC) is double soc and > 0) r.SocTemp = soc;
                if (Get(Adl.CLK_CPU) is double cc and > 0 && r.CpuMhz is null) r.CpuMhz = cc;
                if (Get(Adl.CPU_POWER) is double cp and > 0) r.CpuPower = cp;
                if (Get(Adl.ASIC_POWER) is double ap and > 0) r.PackagePower = ap;
                if (adlGpu is not null)
                {
                    if ((Get(Adl.TEMP_EDGE) is double edge and > 0 ? edge : Get(Adl.TEMP_GFX)) is double gt and > 0) { adlGpu.Temp = gt; adlGpu.TempSource = "amd"; }
                    if (Get(Adl.TEMP_HOTSPOT) is double hs and > 0) adlGpu.Hotspot = hs;
                    if (Get(Adl.CLK_GFX) is double gc and > 0) adlGpu.CoreMhz = gc;
                    if (Get(Adl.CLK_MEM) is double mc and > 0) adlGpu.MemMhz = mc;
                    if (Get(Adl.GFX_POWER) is double gp and > 0) adlGpu.Power = gp;
                    if (Get(Adl.FAN_RPM) is double fr and > 0) adlGpu.FanRpm ??= (int)fr;
                }
            }
        }

        // The main GPU for the history and the overlay: the discrete one when it reports a temperature (it is
        // the one games run on), otherwise any adapter that does.
        var main = gpus.Values.Where(g => g.Temp is not null).OrderBy(g => _integrated.Contains(g.Luid)).FirstOrDefault()
            ?? (adlGpu is { Temp: not null } && !gpus.ContainsValue(adlGpu) ? adlGpu : null)
            ?? gpus.Values.Where(g => g.CoreMhz is not null).OrderBy(g => _integrated.Contains(g.Luid)).FirstOrDefault();
        if (main is not null)
        {
            r.GpuTemp = main.Temp; r.GpuTempSource = main.TempSource; r.GpuHotspot = main.Hotspot;
            r.GpuMhz = main.CoreMhz; r.GpuMemMhz = main.MemMhz; r.GpuPower = main.Power;
            r.Fan1Rpm = main.FanRpm;
        }
        if (gpus.Count > 0)
        {
            r.GpuDedicatedUsed = main?.DedicatedUsed ?? gpus.Values.Max(g => g.DedicatedUsed);
            r.GpuSharedUsed = main?.SharedUsed ?? gpus.Values.Max(g => g.SharedUsed);
            r.Gpus = gpus.Values.ToList();
        }

        // Lenovo GameZone (admin): the embedded controller's own CPU/GPU sensors and fans
        SensorHub.Step = "lenovo-wmi";
        if (_lenovo is not null)
        {
            var l = _lenovo.Read();
            if (l.cpu is > 0 and < 125) { r.CpuTemp = l.cpu; r.CpuTempSource = "lenovo"; }
            if (l.gpu is > 0 and < 125 && r.GpuTempSource != "amd") { r.GpuTemp = l.gpu; r.GpuTempSource = "lenovo"; }
            if (l.fan1 is > 0) r.Fan1Rpm = l.fan1;
            if (l.fan2 is > 0) r.Fan2Rpm = l.fan2;
        }
        return r;
    }

    public Dictionary<string, object?> Describe()
    {
        var d = new Dictionary<string, object?> { ["sources"] = Sources, ["adlSensors"] = _adl?.Read()?.ToDictionary(k => k.Key.ToString(), v => (object?)v.Value) };
        d["adlAdapter"] = _adlLuid;
        d["adlIsApu"] = _adlIsApu;
        d["integrated"] = _integrated.ToList();
        d["adapters"] = _adapters.Select(a => new { a.Luid, a.Name, type = $"0x{a.Type:X}", perf = a.Read() }).ToList();
        return d;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        foreach (var a in _adapters) a.Dispose();
        _adl?.Dispose();
        _lenovo?.Dispose();
    }

    // ===================================================================== D3DKMT

    internal sealed class GpuAdapter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecise; }

        [StructLayout(LayoutKind.Sequential)]
        struct ENUMADAPTERS2 { public uint NumAdapters; public ADAPTERINFO* pAdapters; }

        [StructLayout(LayoutKind.Sequential)]
        struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public void* pData; public uint Size; }

        [StructLayout(LayoutKind.Sequential)]
        struct ADAPTER_PERFDATA
        {
            public uint PhysicalAdapterIndex;
            public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
            public uint FanRPM, Power, Temperature;
            public byte PowerStateOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NODE_PERFDATA
        {
            public uint NodeOrdinal, PhysicalAdapterIndex;
            public ulong Frequency, MaxFrequency, MaxFrequencyOC;
            public uint Voltage, VoltageMax, VoltageMaxOC;
            public ulong MaxTransitionLatency;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CLOSEADAPTER { public uint hAdapter; }

        const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8, KMTQAITYPE_ADAPTERTYPE = 15, KMTQAITYPE_ADAPTERREGISTRYINFO_RENDER = 54,
            KMTQAITYPE_ADAPTERTYPE_RENDER = 57, KMTQAITYPE_NODEPERFDATA = 61, KMTQAITYPE_ADAPTERPERFDATA = 62;

        // D3DKMT_ADAPTERTYPE bits
        const uint SoftwareDevice = 1 << 2, HybridIntegrated = 1 << 5, IndirectDisplay = 1 << 6, ComputeOnly = 1 << 11;

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ENUMADAPTERS2* p);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(QUERYADAPTERINFO* p);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(CLOSEADAPTER* p);

        uint _h;
        public string Luid { get; private set; } = "";
        public string? Name { get; private set; }
        public uint Type { get; private set; }
        /// <summary>Exposes temperature or clocks (WDDM 2.4+ drivers).</summary>
        public bool HasPerf { get; private set; }
        /// <summary>The integrated half of a hybrid (iGPU + dGPU) laptop.</summary>
        public bool Integrated => (Type & HybridIntegrated) != 0;
        /// <summary>NPUs and other compute-only accelerators: Windows lists them with the GPUs, but they are not GPUs.</summary>
        public bool IsNpu => (Type & ComputeOnly) != 0;
        /// <summary>A real graphics adapter: not the software renderer, a virtual display (remote desktop, streaming) or an NPU.</summary>
        public bool IsGpu => (Type & (SoftwareDevice | IndirectDisplay | ComputeOnly)) == 0 &&
            Name?.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) != true;

        /// <summary>Every adapter Windows has right now (the caller disposes them).</summary>
        public static List<GpuAdapter> Enumerate()
        {
            var list = new List<GpuAdapter>();
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(&e) != 0 || e.NumAdapters == 0) return list;
            var arr = new ADAPTERINFO[e.NumAdapters];
            fixed (ADAPTERINFO* p = arr)
            {
                e.pAdapters = p;
                if (D3DKMTEnumAdapters2(&e) != 0) return list;
            }
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var a = new GpuAdapter { _h = arr[i].hAdapter, Luid = $"0x{arr[i].LuidHigh:X8}_0x{arr[i].LuidLow:X8}" };
                a.Type = a.QueryType();
                a.Name = a.QueryName();
                var test = a.Read();
                a.HasPerf = test.Temp is not null || test.CoreMhz is not null || test.MemMhz is not null;
                list.Add(a);
            }
            return list;
        }

        uint QueryType()
        {
            uint v = 0;
            foreach (int type in new[] { KMTQAITYPE_ADAPTERTYPE, KMTQAITYPE_ADAPTERTYPE_RENDER })
            {
                var q = new QUERYADAPTERINFO { hAdapter = _h, Type = type, pData = &v, Size = sizeof(uint) };
                if (D3DKMTQueryAdapterInfo(&q) == 0) return v;
            }
            return 0;
        }

        string? QueryName()
        {
            // D3DKMT_ADAPTERREGISTRYINFO: four WCHAR[MAX_PATH] strings, the adapter's name first
            const int Chars = 4 * 260;
            char* buf = stackalloc char[Chars];
            foreach (int type in new[] { KMTQAITYPE_ADAPTERREGISTRYINFO, KMTQAITYPE_ADAPTERREGISTRYINFO_RENDER })
            {
                new Span<char>(buf, Chars).Clear();
                var q = new QUERYADAPTERINFO { hAdapter = _h, Type = type, pData = buf, Size = Chars * sizeof(char) };
                if (D3DKMTQueryAdapterInfo(&q) != 0) continue;
                var name = new ReadOnlySpan<char>(buf, 260);
                int end = name.IndexOf('\0');
                string s = (end >= 0 ? name[..end] : name).Trim().ToString();
                if (s.Length > 0) return s;
            }
            return null;
        }

        public (double? Temp, double? CoreMhz, double? MemMhz, int? FanRpm, double? PowerPct) Read()
        {
            double? temp = null, core = null, mem = null, pow = null; int? fan = null;
            var ap = new ADAPTER_PERFDATA();
            var q = new QUERYADAPTERINFO { hAdapter = _h, Type = KMTQAITYPE_ADAPTERPERFDATA, pData = &ap, Size = (uint)sizeof(ADAPTER_PERFDATA) };
            if (D3DKMTQueryAdapterInfo(&q) == 0)
            {
                if (ap.Temperature > 0) temp = ap.Temperature / 10.0;
                if (ap.MemoryFrequency > 0) mem = ap.MemoryFrequency / 1_000_000.0;
                if (ap.FanRPM > 0) fan = (int)ap.FanRPM;
                if (ap.Power > 0) pow = ap.Power / 10.0;
            }
            var np = new NODE_PERFDATA { NodeOrdinal = 0 };
            q = new QUERYADAPTERINFO { hAdapter = _h, Type = KMTQAITYPE_NODEPERFDATA, pData = &np, Size = (uint)sizeof(NODE_PERFDATA) };
            if (D3DKMTQueryAdapterInfo(&q) == 0 && np.Frequency > 0) core = np.Frequency / 1_000_000.0;
            return (temp, core, mem, fan, pow);
        }

        public void Dispose()
        {
            if (_h == 0) return;
            var c = new CLOSEADAPTER { hAdapter = _h };
            D3DKMTCloseAdapter(&c);
            _h = 0;
        }
    }

    // ===================================================================== AMD ADL PMLog

    internal sealed class Adl : IDisposable
    {
        public const int CLK_GFX = 1, CLK_MEM = 2, CLK_SOC = 3, TEMP_EDGE = 8, TEMP_MEM = 9, FAN_RPM = 14, FAN_PCT = 15,
            SOC_POWER = 17, ACTIVITY_GFX = 19, ASIC_POWER = 23, TEMP_HOTSPOT = 27, TEMP_GFX = 28, TEMP_SOC = 29,
            GFX_POWER = 30, TEMP_CPU = 32, CPU_POWER = 33, CLK_CPU = 34;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        static IntPtr Alloc(int size) => Marshal.AllocHGlobal(size);

        IntPtr _lib, _ctx;
        delegate* unmanaged[Stdcall]<IntPtr, int> _destroy;
        delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int> _pmlog;
        int _adapter = -1;
        const int OutputSize = 4 + 256 * 8;
        readonly byte[] _buf = new byte[OutputSize];

        public static Adl? TryCreate()
        {
            if (!NativeLibrary.TryLoad("atiadlxx.dll", out var lib)) return null;
            var a = new Adl { _lib = lib };
            try
            {
                if (!NativeLibrary.TryGetExport(lib, "ADL2_Main_Control_Create", out var pCreate) ||
                    !NativeLibrary.TryGetExport(lib, "ADL2_Adapter_NumberOfAdapters_Get", out var pCount) ||
                    !NativeLibrary.TryGetExport(lib, "ADL2_New_QueryPMLogData_Get", out var pLog)) { a.Dispose(); return null; }
                if (NativeLibrary.TryGetExport(lib, "ADL2_Main_Control_Destroy", out var pDestroy))
                    a._destroy = (delegate* unmanaged[Stdcall]<IntPtr, int>)pDestroy;
                var create = (delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<int, IntPtr>, int, IntPtr*, int>)pCreate;
                IntPtr ctx;
                if (create(&Alloc, 1, &ctx) != 0) { a.Dispose(); return null; }
                a._ctx = ctx;
                a._pmlog = (delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)pLog;
                int n = 0;
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)pCount)(ctx, &n);
                for (int i = 0; i < Math.Min(n, 32); i++)
                {
                    a._adapter = i;
                    if (a.Read() is { Count: > 0 }) return a;
                }
            }
            catch { }
            a.Dispose();
            return null;
        }

        /// <summary>sensor index -> value for every supported PMLog sensor.</summary>
        public Dictionary<int, double>? Read()
        {
            if (_pmlog == null || _adapter < 0) return null;
            fixed (byte* p = _buf)
            {
                new Span<byte>(p, OutputSize).Clear();
                *(int*)p = OutputSize;
                if (_pmlog(_ctx, _adapter, p) != 0) return null;
                var d = new Dictionary<int, double>();
                for (int i = 1; i < 256; i++)
                {
                    int supported = *(int*)(p + 4 + i * 8);
                    if (supported != 0) d[i] = *(int*)(p + 8 + i * 8);
                }
                return d;
            }
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero) { if (_destroy != null) _destroy(_ctx); _ctx = IntPtr.Zero; }
            if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
        }
    }

    // ===================================================================== Lenovo GameZone WMI (admin)

    internal sealed class Lenovo : IDisposable
    {
        readonly System.Management.ManagementObject _gz;
        readonly bool _cpu, _gpu, _fan1, _fan2;

        Lenovo(System.Management.ManagementObject gz)
        {
            _gz = gz;
            _cpu = Call("GetCPUTemp") is > 0;
            _gpu = Call("GetGPUTemp") is > 0;
            _fan1 = Call("GetFan1Speed") is > 0;
            _fan2 = Call("GetFan2Speed") is > 0;
        }

        public static Lenovo? TryCreate()
        {
            if (!Native.IsUserAnAdmin()) return null; // the GameZone provider only answers elevated callers
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                var l = new Lenovo(o);
                if (l._cpu || l._gpu || l._fan1) return l;
                o.Dispose();
            }
            return null;
        }

        int? Call(string method)
        {
            try
            {
                var r = _gz.InvokeMethod(method, null, null);
                return r?["Data"] is { } v ? Convert.ToInt32(v) : null;
            }
            catch { return null; }
        }

        public (int? cpu, int? gpu, int? fan1, int? fan2) Read() =>
            (_cpu ? Call("GetCPUTemp") : null, _gpu ? Call("GetGPUTemp") : null, _fan1 ? Call("GetFan1Speed") : null, _fan2 ? Call("GetFan2Speed") : null);

        public void Dispose() => _gz.Dispose();
    }
}
