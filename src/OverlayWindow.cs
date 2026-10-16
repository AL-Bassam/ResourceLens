using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>
/// Gaming overlay: a small always-on-top panel in a screen corner, drawn with per-pixel alpha (layered window).
/// It never takes focus and every click passes through to the game underneath. Updates once per second.
/// Note: like every overlay that does not inject into the game, it shows over borderless/windowed games;
/// exclusive-fullscreen games draw above all desktop windows.
/// </summary>
internal sealed class OverlayWindow : Form
{
    public static readonly string[] AllItems = { "cpu", "cpuTemp", "cpuClock", "gpu", "gpuTemp", "gpuClock", "ram", "vram", "power", "net", "fan", "time" };

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly OverlaySampler _sampler;
    AppSettings _s;

    public OverlayWindow(AppSettings s)
    {
        _s = s;
        _sampler = new OverlaySampler();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "ResourceLens overlay";
        _timer.Tick += (_, _) => Tick();
        Load += (_, _) => { Tick(); _timer.Start(); };
    }

    public void Apply(AppSettings s) { _s = s; Tick(); }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT (click-through) | TOOLWINDOW (no Alt-Tab) | NOACTIVATE | TOPMOST
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
            return cp;
        }
    }

    // ------------------------------------------------------------------ drawing

    static readonly Color CCpu = Color.FromArgb(129, 140, 248), CGpu = Color.FromArgb(244, 114, 182), CRam = Color.FromArgb(34, 211, 238),
        CNet = Color.FromArgb(52, 211, 153), CPow = Color.FromArgb(251, 191, 36), CDim = Color.FromArgb(160, 168, 186), CVal = Color.FromArgb(245, 247, 250);

    static Color TempColor(double? c) => c is null ? CVal : c >= 85 ? Color.FromArgb(248, 113, 113) : c >= 70 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(74, 222, 128);

    static string Mhz(double? v) => v is null ? "—" : v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz";
    static string Bytes(double? b) => b is null ? "—" : b >= 1073741824 ? $"{b / 1073741824:0.0} GB" : $"{b / 1048576:0} MB";
    static string Rate(double? b) => b is null ? "—" : b >= 1048576 ? $"{b / 1048576:0.0} MB/s" : $"{b / 1024:0} KB/s";

    void Tick()
    {
        try
        {
            var d = _sampler.Read();
            var items = new HashSet<string>(_s.OverlayItems);
            var rows = new List<(string Label, Color Color, List<(string Text, Color Color)> Values)>();

            var cpu = new List<(string, Color)>();
            if (items.Contains("cpu")) cpu.Add(($"{d.Cpu:0}%", CVal));
            if (items.Contains("cpuTemp") && d.CpuTemp is not null) cpu.Add(($"{d.CpuTemp:0}°C", TempColor(d.CpuTemp)));
            if (items.Contains("cpuClock") && d.CpuMhz is not null) cpu.Add((Mhz(d.CpuMhz), CDim));
            if (cpu.Count > 0) rows.Add(("CPU", CCpu, cpu));

            var gpu = new List<(string, Color)>();
            if (items.Contains("gpu")) gpu.Add(($"{d.Gpu:0}%", CVal));
            if (items.Contains("gpuTemp") && d.GpuTemp is not null) gpu.Add(($"{d.GpuTemp:0}°C", TempColor(d.GpuTemp)));
            if (items.Contains("gpuClock") && d.GpuMhz is not null) gpu.Add((Mhz(d.GpuMhz), CDim));
            if (gpu.Count > 0) rows.Add(("GPU", CGpu, gpu));

            if (items.Contains("vram") && d.Vram is not null) rows.Add(("VRAM", CGpu, new() { (Bytes(d.Vram), CVal) }));
            if (items.Contains("ram")) rows.Add(("RAM", CRam, new() { (Bytes(d.RamUsed), CVal), ($"{d.RamPct:0}%", CDim) }));
            if (items.Contains("power") && d.Power is not null) rows.Add(("PWR", CPow, new() { ($"{d.Power:0.0} W", CVal) }));
            if (items.Contains("net") && d.NetRx is not null) rows.Add(("NET", CNet, new() { ($"↓ {Rate(d.NetRx)}", CVal), ($"↑ {Rate(d.NetTx)}", CDim) }));
            if (items.Contains("fan") && d.Fan is not null) rows.Add(("FAN", CDim, new() { ($"{d.Fan} RPM", CVal) }));
            if (items.Contains("time")) rows.Add(("", CDim, new() { (DateTime.Now.ToString("HH:mm"), CDim) }));
            if (rows.Count == 0) rows.Add(("ResourceLens", CCpu, new()));

            Render(rows);
            KeepOnTop();
        }
        catch (Exception ex) { Log.Write("overlay", ex); }
    }

    void Render(List<(string Label, Color Color, List<(string Text, Color Color)> Values)> rows)
    {
        float dpi = DeviceDpi / 96f;
        float pt = 10.5f * _s.OverlayScale / 100f * dpi;
        using var fLabel = new Font("Segoe UI", pt, FontStyle.Bold, GraphicsUnit.Point);
        using var fValue = new Font("Segoe UI Semibold", pt, FontStyle.Regular, GraphicsUnit.Point);
        int pad = (int)(9 * dpi), gap = (int)(10 * dpi), lineGap = (int)(2 * dpi);

        using var probe = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(probe);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var fmt = StringFormat.GenericTypographic;
        SizeF M(string s, Font f) => s.Length == 0 ? SizeF.Empty : mg.MeasureString(s, f, int.MaxValue, fmt);

        float labelW = rows.Max(r => M(r.Label, fLabel).Width);
        int colCount = rows.Max(r => r.Values.Count);
        var colW = new float[colCount];
        foreach (var r in rows) for (int i = 0; i < r.Values.Count; i++) colW[i] = Math.Max(colW[i], M(r.Values[i].Text, fValue).Width);
        float lineH = Math.Max(M("CPU", fLabel).Height, M("0%", fValue).Height) + lineGap;
        int w = (int)Math.Ceiling(pad * 2 + labelW + (labelW > 0 ? gap : 0) + colW.Sum() + gap * Math.Max(0, colCount - 1));
        int h = (int)Math.Ceiling(pad * 2 + lineH * rows.Count - lineGap);

        using var bmp = new Bitmap(Math.Max(w, 10), Math.Max(h, 10), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            int alphaBg = (int)(255 * _s.OverlayOpacity / 100.0);
            if (alphaBg > 0)
            {
                using var path = Rounded(new RectangleF(0, 0, bmp.Width - 1, bmp.Height - 1), 8 * dpi);
                using var bg = new SolidBrush(Color.FromArgb(alphaBg, 12, 14, 20));
                g.FillPath(bg, path);
                using var border = new Pen(Color.FromArgb(Math.Min(255, alphaBg / 2 + 20), 255, 255, 255), 1);
                g.DrawPath(border, path);
            }
            float y = pad;
            foreach (var r in rows)
            {
                float x = pad;
                if (labelW > 0)
                {
                    using var lb = new SolidBrush(r.Color);
                    // a soft shadow keeps text readable over bright game scenes even with a transparent background
                    DrawShadowed(g, r.Label, fLabel, lb, x, y, fmt);
                    x += labelW + gap;
                }
                for (int i = 0; i < r.Values.Count; i++)
                {
                    using var vb = new SolidBrush(r.Values[i].Color);
                    DrawShadowed(g, r.Values[i].Text, fValue, vb, x, y, fmt);
                    x += colW[i] + gap;
                }
                y += lineH;
            }
        }
        Present(bmp);
    }

    static void DrawShadowed(Graphics g, string s, Font f, Brush b, float x, float y, StringFormat fmt)
    {
        using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        g.DrawString(s, f, shadow, x + 1, y + 1, fmt);
        g.DrawString(s, f, b, x, y, fmt);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ------------------------------------------------------------------ layered window plumbing

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    void Present(Bitmap bmp)
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        int margin = (int)(14 * DeviceDpi / 96f);
        int x = _s.OverlayCorner is "tr" or "br" ? area.Right - bmp.Width - margin : area.Left + margin;
        int y = _s.OverlayCorner is "bl" or "br" ? area.Bottom - bmp.Height - margin - (int)(40 * DeviceDpi / 96f) : area.Top + margin;

        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = x, Y = y };
            var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    void KeepOnTop() =>
        // games can push other top-most windows down; re-assert without moving, sizing or activating
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _sampler.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Light 1-second sampler for the overlay (system totals only, no per-process work).</summary>
internal sealed class OverlaySampler : IDisposable
{
    public sealed class Data
    {
        public double Cpu, Gpu, RamPct;
        public double RamUsed;
        public double? CpuTemp, GpuTemp, CpuMhz, GpuMhz, Vram, Power, NetRx, NetTx;
        public int? Fan;
    }

