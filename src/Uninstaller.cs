using System.Diagnostics;
using Microsoft.Win32;

namespace ResourceLens;

/// <summary>
/// `ResourceLens.exe --uninstall` — what Windows' "Installed apps" runs (registered by ResourceLens-Setup.exe).
/// Closes the running copy, removes autostart, shortcuts and the uninstall entry, optionally the recorded history,
/// and finally the program folder itself.
/// </summary>
internal static class Uninstaller
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceLens";

    public static int Run(bool quiet)
    {
        Application.EnableVisualStyles();
        var settings = AppSettings.Load();
        bool ar = settings.Language == "ar";
        bool deleteData = false;

        if (!quiet)
        {
            var remove = new TaskDialogButton(ar ? "إزالة" : "Uninstall");
            var page = new TaskDialogPage
            {
                Caption = "ResourceLens",
                Heading = ar ? "إزالة ResourceLens من هذا الجهاز؟" : "Remove ResourceLens from this PC?",
                Text = ar ? "سيتوقف التسجيل وتُحذف الاختصارات والتشغيل التلقائي." : "Recording stops; shortcuts and autostart are removed.",
                Icon = TaskDialogIcon.Warning,
                Verification = new TaskDialogVerificationCheckBox(ar
                    ? $"احذف أيضاً سجل البيانات والإعدادات ({AppSettings.DataDir})"
                    : $"Also delete the recorded history and settings ({AppSettings.DataDir})"),
                Buttons = { remove, TaskDialogButton.Cancel },
                RightToLeftLayout = ar,
            };
            if (TaskDialog.ShowDialog(page) != remove) return 1;
            deleteData = page.Verification.Checked;
        }

        Program.RequestExit();
        Autostart.Apply(new AppSettings { Autostart = false }); // Run entry + elevated logon task
        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ResourceLens.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ResourceLens.lnk"),
                 })
            try { File.Delete(lnk); } catch { }

        string exe = Environment.ProcessPath!, dir = Path.GetDirectoryName(exe)!;
        string? installDir;
        using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) installDir = k?.GetValue("InstallLocation") as string;
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        if (deleteData)
        {
            Thread.Sleep(1500); // the browser engine releases its folder shortly after the app exits
            try { Directory.Delete(AppSettings.DataDir, true); } catch (Exception ex) { Log.Write("uninstall data", ex); }
        }

        // The confirmation comes first: while it is open this exe is still running and could not be deleted.
        // A top-most owner keeps it in front of Settings (this process no longer has the foreground once the
        // first dialog closed).
        if (!quiet)
        {
            using var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen, Opacity = 0 };
            owner.Show();
            owner.Activate();
            MessageBox.Show(owner, ar ? "تمت إزالة ResourceLens." : "ResourceLens has been removed.", "ResourceLens",
                MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        }

        // A running exe cannot delete itself: a hidden helper removes it (and its folder, when it is the install
        // folder) once this process has exited, retrying for up to ~30 s.
        bool ownFolder = installDir is not null && string.Equals(Path.GetFullPath(installDir).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string path = ownFolder ? dir : exe;
        string removeCmd = ownFolder ? $"rd /s /q \"{dir}\"" : $"del /f /q \"{exe}\"";
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/c for /l %i in (1,1,30) do (ping 127.0.0.1 -n 2 >nul & {removeCmd} 2>nul & if not exist \"{path}\" exit /b 0)")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return 0;
    }
}
