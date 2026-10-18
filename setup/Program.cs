using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace ResourceLensSetup;

/// <summary>
/// ResourceLens-Setup.exe [--silent] [--dir=PATH] [--lang=ar|en] [--no-autostart] [--no-desktop] [--no-run]
/// --silent installs with the defaults (or the given options) without a window; exit code 0 on success.
/// --preview=FILE.png saves a picture of the window and exits (used to check the layout).
/// </summary>
internal static class Program
{
    public static string[] Args = Array.Empty<string>();
    public static bool Has(string flag) => Args.Contains(flag, StringComparer.OrdinalIgnoreCase);
    public static string Value(string name) =>
        Args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?.Substring(name.Length + 1);

    [STAThread]
    static int Main(string[] args)
    {
        Args = args;
        // Opened inside another app's container the install would land in that app's private storage: restart outside.
        if (Sandbox.IsRedirected(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceLens"))
            && Sandbox.RelaunchOutside(args)) return 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Major < 10)
        {
            if (!Has("--silent")) MessageBox.Show(Strings.NeedsWin10, "ResourceLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        if (IsArm64() && Environment.OSVersion.Version.Build < 22000)
        {
            // The app is x64: Windows on ARM runs x64 programs only from Windows 11 on.
            if (!Has("--silent")) MessageBox.Show(Strings.NeedsWin11Arm, "ResourceLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        if (Has("--silent"))
        {
            try
            {
                string dir = Value("--dir") ?? Installer.ExistingDir() ?? Installer.DefaultDir;
                Installer.Install(dir, !Has("--no-autostart"), !Has("--no-desktop"), Strings.Ar ? "ar" : "en", (_, _) => { });
                if (!Has("--no-run")) Installer.Launch(dir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
        var form = new SetupForm();
        if (Value("--preview") is string png)
            form.Shown += async (_, _) =>
            {
                await System.Threading.Tasks.Task.Delay(600);
                using var bmp = new Bitmap(form.Width, form.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(form.Location, Point.Empty, form.Size);
                bmp.Save(png);
                form.Close();
            };
        Application.Run(form);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    /// <summary>An ARM64 PC (asked of Windows itself: an emulated process is told it runs on x64).</summary>
    static bool IsArm64()
    {
        try { return IsWow64Process2((IntPtr)(-1), out _, out ushort native) && native == 0xAA64; }
        catch (EntryPointNotFoundException) { return false; } // Windows 10 before 1709 never runs on ARM64
    }
}

/// <summary>Arabic on Arabic Windows (or --lang=ar), English otherwise.</summary>
internal static class Strings
{
    // Evaluated on first use (after Main has read --lang).
    static bool? _ar;
    public static bool Ar => _ar ??= (Program.Value("--lang") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "ar";
    static string T(string ar, string en) => Ar ? ar : en;

    public static string Title => T("تثبيت ResourceLens", "ResourceLens Setup");
    public static string Tagline => T("سجلّ موارد جهازك: المعالج والذاكرة والقرص والشبكة وكرت الشاشة منذ تشغيل الجهاز",
        "Your PC's resource history: CPU, memory, disk, network and GPU since boot");
    public static string Location => T("مكان التثبيت", "Install location");
    public static string Change => T("تغيير…", "Change…");
    public static string Autostart => T("التشغيل تلقائياً مع ويندوز (ليبدأ التسجيل من لحظة تشغيل الجهاز)",
        "Start with Windows (so recording begins at boot)");
    public static string Desktop => T("اختصار على سطح المكتب", "Desktop shortcut");
    public static string RunAfter => T("تشغيل ResourceLens بعد التثبيت", "Run ResourceLens when setup finishes");
    public static string Space(string size) => T($"المساحة المطلوبة: ‪{size}‬ • لا يحتاج صلاحيات المسؤول ولا أي برامج إضافية",
        $"Space required: {size} • No administrator rights or extra software needed");
    public static string Update(string v) => T($"يوجد إصدار مثبت؛ سيُحدَّث إلى ‪{v}‬ مع الإبقاء على سجلك وإعداداتك.",
        $"An installed version will be updated to {v}; your history and settings are kept.");
    public static string Install => T("تثبيت", "Install");
    public static string UpdateBtn => T("تحديث", "Update");
    public static string Cancel => T("إلغاء", "Cancel");
    public static string Finish => T("إنهاء", "Finish");
    public static string Closing => T("جارٍ إغلاق النسخة العاملة…", "Closing the running copy…");
    public static string Copying => T("جارٍ نسخ الملفات…", "Copying files…");
    public static string Shortcuts => T("جارٍ إنشاء الاختصارات…", "Creating shortcuts…");
    public static string Registering => T("جارٍ التسجيل في قائمة البرامج…", "Registering with Windows…");
    public static string Done => T("تم التثبيت بنجاح", "Installed successfully");
    public static string DoneNote => T("يعمل ResourceLens بجانب الساعة ويسجّل في الخلفية. تجده في قائمة ابدأ، وتزيله من «التطبيقات المثبتة» في إعدادات ويندوز.",
        "ResourceLens runs next to the clock and records in the background. Find it in the Start menu; remove it from Installed apps in Windows Settings.");
    public static string Failed => T("تعذّر التثبيت", "Setup failed");
    public static string CloseFirst => T("ResourceLens يعمل ولم يمكن إغلاقه. أغلقه من أيقونته بجانب الساعة (خروج) ثم أعد المحاولة.",
        "ResourceLens is running and could not be closed. Exit it from its icon next to the clock, then try again.");
    public static string NoWebView2 => T("يحتاج ResourceLens إلى مكوّن Microsoft Edge WebView2 لعرض الواجهة، وهو غير موجود على هذا الجهاز. هل تريد فتح صفحة تنزيله؟ (سيستمر التثبيت والتسجيل يعمل)",
        "ResourceLens needs the Microsoft Edge WebView2 Runtime for its window, and it is missing on this PC. Open its download page? (Setup continues; recording works without it)");
    public static string NeedsWin10 => T("يتطلب ResourceLens نظام Windows 10 أو 11 بإصدار 64 بت.", "ResourceLens requires 64-bit Windows 10 or 11.");
    public static string NeedsWin11Arm => T("على الأجهزة بمعالجات ARM يتطلب ResourceLens نظام Windows 11.", "On ARM-based PCs, ResourceLens requires Windows 11.");
}
