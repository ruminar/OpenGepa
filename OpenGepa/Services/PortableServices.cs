using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class StartupService
{
    private const string ShortcutName = "OpenGepa.lnk";
    public string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);
    public bool IsEnabled => File.Exists(ShortcutPath) && IsManagedShortcut();
    public void SetEnabled(bool enabled)
    {
        if (enabled) CreateShortcut();
        else if (File.Exists(ShortcutPath) && IsManagedShortcut()) File.Delete(ShortcutPath);
    }
    public void RepairShortcutIfEnabled()
    {
        if (File.Exists(ShortcutPath) && IsManagedShortcut()) CreateShortcut();
    }
    private bool IsManagedShortcut()
    {
        try { dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!; dynamic link = shell.CreateShortcut(ShortcutPath); return string.Equals((string)link.Description, "OpenGepa automatic startup", StringComparison.Ordinal); }
        catch { return false; }
    }
    private void CreateShortcut()
    {
        if (File.Exists(ShortcutPath) && !IsManagedShortcut()) throw new IOException("同名のOpenGepa管理外ショートカットがStartupフォルダにあるため、上書きしません。");
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenGepa.exe");
        link.WorkingDirectory = AppContext.BaseDirectory;
        link.Description = "OpenGepa automatic startup";
        link.Save();
    }
}

public sealed class ProfileService
{
    private readonly AppService _app;
    public ProfileService(AppService app) => _app = app;

