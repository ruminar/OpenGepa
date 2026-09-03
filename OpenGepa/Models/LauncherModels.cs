using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace OpenGepa.Models;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class OpenGepaData : ObservableModel
{
    public const int CurrentFormatVersion = 2;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string? SelectedTabId { get; set; }
    public bool IsLauncherPinned { get; set; }
    public AppearanceSettings Appearance { get; set; } = new();
    public ItemLaunchSettings ItemLaunch { get; set; } = new();
    public DefaultIconSettings DefaultIcons { get; set; } = new();
    public WindowsMenuSettings WindowsMenu { get; set; } = new();
    public PresetSettings Presets { get; set; } = new();
    public ObservableCollection<LauncherTab> Tabs { get; set; } = [];
}

/// <summary>ローカル環境の Start Menu を操作できるかどうかを保持します。</summary>
public sealed class WindowsMenuSettings : ObservableModel
{
    private bool _allowCurrentUserEdit;
    private bool _allowAllUsersEdit;
    private bool _foldersFirst = true;
    public bool AllowCurrentUserEdit { get => _allowCurrentUserEdit; set => SetField(ref _allowCurrentUserEdit, value); }
    public bool AllowAllUsersEdit { get => _allowAllUsersEdit; set => SetField(ref _allowAllUsersEdit, value); }
    public bool FoldersFirst { get => _foldersFirst; set => SetField(ref _foldersFirst, value); }
}

/// <summary>固定プリセットの利用者ごとの非表示状態です。Profile には含めません。</summary>
public sealed class PresetSettings
{
    public HashSet<string> HiddenItemIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class LauncherTabKinds
{
    public const string Launcher = "launcher";
    public const string Web = "web";
    public const string WindowsMenu = "windowsMenu";
    public const string StoreApps = "storeApps";
    public const string Presets = "presets";
    public static bool IsKnown(string? kind) => kind is Launcher or Web or WindowsMenu or StoreApps or Presets;
    public static bool IsSystem(string? kind) => kind is WindowsMenu or StoreApps or Presets;
}

/// <summary>環境依存の3タブは常にローカル設定にのみ存在し、Profileへは送られません。</summary>
public static class BuiltInTabs
{
    public const string WindowsMenuId = "10000000-0000-0000-0000-000000000001";
    public const string StoreAppsId = "10000000-0000-0000-0000-000000000002";
    public const string PresetsId = "10000000-0000-0000-0000-000000000003";

    public static void Ensure(OpenGepaData data)
    {
        data.Tabs ??= [];
        var nextOrder = data.Tabs.Select(tab => tab.Order).DefaultIfEmpty(-1).Max() + 1;
        foreach (var (id, kind, name) in new[]
        {
            (WindowsMenuId, LauncherTabKinds.WindowsMenu, "Windows Menu"),
            (StoreAppsId, LauncherTabKinds.StoreApps, "ストアアプリ"),
            (PresetsId, LauncherTabKinds.Presets, "Windows主要操作")
        })
        {
            var (tab, created) = Ensure(data, id, kind, name);
            if (created) tab.Order = nextOrder++;
            tab.Children.Clear();
            tab.RuntimeChildren = null;
        }
    }

