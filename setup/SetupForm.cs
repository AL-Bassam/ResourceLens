using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ResourceLensSetup;

/// <summary>One-page installer: options → progress → done.</summary>
internal sealed class SetupForm : Form
{
    static readonly Color Accent = Color.FromArgb(79, 70, 229), Accent2 = Color.FromArgb(37, 99, 235);
    static readonly Color Muted = Color.FromArgb(100, 108, 125), Footer = Color.FromArgb(246, 247, 251);

    readonly Icon _icon;
    readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 8) };
    readonly TextBox _path = new() { ReadOnly = true, Width = 360 };
    readonly Button _browse = new() { Text = Strings.Change, AutoSize = true };
    readonly CheckBox _autostart = new() { Text = Strings.Autostart, Checked = true, AutoSize = true };
    readonly CheckBox _desktop = new() { Text = Strings.Desktop, Checked = true, AutoSize = true };
    readonly CheckBox _run = new() { Text = Strings.RunAfter, Checked = true, AutoSize = true };
    readonly Label _note = new() { AutoSize = true, ForeColor = Muted };
    readonly ProgressBar _progress = new() { Visible = false, Height = 6, Style = ProgressBarStyle.Continuous };
    readonly Label _status = new() { AutoSize = true, ForeColor = Muted };
    readonly Button _primary = new(), _cancel = new() { Text = Strings.Cancel };
    readonly bool _update;
    bool _done;

    public SetupForm()
    {
        _icon = new Icon(typeof(SetupForm).Assembly.GetManifestResourceStream("app.ico"), 64, 64);
        Text = Strings.Title;
        Icon = _icon;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(600, 430);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        if (Strings.Ar) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

        string existing = Installer.ExistingDir();
        _update = existing != null;
        _path.Text = Program.Value("--dir") ?? existing ?? Installer.DefaultDir;
        _note.Text = Strings.Space(Mb(Installer.AppSize())) + (_update ? "\n" + Strings.Update(Installer.Version) : "");

        // ---- header (painted gradient, icon, name, version, tagline); laid out per reading direction
        var header = new Panel { Dock = DockStyle.Top, Height = 112 };
        var title = new Label { Text = "ResourceLens", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 17f) };
        var sub = new Label
        {
            Text = Strings.Ar ? $"الإصدار ‪{Installer.Version}‬  •  {Strings.Tagline}" : $"v{Installer.Version}  •  {Strings.Tagline}",
            ForeColor = Color.FromArgb(226, 232, 255), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9f),
        };
        header.Controls.Add(title);
        header.Controls.Add(sub);
        int Px(int v) => v * DeviceDpi / 96;
        header.Layout += (_, _) =>
        {
            int w = header.ClientSize.Width, pad = Px(28), s = Px(56), gap = Px(16), textW = w - 2 * pad - s - gap;
            int x = Strings.Ar ? pad : pad + s + gap;
            title.SetBounds(x, Px(18), textW, Px(38));
            sub.SetBounds(x, Px(58), textW, Px(42));
        };
        header.Paint += (_, e) =>
        {
            var r = header.ClientRectangle;
            using (var b = new LinearGradientBrush(r, Accent, Accent2, Strings.Ar ? 180f : 0f)) e.Graphics.FillRectangle(b, r);
            int s = Px(56), pad = Px(28);
            using var bmp = new Icon(_icon, s, s).ToBitmap();
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(bmp, new Rectangle(Strings.Ar ? r.Width - pad - s : pad, (r.Height - s) / 2, s, s));
        };

        // ---- body (options)
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
        var loc = new Label { Text = Strings.Location, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, 6) };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        _path.Margin = new Padding(0, 1, 8, 0);
        row.Controls.Add(_path);
        row.Controls.Add(_browse);
        foreach (var c in new[] { _autostart, _desktop, _run }) c.Margin = new Padding(0, 4, 0, 4);
        _note.Margin = new Padding(0, 14, 0, 0);
        _note.MaximumSize = new Size(540, 0);
        flow.Controls.AddRange(new Control[] { loc, row, _autostart, _desktop, _run, _note });
        _body.Controls.Add(flow);

        // ---- footer (status, progress, buttons)
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Footer, Padding = new Padding(28, 0, 28, 0) };
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(229, 231, 238)), 0, 0, footer.Width, 0);
        _primary.Text = _update ? Strings.UpdateBtn : Strings.Install;
        _primary.FlatStyle = FlatStyle.Flat;
        _primary.FlatAppearance.BorderSize = 0;
        _primary.BackColor = Accent;
        _primary.ForeColor = Color.White;
        _primary.Font = new Font("Segoe UI Semibold", 10f);
        _status.AutoSize = false;
        footer.Layout += (_, _) =>
        {
            int w = footer.ClientSize.Width, h = footer.ClientSize.Height, pad = Px(28), bw = Px(110), bh = Px(36), gap = Px(10);
            int y = (h - bh) / 2, px = Strings.Ar ? pad : w - pad - bw, cx = Strings.Ar ? pad + bw + gap : px - gap - bw;
            _primary.SetBounds(px, y, bw, bh);
            _cancel.SetBounds(cx, y, bw, bh);
            int sw = w - 2 * pad - 2 * bw - 3 * gap, sx = Strings.Ar ? w - pad - sw : pad;
            _status.SetBounds(sx, Px(14), sw, Px(22));
            _progress.SetBounds(sx, Px(42), sw, Px(6));
        };
        footer.Controls.AddRange(new Control[] { _status, _progress, _cancel, _primary });

        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = _primary;
        ActiveControl = _primary;
        _path.TabStop = false;
        CancelButton = _cancel;

        _browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _path.Text, ShowNewFolderButton = true };
            if (d.ShowDialog(this) == DialogResult.OK)
                _path.Text = Path.GetFileName(d.SelectedPath).Equals("ResourceLens", StringComparison.OrdinalIgnoreCase)
                    ? d.SelectedPath : Path.Combine(d.SelectedPath, "ResourceLens");
        };
        _cancel.Click += (_, _) => Close();
        _primary.Click += async (_, _) => { if (_done) Finish(); else await InstallAsync(); };
    }

    static string Mb(long bytes) => $"{bytes / 1048576.0:0} MB";

    async Task InstallAsync()
    {
        if (!Installer.WebView2Present() &&
            MessageBox.Show(this, Strings.NoWebView2, Strings.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl) == DialogResult.Yes)
            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });

        SetBusy(true);
        string dir = _path.Text;
        bool autostart = _autostart.Checked, desktop = _desktop.Checked;
        try
        {
            await Task.Run(() => Installer.Install(dir, autostart, desktop, Strings.Ar ? "ar" : "en",
                (pct, msg) => BeginInvoke((Action)(() => { _progress.Value = Math.Min(100, pct); _status.Text = msg; }))));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = "";
            MessageBox.Show(this, ex.Message, Strings.Failed, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Rtl);
            return;
        }
        ShowDone();
    }

    MessageBoxOptions Rtl => Strings.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;

    void SetBusy(bool busy)
    {
        _progress.Visible = busy;
        _primary.Enabled = _cancel.Enabled = _browse.Enabled = !busy;
        foreach (var c in new[] { _autostart, _desktop, _run }) c.Enabled = !busy;
    }

    void ShowDone()
    {
        _done = true;
        _body.Controls.Clear();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(new Label
        {
            Text = "✓  " + Strings.Done, AutoSize = true, ForeColor = Color.FromArgb(22, 163, 74),
            Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 8, 0, 12),
        });
        flow.Controls.Add(new Label { Text = Strings.DoneNote, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Muted });
        _body.Controls.Add(flow);
        _progress.Visible = false;
        _status.Text = "";
        _cancel.Visible = false;
        _primary.Text = Strings.Finish;
        _primary.Enabled = true;
    }

    void Finish()
    {
        if (_run.Checked)
            try { Installer.Launch(_path.Text); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Strings.Title); }
        Close();
    }
}
