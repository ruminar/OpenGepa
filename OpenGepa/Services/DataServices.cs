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
    public string DefaultDataFile => Path.Combine(BaseDirectory, "opengepa.default.json");
    public string IconDirectory => Path.Combine(BaseDirectory, "icon");
    public string IconSetDirectory => Path.Combine(BaseDirectory, "iconSet");
    public string ShortcutDirectory => Path.Combine(BaseDirectory, "shortcut");
    public void EnsureWritable()
    {
        Directory.CreateDirectory(IconDirectory);
        Directory.CreateDirectory(IconSetDirectory);
        Directory.CreateDirectory(ShortcutDirectory);
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
        if (data.WindowsMenu is null || data.Presets is null) throw new InvalidDataException("Windows機能の設定がありません。");
        if (data.Presets.HiddenItemIds is null || data.Presets.HiddenItemIds.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("プリセットの表示設定が不正です。");
        AppearanceRules.Validate(data.Appearance);
        ValidateItemLaunch(data.ItemLaunch);
        ValidateDefaultIcons(data.DefaultIcons);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateNames(data.Tabs.Where(x => !x.IsSystemTab).Select(x => x.Name), "LauncherTab");
        ValidateOrders(data.Tabs.Select(x => x.Order), "LauncherTab");
        foreach (var tab in data.Tabs)
        {
            ValidateId(tab.Id, ids); tab.Name = Required(tab.Name); ValidateIcon(tab.Icon, tab.Name);
            if (!LauncherTabKinds.IsKnown(tab.Kind)) throw new InvalidDataException($"未対応のタブ種別です: {tab.Kind}");
            if (tab.IsSystemTab)
            {
                if (tab.Children.Count != 0) throw new InvalidDataException("特殊タブに保存済みの項目は含められません。");
                continue;
            }
            ValidateNodes(tab.Children, ids, [], tab.Kind);
        }
        SortTabs(data.Tabs);
        if (data.SelectedTabId is not null && data.Tabs.All(t => !t.Id.Equals(data.SelectedTabId, StringComparison.OrdinalIgnoreCase)))
            data.SelectedTabId = null;
    }

    private static void ValidateNodes(IEnumerable<LauncherNode> source, HashSet<string> ids, HashSet<string> ancestors, string tabKind)
    {
        var nodes = source.ToList(); ValidateNames(nodes.Select(NodeLabel), "同一Group"); ValidateOrders(nodes.Select(x => x.Order), "同一Group");
        foreach (var node in nodes)
        {
            ValidateId(node.Id, ids); ValidateNode(node, tabKind);
            ValidateIcon(node.Icon, NodeLabel(node));
            if (node is GroupNode group)
            {
                if (!ancestors.Add(group.Id)) throw new InvalidDataException($"Group {group.Name} に循環があります。");
                ValidateNodes(group.Children, ids, ancestors, tabKind); ancestors.Remove(group.Id);
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
    private static void ValidateNode(LauncherNode node, string tabKind)
    {
        switch (node)
        {
            case GroupNode group: group.Name = Required(group.Name); break;
            case NamedLauncherItem item:
                item.Name = Required(item.Name);
                if (string.IsNullOrWhiteSpace(item.Target)) throw new InvalidDataException($"{item.Name}のtargetが空です。");
                if (item is UrlItem)
                {
                    if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new InvalidDataException($"{item.Name}のURLはHTTPまたはHTTPSで指定してください。");
                }
                else
                {
                    if (tabKind == LauncherTabKinds.Web) throw new InvalidDataException("WebランチャーにはURL以外を登録できません。");
                    if (!Path.IsPathFullyQualified(item.Target)) throw new InvalidDataException($"{item.Name}のtargetは絶対パスで指定してください。");
                }
                break;
            case DirectoryItem directory:
                if (tabKind == LauncherTabKinds.Web) throw new InvalidDataException("WebランチャーにはDirectory参照を登録できません。");
                if (string.IsNullOrWhiteSpace(directory.Target) || !Path.IsPathFullyQualified(directory.Target)) throw new InvalidDataException("Directory参照のtargetは絶対パスで指定してください。");
                break;
            default: throw new InvalidDataException("未対応のランチャー項目です。");
        }
    }
    public static string NodeLabel(LauncherNode node) => node switch { DirectoryItem directory => directory.Target, GroupNode group => group.Name, NamedLauncherItem item => item.Name, StoreAppItem store => store.Name, PresetItem preset => preset.Name, _ => "項目" };
    private static void ValidateDefaultIcons(DefaultIconSettings icons)
    {
        ValidateIcon(icons.GroupIcon, "Group既定"); ValidateIcon(icons.DirectoryIcon, "Directory既定"); ValidateIcon(icons.UrlIcon, "URL既定"); ValidateIcon(icons.TrayIcon, "トレイ既定");
    }
    private static void ValidateItemLaunch(ItemLaunchSettings settings)
    {
        if (settings.FileItemClickCount is not (1 or 2) || settings.DirectoryItemClickCount is not (1 or 2) || settings.UrlItemClickCount is not (1 or 2))
            throw new InvalidDataException("項目の起動クリック数は1または2で指定してください。");
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
        if (File.Exists(_paths.DefaultDataFile))
        {
            try
            {
                var data = Read(_paths.DefaultDataFile);
                Save(data);
                return new(data, DataSource.New);
            }
            catch (Exception ex) { throw new InvalidDataException($"{Path.GetFileName(_paths.DefaultDataFile)}を読み込めませんでした: {ex.Message}"); }
        }
        return new(CreateInitialData(), DataSource.New);
    }

    public void Save(OpenGepaData data)
    {
        _validator.Validate(data); Write(_paths.TemporaryFile, data); _ = Read(_paths.TemporaryFile);
        if (File.Exists(_paths.DataFile)) File.Replace(_paths.TemporaryFile, _paths.DataFile, _paths.BackupFile, true);
        else File.Move(_paths.TemporaryFile, _paths.DataFile);
    }

    /// <summary>UIの一時的な状態変更向け。既に読み込み済みのデータを再検証せず、同じ原子的置換で保存します。</summary>
    public void SaveWithoutValidation(OpenGepaData data)
    {
        Write(_paths.TemporaryFile, data);
        if (File.Exists(_paths.DataFile)) File.Replace(_paths.TemporaryFile, _paths.DataFile, _paths.BackupFile, true);
        else File.Move(_paths.TemporaryFile, _paths.DataFile);
    }

    public OpenGepaData Clone(OpenGepaData data) => Deserialize(JsonSerializer.Serialize(data, JsonOptions));
    public void MarkLastGood(OpenGepaData data) => WriteLastGood(data);
    public string Serialize(OpenGepaData data) => JsonSerializer.Serialize(data, JsonOptions);
    public OpenGepaData Deserialize(string json)
    {
        var data = JsonSerializer.Deserialize<OpenGepaData>(json, JsonOptions) ?? throw new InvalidDataException("JSONが空です。");
        Migrate(data); _validator.Validate(data); return data;
    }
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

    private static OpenGepaData CreateInitialData()
    {
        var tab = new LauncherTab { Name = "Launcher", Kind = LauncherTabKinds.Launcher, Order = 0 };
        var data = new OpenGepaData { SelectedTabId = tab.Id, Tabs = new ObservableCollection<LauncherTab> { tab } };
        BuiltInTabs.Ensure(data);
        return data;
    }

    private static void Migrate(OpenGepaData data)
    {
        if (data.FormatVersion == 1)
        {
            data.FormatVersion = OpenGepaData.CurrentFormatVersion;
            foreach (var tab in data.Tabs ?? []) if (string.IsNullOrWhiteSpace(tab.Kind)) tab.Kind = LauncherTabKinds.Launcher;
        }
        if (data.FormatVersion != OpenGepaData.CurrentFormatVersion) return;
        data.Tabs ??= [];
        data.WindowsMenu ??= new WindowsMenuSettings();
        data.Presets ??= new PresetSettings();
        data.Presets.HiddenItemIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in data.Tabs) if (string.IsNullOrWhiteSpace(tab.Kind)) tab.Kind = LauncherTabKinds.Launcher;
        BuiltInTabs.Ensure(data);
    }
}