    private static (LauncherTab Tab, bool Created) Ensure(OpenGepaData data, string id, string kind, string name)
    {
        var sameKind = data.Tabs.FirstOrDefault(tab => tab.Kind == kind);
        if (sameKind is null)
        {
            sameKind = new LauncherTab { Id = id, Kind = kind, Name = name, IsVisible = true };
            data.Tabs.Add(sameKind);
            return (sameKind, true);
        }
        sameKind.Id = id;
        sameKind.Name = name;
        sameKind.Kind = kind;
        foreach (var duplicate in data.Tabs.Where(tab => tab != sameKind && tab.Kind == kind).ToList()) data.Tabs.Remove(duplicate);
        return (sameKind, false);
    }
}

public static class RuntimeNodeIds
{
    public static string Create(string key) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToString("D");
}

public sealed class ItemLaunchSettings : ObservableModel
{
    private int _fileItemClickCount = 1; private int _directoryItemClickCount = 2; private int _urlItemClickCount = 2;
    public int FileItemClickCount { get => _fileItemClickCount; set => SetField(ref _fileItemClickCount, value); }
    public int DirectoryItemClickCount { get => _directoryItemClickCount; set => SetField(ref _directoryItemClickCount, value); }
    public int UrlItemClickCount { get => _urlItemClickCount; set => SetField(ref _urlItemClickCount, value); }
    public int GetClickCount(LauncherNode node) => node switch { FileItem => FileItemClickCount, DirectoryItem => DirectoryItemClickCount, UrlItem => UrlItemClickCount, _ => 0 };
}

public sealed class DefaultIconSettings : ObservableModel
{
    private string? _groupIcon; private string? _directoryIcon; private string? _urlIcon; private string? _trayIcon;
    public string? GroupIcon { get => _groupIcon; set => SetField(ref _groupIcon, value); }
    public string? DirectoryIcon { get => _directoryIcon; set => SetField(ref _directoryIcon, value); }
    public string? UrlIcon { get => _urlIcon; set => SetField(ref _urlIcon, value); }
    public string? TrayIcon { get => _trayIcon; set => SetField(ref _trayIcon, value); }
}

public sealed class AppearanceSettings : ObservableModel
{
    private string _theme = "light"; private string _groupBackgroundColor = "#F1F5F9"; private string _groupForegroundColor = "#101828"; private string _launcherItemBackgroundColor = "#FFFFFF"; private string _launcherItemForegroundColor = "#101828";
    public string Theme { get => _theme; set => SetField(ref _theme, value); }
    public string GroupBackgroundColor { get => _groupBackgroundColor; set => SetField(ref _groupBackgroundColor, value); }
    public string GroupForegroundColor { get => _groupForegroundColor; set => SetField(ref _groupForegroundColor, value); }
    public string LauncherItemBackgroundColor { get => _launcherItemBackgroundColor; set => SetField(ref _launcherItemBackgroundColor, value); }
    public string LauncherItemForegroundColor { get => _launcherItemForegroundColor; set => SetField(ref _launcherItemForegroundColor, value); }
}

public sealed class LauncherTab : ObservableModel
{
    private string _name = "Launcher"; private string _kind = LauncherTabKinds.Launcher; private bool _isVisible = true; private int _order; private string? _icon;
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Kind { get => _kind; set => SetField(ref _kind, value); }
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }
    public int Order { get => _order; set => SetField(ref _order, value); }
    public string? Icon { get => _icon; set => SetField(ref _icon, value); }
    public ObservableCollection<LauncherNode> Children { get; set; } = [];
    /// <summary>特殊タブだけが使う、保存しない現在環境のノードです。</summary>
    [JsonIgnore] public ObservableCollection<LauncherNode>? RuntimeChildren { get; set; }
    [JsonIgnore] public bool IsSystemTab => LauncherTabKinds.IsSystem(Kind);
    [JsonIgnore] public bool IsWebTab => Kind == LauncherTabKinds.Web;
    [JsonIgnore] public ObservableCollection<LauncherNode> DisplayChildren => RuntimeChildren ?? Children;
    [JsonIgnore] public string DisplayGlyph => "▣";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GroupNode), "group")]
[JsonDerivedType(typeof(FileItem), "file")]
[JsonDerivedType(typeof(DirectoryItem), "directory")]
[JsonDerivedType(typeof(UrlItem), "url")]
[JsonDerivedType(typeof(WindowsMenuGroupNode), "runtimeWindowsGroup")]
[JsonDerivedType(typeof(WindowsMenuShortcutItem), "runtimeWindowsShortcut")]
[JsonDerivedType(typeof(StoreAppItem), "runtimeStoreApp")]
[JsonDerivedType(typeof(PresetItem), "runtimePreset")]
public abstract class LauncherNode : ObservableModel
{
    private int _order; private string? _icon;
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public int Order { get => _order; set => SetField(ref _order, value); }
    public string? Icon { get => _icon; set => SetField(ref _icon, value); }
    [JsonIgnore] public abstract string DisplayGlyph { get; }
}

public class GroupNode : LauncherNode
{
    private string _name = string.Empty;
    public string Name { get => _name; set => SetField(ref _name, value); }
    public ObservableCollection<LauncherNode> Children { get; set; } = [];
    public override string DisplayGlyph => "▸";
}

