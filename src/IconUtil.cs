using System.Drawing;
using System.Drawing.Imaging;

namespace ResourceLens;

internal static class IconUtil
{
    static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>32×32 PNG of a file's icon (exe, lnk, …), or null.</summary>
    public static byte[]? Png(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            byte[]? png = null;
            try
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null)
                {
                    using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        using var big = new Icon(ico, 32, 32);
                        g.DrawIcon(big, new Rectangle(0, 0, 32, 32));
                    }
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
            }
            catch { }
            if (Cache.Count > 500) Cache.Clear();
            return Cache[path] = png;
        }
    }
}