    readonly PdhCounters _pdh = new(false, true, true);
    long _idle, _kernel, _user;

    public OverlaySampler() => Native.GetSystemTimes(out _idle, out _kernel, out _user);

    public Data Read()
    {
        var d = new Data();
        if (Native.GetSystemTimes(out long i, out long k, out long u))
        {
            long di = i - _idle, dk = k - _kernel, du = u - _user;
            if (dk + du > 0) d.Cpu = Math.Clamp((dk + du - di) * 100.0 / (dk + du), 0, 100);
            _idle = i; _kernel = k; _user = u;
        }
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref ms);
        d.RamUsed = ms.ullTotalPhys - ms.ullAvailPhys;
        d.RamPct = d.RamUsed * 100.0 / Math.Max(1, ms.ullTotalPhys);
        var p = _pdh.Collect();
        d.Gpu = double.IsNaN(p.GpuTotal) ? 0 : p.GpuTotal;
        if (!double.IsNaN(p.NetRxBps)) { d.NetRx = p.NetRxBps; d.NetTx = p.NetTxBps; }
        // Shared reader on its own thread: a slow driver never freezes the overlay (or the app's UI thread).
        var s = SensorHub.Latest();
        if (s is not null)
        {
            d.CpuTemp = s.CpuTemp; d.GpuTemp = s.GpuTemp; d.CpuMhz = s.CpuMhz; d.GpuMhz = s.GpuMhz;
            d.Vram = s.GpuDedicatedUsed; d.Power = s.PackagePower ?? s.CpuPower; d.Fan = s.Fan1Rpm;
        }
        return d;
    }

    public void Dispose() => _pdh.Dispose();
}
