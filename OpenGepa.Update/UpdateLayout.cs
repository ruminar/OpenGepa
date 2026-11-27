using System.Text.Json;

namespace OpenGepa.Update;

public sealed record InstallationState(string Current, string? Previous = null);
public sealed record PendingUpdate(string Version, string WorkDirectory);

public sealed class UpdateLayout
{
    public const int ApplyExitCode = 73;
    public string Root { get; }
    public string AppDirectory => Path.Combine(Root, "app");
    public string Versions => Path.Combine(AppDirectory, "versions");
    public string UpdateDirectory => Path.Combine(AppDirectory, "update");
    public string DataDirectory => Path.Combine(Root, "data");
    public string StatePath => Path.Combine(UpdateDirectory, "installation.json");
    public string PendingPath => Path.Combine(UpdateDirectory, "pending.json");
    public string Launcher => Path.Combine(Root, "OpenGepa.exe");
    public string McpLauncher => Path.Combine(Root, "OpenGepa.Mcp.exe");
    public UpdateLayout(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string VersionDirectory(string version) => UpdatePackage.ParseVersion(version) is null ? throw new InvalidDataException("版のディレクトリ名が正しくありません。") : Path.Combine(Versions, version);
    public string ApplicationPath(string version) => Path.Combine(VersionDirectory(version), "OpenGepa.exe");
    public string McpPath(string version) => Path.Combine(VersionDirectory(version), "OpenGepa.Mcp.exe");
    public void EnsureDirectories() { RequireDirectory(Root); EnsureDirectory(AppDirectory); EnsureDirectory(Versions); EnsureDirectory(UpdateDirectory); }
    public string NewWorkDirectory() { EnsureDirectories(); var path = Path.Combine(UpdateDirectory, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, ".update-work"), Path.GetFileName(path)); return path; }
    public string WorkDirectory(string value) => value.Length == 38 && value.StartsWith("stage-", StringComparison.Ordinal) && Guid.TryParseExact(value[6..], "N", out _) ? Path.Combine(UpdateDirectory, value) : throw new InvalidDataException("更新作業ディレクトリが不正です。");
    public void WriteState(InstallationState value) => WriteAtomic(StatePath, value);
    public void WritePending(PendingUpdate value) => WriteAtomic(PendingPath, value);
    public static T? Read<T>(string path) => Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), UpdatePackage.Json) ?? throw new InvalidDataException("更新管理ファイルを読み込めません。") : default;
    public static bool Exists(string path) { try { File.GetAttributes(path); return true; } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; } }
    public static void RequireDirectory(string path) { if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) throw new IOException("通常のフォルダーを指定してください。"); }
    public static void RequireFile(string path) { if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("通常のファイルを指定してください。"); }
    public static void EnsureDirectory(string path) { Directory.CreateDirectory(path); RequireDirectory(path); }
    public static void WriteAtomic<T>(string path, T value) { var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; try { File.WriteAllText(temp, JsonSerializer.Serialize(value, UpdatePackage.Json)); if (Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); } finally { if (File.Exists(temp)) File.Delete(temp); } }
    public void DeleteWorkDirectory(string path) { if (!Path.GetFullPath(path).StartsWith(UpdateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(path, ".update-work"))) throw new IOException("更新作業ディレクトリではありません。"); Directory.Delete(path, true); }
}
