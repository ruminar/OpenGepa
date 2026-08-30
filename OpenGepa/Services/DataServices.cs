using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null) => BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    public string BaseDirectory { get; }
    public string DataFile => Path.Combine(BaseDirectory, "opengepa.json");
    public string BackupFile => Path.Combine(BaseDirectory, "opengepa.backup.json");
    public string LastGoodFile => Path.Combine(BaseDirectory, "opengepa.lastgood.json");
    public string TemporaryFile => Path.Combine(BaseDirectory, "opengepa.tmp");
    public string IconDirectory => Path.Combine(BaseDirectory, "icon");
    public void EnsureWritable()
    {
        Directory.CreateDirectory(IconDirectory);
        var probe = Path.Combine(BaseDirectory, $".write-{Guid.NewGuid():N}.tmp");
        try { using var s = new FileStream(probe, FileMode.CreateNew); s.WriteByte(0); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
}

public static class NameRules
{
    public static string Normalize(string? name) => (name ?? "").Trim().Normalize(NormalizationForm.FormC);
    public static bool IsValid(string? name, out string error)
    {
        var value = Normalize(name);
        if (value.Length == 0) { error = "名前を入力してください。"; return false; }
        if (value.Any(char.IsControl) || value.Contains('\r') || value.Contains('\n'))
        { error = "名前に改行または制御文字は使用できません。"; return false; }
        error = ""; return true;
    }
}

public sealed class DataValidator
{
    public void Validate(OpenGepaData data)
    {
        if (data.FormatVersion != OpenGepaData.CurrentFormatVersion)
            throw new InvalidDataException($"未対応のformatVersionです: {data.FormatVersion}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateNames(data.Tabs.Select(x => x.Name), "LauncherTab");
        ValidateOrders(data.Tabs.Select(x => x.Order), "LauncherTab");
        foreach (var tab in data.Tabs)
        {
            ValidateId(tab.Id, ids); tab.Name = Required(tab.Name); ValidateIcon(tab.Icon, tab.Name);
            ValidateNodes(tab.Children, ids, []);
        }
        SortTabs(data.Tabs);
        if (data.SelectedTabId is not null && data.Tabs.All(t => !t.Id.Equals(data.SelectedTabId, StringComparison.OrdinalIgnoreCase)))
            data.SelectedTabId = null;
    }

    private static void ValidateNodes(IEnumerable<LauncherNode> source, HashSet<string> ids, HashSet<string> ancestors)
    {
        var nodes = source.ToList(); ValidateNames(nodes.Select(x => x.Name), "同一Group"); ValidateOrders(nodes.Select(x => x.Order), "同一Group");
        foreach (var node in nodes)
        {
            ValidateId(node.Id, ids); node.Name = Required(node.Name);
            if (node is LauncherItem item) ValidateTarget(item);
            ValidateIcon(node.Icon, node.Name);
            if (node is GroupNode group)
            {
                if (!ancestors.Add(group.Id)) throw new InvalidDataException($"Group {group.Name} に循環があります。");
                ValidateNodes(group.Children, ids, ancestors); ancestors.Remove(group.Id);
            }
        }
        if (source is ObservableCollection<LauncherNode> collection) SortNodes(collection);
    }
    private static void ValidateNames(IEnumerable<string> names, string scope)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) { var n = Required(name); if (!set.Add(n)) throw new InvalidDataException($"{scope}内で「{n}」が重複しています。"); }
    }
    private static void ValidateOrders(IEnumerable<int> orders, string scope)
    { var values = orders.ToList(); if (values.Any(x => x < 0) || values.Distinct().Count() != values.Count) throw new InvalidDataException($"{scope}の表示順が不正です。"); }
    private static void ValidateTarget(LauncherItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Target)) throw new InvalidDataException($"{item.Name}のtargetが空です。");
        if (item is UrlItem)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new InvalidDataException($"{item.Name}のURLはHTTPまたはHTTPSで指定してください。");
        }
        else if (!Path.IsPathFullyQualified(item.Target)) throw new InvalidDataException($"{item.Name}のtargetは絶対パスで指定してください。");
    }
    private static void ValidateIcon(string? icon, string name)
    {
        if (icon is null) return;
        var normalized = icon.Replace('\\', '/');
        if (Path.IsPathRooted(icon) || !normalized.StartsWith("icon/", StringComparison.OrdinalIgnoreCase) || normalized.Split('/').Any(x => x is "" or "." or "..") || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name}のアイコンパスが不正です。");
    }
    private static void SortTabs(ObservableCollection<LauncherTab> tabs)
    { var sorted = tabs.OrderBy(x => x.Order).ToList(); for (var i = 0; i < sorted.Count; i++) { var index = tabs.IndexOf(sorted[i]); if (index != i) tabs.Move(index, i); } }
    private static void SortNodes(ObservableCollection<LauncherNode> nodes)
    { var sorted = nodes.OrderBy(x => x.Order).ToList(); for (var i = 0; i < sorted.Count; i++) { var index = nodes.IndexOf(sorted[i]); if (index != i) nodes.Move(index, i); } }
    private static string Required(string name) { if (!NameRules.IsValid(name, out var e)) throw new InvalidDataException(e); return NameRules.Normalize(name); }
    private static void ValidateId(string id, HashSet<string> ids)
    { if (!Guid.TryParse(id, out _) || !ids.Add(id)) throw new InvalidDataException($"不正または重複したIDです: {id}"); }
}