    public void Save(string requestedPath)
    {
        var path = UniquePath(requestedPath); var temp = path + $".{Guid.NewGuid():N}.tmp";
        var profileData = _app.Store.Clone(_app.Data); RewriteForProfile(profileData);
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(new { format = "OpenGepaProfile", formatVersion = 1, createdAt = DateTimeOffset.Now, createdBy = "OpenGepa", appVersion = "0.1.0" }, _app.Store.JsonOptions));
            WriteEntry(archive, "settings.json", JsonSerializer.Serialize(new { selectedTabId = profileData.SelectedTabId, appearance = profileData.Appearance, itemLaunch = profileData.ItemLaunch, defaultIcons = profileData.DefaultIcons, tabs = profileData.Tabs.Select(t => new { t.Id, t.IsVisible, t.Order }) }, _app.Store.JsonOptions));
            foreach (var tab in profileData.Tabs) WriteEntry(archive, $"menus/{tab.Id}.json", JsonSerializer.Serialize(tab, _app.Store.JsonOptions));
            foreach (var iconPath in EnumerateIcons(_app.Data).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = SafeRuntimePath(iconPath); if (!File.Exists(source)) continue;
                archive.CreateEntryFromFile(source, "icons/" + Path.GetFileName(source), CompressionLevel.Optimal);
            }
            foreach (var source in EnumerateIconSetFiles())
                archive.CreateEntryFromFile(source, "iconSet/" + Path.GetFileName(source), CompressionLevel.Optimal);
        }
        using (var verify = ZipFile.OpenRead(temp))
        {
            if (verify.GetEntry("manifest.json") is null || verify.GetEntry("settings.json") is null) throw new InvalidDataException("Profileの検証に失敗しました。");
            foreach (var entry in verify.Entries.Where(x => x.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) using (JsonDocument.Parse(entry.Open())) { }
        }
        File.Move(temp, path);
    }

    public OpenGepaData Load(string path)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "OpenGepa", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tempRoot);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > 20000 || archive.Entries.Sum(x => x.Length) > 512L * 1024 * 1024) throw new InvalidDataException("Profileが安全上限を超えています。");
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!entryNames.Add(entry.FullName.Replace('\\', '/')) || entry.Length > 20L * 1024 * 1024) throw new InvalidDataException("Profileに重複パスまたは大きすぎるエントリが含まれています。");
                var full = Path.GetFullPath(Path.Combine(tempRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Profileに危険なパスが含まれています。");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!); entry.ExtractToFile(full, false);
            }
            var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempRoot, "manifest.json")));
            if (manifest.RootElement.GetProperty("format").GetString() != "OpenGepaProfile" || manifest.RootElement.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("未対応のProfileです。");
            var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(tempRoot, "settings.json")));
            var appearance = settings.RootElement.TryGetProperty("appearance", out var appearanceElement) ? JsonSerializer.Deserialize<AppearanceSettings>(appearanceElement.GetRawText(), _app.Store.JsonOptions) ?? new AppearanceSettings() : new AppearanceSettings();
            var itemLaunch = settings.RootElement.TryGetProperty("itemLaunch", out var itemLaunchElement) ? JsonSerializer.Deserialize<ItemLaunchSettings>(itemLaunchElement.GetRawText(), _app.Store.JsonOptions) ?? new ItemLaunchSettings() : new ItemLaunchSettings();
            var defaultIcons = settings.RootElement.TryGetProperty("defaultIcons", out var defaultIconsElement) ? JsonSerializer.Deserialize<DefaultIconSettings>(defaultIconsElement.GetRawText(), _app.Store.JsonOptions) ?? new DefaultIconSettings() : new DefaultIconSettings();
            var data = new OpenGepaData { SelectedTabId = settings.RootElement.GetProperty("selectedTabId").GetString(), Appearance = appearance, ItemLaunch = itemLaunch, DefaultIcons = defaultIcons };
            foreach (var menu in Directory.EnumerateFiles(Path.Combine(tempRoot, "menus"), "*.json"))
            {
                var tab = JsonSerializer.Deserialize<LauncherTab>(File.ReadAllText(menu), _app.Store.JsonOptions) ?? throw new InvalidDataException("LauncherTabを読み込めません。");
                RewriteIcons(tab, tempRoot); data.Tabs.Add(tab);
            }
            ImportIconSetFiles(tempRoot);
            return _app.Store.Deserialize(_app.Store.Serialize(data));
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private void RewriteIcons(LauncherTab tab, string tempRoot)
    {
        if (tab.Icon is not null) tab.Icon = ImportIcon(tab.Icon, tempRoot);
        foreach (var node in Walk(tab.Children)) if (node.Icon is not null) node.Icon = ImportIcon(node.Icon, tempRoot);
    }
    private static void RewriteForProfile(OpenGepaData data)
    {
        foreach (var tab in data.Tabs)
        {
            if (tab.Icon is not null) tab.Icon = "icons/" + Path.GetFileName(tab.Icon);
            foreach (var node in Walk(tab.Children)) if (node.Icon is not null) node.Icon = "icons/" + Path.GetFileName(node.Icon);
        }
    }
    private string? ImportIcon(string profilePath, string root)
    {
        var normalized = profilePath.Replace('\\', '/');
        if (!normalized.StartsWith("icons/", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("icon/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不正なProfileアイコンパスです。");
        var source = Path.Combine(root, "icons", Path.GetFileName(profilePath)); if (!File.Exists(source)) return null;
        var name = Path.GetFileName(source); var target = Path.Combine(_app.Paths.IconDirectory, name);
        if (File.Exists(target))
        {
            if (SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target)))) return Path.GetRelativePath(_app.Paths.BaseDirectory, target).Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
            for (var i = 2; File.Exists(target); i++) target = Path.Combine(_app.Paths.IconDirectory, $"{stem}_{i}{ext}");
        }
        using (var verify = System.Drawing.Image.FromFile(source)) { if (verify.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid) throw new InvalidDataException("ProfileのアイコンがPNGではありません。"); }
        File.Copy(source, target, false); return Path.GetRelativePath(_app.Paths.BaseDirectory, target).Replace('\\', '/');
    }
    private string SafeRuntimePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_app.Paths.BaseDirectory, relative));
        if (!full.StartsWith(_app.Paths.IconDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不正なアイコンパスです。"); return full;
    }
    private IEnumerable<string> EnumerateIconSetFiles()
    {
        if (!Directory.Exists(_app.Paths.IconSetDirectory)) return [];
        return Directory.EnumerateFiles(_app.Paths.IconSetDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase));
    }
    private void ImportIconSetFiles(string tempRoot)
    {
        var sourceDirectory = Path.Combine(tempRoot, "iconSet");
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source); var extension = Path.GetExtension(name);
            if ((!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            var target = Path.Combine(_app.Paths.IconSetDirectory, name);
            if (!File.Exists(target)) File.Copy(source, target, false);
        }
    }
    private static IEnumerable<string> EnumerateIcons(OpenGepaData data)
    { foreach (var t in data.Tabs) { if (t.Icon is not null) yield return t.Icon; foreach (var n in Walk(t.Children)) if (n.Icon is not null) yield return n.Icon; } }
    private static IEnumerable<LauncherNode> Walk(IEnumerable<LauncherNode> nodes)
    { foreach (var n in nodes) { yield return n; if (n is GroupNode g) foreach (var c in Walk(g.Children)) yield return c; } }
    private static void WriteEntry(ZipArchive archive, string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
    private static string UniquePath(string path)
    { if (!File.Exists(path)) return path; var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); for (var i = 2; ; i++) { var p = Path.Combine(dir, $"{stem}_{i}{ext}"); if (!File.Exists(p)) return p; } }
}
