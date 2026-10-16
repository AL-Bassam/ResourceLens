namespace ResourceLens;

internal static class Log
{
    static readonly object Gate = new();
    static string LogPath => Path.Combine(AppSettings.DataDir, "errors.log");

    public static void Write(string where, Exception ex) => Write($"{where}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
