using System.Security.Cryptography;
using System.Text;

namespace OpenGepa;

public static class InstanceIdentity
{
    private static string? _rootDirectory;
    public static string RootDirectory => _rootDirectory ?? AppContext.BaseDirectory;
    public static void ConfigureRoot(string root) => _rootDirectory = Path.GetFullPath(root);
    public static string CurrentId => FromDirectory(RootDirectory);
    public static string MutexName => $@"Local\OpenGepa.Singleton.{CurrentId}";
    public static string ShowEventName => $@"Local\OpenGepa.Show.{CurrentId}";
    public static string McpShutdownEventName => $@"Local\OpenGepa.McpShutdown.{CurrentId}";
    public static string PipeName => $"OpenGepa.Mcp.{CurrentId}";

    public static string FromDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var info = new DirectoryInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.ResolveLinkTarget(true) is DirectoryInfo resolved)
                full = resolved.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var normalized = full.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }
}