public enum DataSource { New, Current, Backup, LastGood }
public sealed record LoadResult(OpenGepaData Data, DataSource Source);

public sealed class DataStore
{
    private readonly AppPaths _paths; private readonly DataValidator _validator;
    public JsonSerializerOptions JsonOptions { get; } = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public DataStore(AppPaths paths, DataValidator validator) { _paths = paths; _validator = validator; }

    public LoadResult Load()
    {
        var files = new[] { (_paths.DataFile, DataSource.Current), (_paths.BackupFile, DataSource.Backup), (_paths.LastGoodFile, DataSource.LastGood) };
        var errors = new List<string>();
        foreach (var (path, source) in files)
        {
            if (!File.Exists(path)) continue;
            try { var data = Read(path); return new(data, source); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidDataException("保存データを読み込めませんでした。\n" + string.Join("\n", errors));
        var tab = new LauncherTab();
        return new(new OpenGepaData { SelectedTabId = tab.Id, Tabs = new ObservableCollection<LauncherTab> { tab } }, DataSource.New);
    }

    public void Save(OpenGepaData data)
    {
        _validator.Validate(data); Write(_paths.TemporaryFile, data); _ = Read(_paths.TemporaryFile);
        if (File.Exists(_paths.DataFile)) File.Replace(_paths.TemporaryFile, _paths.DataFile, _paths.BackupFile, true);
        else File.Move(_paths.TemporaryFile, _paths.DataFile);
    }

    public OpenGepaData Clone(OpenGepaData data) => Deserialize(JsonSerializer.Serialize(data, JsonOptions));
    public void MarkLastGood(OpenGepaData data) => WriteLastGood(data);
    public string Serialize(OpenGepaData data) => JsonSerializer.Serialize(data, JsonOptions);
    public OpenGepaData Deserialize(string json)
    { var data = JsonSerializer.Deserialize<OpenGepaData>(json, JsonOptions) ?? throw new InvalidDataException("JSONが空です。"); _validator.Validate(data); return data; }
    private OpenGepaData Read(string path) => Deserialize(File.ReadAllText(path, Encoding.UTF8));
    private void WriteLastGood(OpenGepaData data)
    {
        var temp = _paths.LastGoodFile + ".tmp"; Write(temp, data); _ = Read(temp);
        if (File.Exists(_paths.LastGoodFile)) File.Replace(temp, _paths.LastGoodFile, null, true); else File.Move(temp, _paths.LastGoodFile);
    }
    private void Write(string path, OpenGepaData data)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(data, JsonOptions)); writer.Flush(); stream.Flush(true);
    }
}
