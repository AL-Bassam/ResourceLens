using System.Reflection;

namespace ResourceLens;

internal static class UiResources
{
    static readonly Assembly Asm = Assembly.GetExecutingAssembly();

    /// <summary>Opens an embedded UI file. MSBuild keeps "\" from %(RecursiveDir) in resource names.</summary>
    public static Stream? Open(string path)
    {
        if (path.Contains("..")) return null;
        return Asm.GetManifestResourceStream("ui/" + path)
            ?? Asm.GetManifestResourceStream("ui/" + path.Replace('/', '\\'));
    }
}