public abstract class NamedLauncherItem : LauncherNode
{
    private string _name = string.Empty;
    private string _target = string.Empty;
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Target { get => _target; set => SetField(ref _target, value); }
}

public class FileItem : NamedLauncherItem { private bool _isTargetMissing; public bool IsTargetMissing { get => _isTargetMissing; set => SetField(ref _isTargetMissing, value); } public override string DisplayGlyph => "▤"; }
public sealed class UrlItem : NamedLauncherItem { public override string DisplayGlyph => "◎"; }
public sealed class DirectoryItem : LauncherNode
{
    private string _target = string.Empty;
    public string Target { get => _target; set => SetField(ref _target, value); }
    public override string DisplayGlyph => "▰";
}

public enum WindowsMenuSource { CurrentUser, AllUsers }

/// <summary>Windows Menu のフォルダ。存在する実体を両方保持し、現在ユーザー用を優先します。</summary>
public sealed class WindowsMenuGroupNode : GroupNode
{
    [JsonIgnore] public string? CurrentUserPath { get; set; }
    [JsonIgnore] public string? AllUsersPath { get; set; }
    [JsonIgnore] public WindowsMenuSource PreferredSource => CurrentUserPath is not null ? WindowsMenuSource.CurrentUser : WindowsMenuSource.AllUsers;
    [JsonIgnore] public string? PreferredPath => CurrentUserPath ?? AllUsersPath;
}

/// <summary>Windows Menu の .lnk。Target は .lnk 自身の絶対パスです。</summary>
public sealed class WindowsMenuShortcutItem : FileItem
{
    [JsonIgnore] public WindowsMenuSource Source { get; set; }
    [JsonIgnore] public string RelativePath { get; set; } = string.Empty;
}

/// <summary>現在ログオン中の利用者に公開されているパッケージアプリです。</summary>
public sealed class StoreAppItem : LauncherNode
{
    private string _name = string.Empty;
    private string _aumid = string.Empty;
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Aumid { get => _aumid; set => SetField(ref _aumid, value); }
    [JsonIgnore] public string IconSource => $"shell:AppsFolder\\{Aumid}";
    public override string DisplayGlyph => "▤";
}

/// <summary>固定カタログから生成した主要操作です。</summary>
public sealed class PresetItem : LauncherNode
{
    private string _presetId = string.Empty;
    private string _name = string.Empty;
    public string PresetId { get => _presetId; set => SetField(ref _presetId, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    [JsonIgnore] public string? IconSource { get; set; }
    [JsonIgnore] public bool RequiresConfirmation { get; set; }
    public override string DisplayGlyph => "⚙";
}

public static class LauncherTabCopy
{
    public static LauncherTab Create(LauncherTab source, string name, int order) => new()
    {
        Name = name,
        Kind = source.Kind,
        Order = order,
        IsVisible = source.IsVisible,
        Icon = source.Icon,
        Children = new ObservableCollection<LauncherNode>(source.Children.Select(CopyNode))
    };

    private static LauncherNode CopyNode(LauncherNode source)
    {
        LauncherNode copy = source switch
        {
            GroupNode group => new GroupNode { Children = new ObservableCollection<LauncherNode>(group.Children.Select(CopyNode)) },
            FileItem => new FileItem(),
            DirectoryItem => new DirectoryItem(),
            UrlItem => new UrlItem(),
            _ => throw new InvalidDataException("未対応のランチャー項目です。")
        };
        if (copy is GroupNode copyGroup && source is GroupNode sourceGroup) copyGroup.Name = sourceGroup.Name;
        if (copy is NamedLauncherItem copyItem && source is NamedLauncherItem sourceItem) copyItem.Name = sourceItem.Name;
        copy.Order = source.Order;
        copy.Icon = source.Icon;
        if (copy is NamedLauncherItem item && source is NamedLauncherItem sourceTargetItem) item.Target = sourceTargetItem.Target;
        if (copy is FileItem copyFile && source is FileItem sourceFile) copyFile.IsTargetMissing = sourceFile.IsTargetMissing;
        if (copy is DirectoryItem directory && source is DirectoryItem sourceDirectory) directory.Target = sourceDirectory.Target;
        return copy;
    }
}
