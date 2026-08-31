using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public const int CurrentFormatVersion = 1;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string? SelectedTabId { get; set; }
    public bool IsLauncherPinned { get; set; }
    public AppearanceSettings Appearance { get; set; } = new();
    public DefaultIconSettings DefaultIcons { get; set; } = new();
    public ObservableCollection<LauncherTab> Tabs { get; set; } = [];
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
    private string _name = "Launcher"; private bool _isVisible = true; private int _order; private string? _icon;
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get => _name; set => SetField(ref _name, value); }
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }
    public int Order { get => _order; set => SetField(ref _order, value); }
    public string? Icon { get => _icon; set => SetField(ref _icon, value); }
    public ObservableCollection<LauncherNode> Children { get; set; } = [];
    [JsonIgnore] public string DisplayGlyph => "▣";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GroupNode), "group")]
[JsonDerivedType(typeof(FileItem), "file")]
[JsonDerivedType(typeof(DirectoryItem), "directory")]
[JsonDerivedType(typeof(UrlItem), "url")]
public abstract class LauncherNode : ObservableModel
{
    private int _order; private string? _icon;
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public int Order { get => _order; set => SetField(ref _order, value); }
    public string? Icon { get => _icon; set => SetField(ref _icon, value); }
    [JsonIgnore] public abstract string DisplayGlyph { get; }
}

public sealed class GroupNode : LauncherNode
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

public sealed class FileItem : NamedLauncherItem { public override string DisplayGlyph => "▤"; }
public sealed class UrlItem : NamedLauncherItem { public override string DisplayGlyph => "◎"; }
public sealed class DirectoryItem : LauncherNode
{
    private string _target = string.Empty;
    public string Target { get => _target; set => SetField(ref _target, value); }
    public override string DisplayGlyph => "▰";
}

public static class LauncherTabCopy
{
    public static LauncherTab Create(LauncherTab source, string name, int order) => new()
    {
        Name = name,
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
        if (copy is DirectoryItem directory && source is DirectoryItem sourceDirectory) directory.Target = sourceDirectory.Target;
        return copy;
    }
}
