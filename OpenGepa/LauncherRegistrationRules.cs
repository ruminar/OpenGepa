using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public enum LauncherRegistrationKind { File, Directory, Url, StoreApp }

/// <summary>システムタブまたは起動記録から通常ランチャーへ渡す、起動先のスナップショットです。</summary>
public sealed record LauncherRegistrationDragInfo(LauncherRegistrationKind Kind, string Name, string Target, string? Icon);

public static class LauncherRegistrationRules
{
    public const string DragFormat = "OpenGepa.LauncherRegistration";

    public static bool TryCreateDragInfo(LauncherNode? source, out LauncherRegistrationDragInfo? info)
    {
        info = null;
        if (source is UsageDisplayItem { IsAvailable: true, CurrentItem: not null } display) source = display.CurrentItem;
        info = source switch
        {
            FileItem file => new LauncherRegistrationDragInfo(LauncherRegistrationKind.File, file.Name, file.Target, file.Icon),
            DirectoryItem directory => new LauncherRegistrationDragInfo(LauncherRegistrationKind.Directory, directory.Target, directory.Target, directory.Icon),
            UrlItem url => new LauncherRegistrationDragInfo(LauncherRegistrationKind.Url, url.Name, url.Target, url.Icon),
            StoreAppItem store => new LauncherRegistrationDragInfo(LauncherRegistrationKind.StoreApp, store.Name, store.Aumid, store.Icon),
            _ => null
        };
        return info is not null && IsValidSource(info);
    }

    public static bool IsValidSource(LauncherRegistrationDragInfo? source)
    {
        if (source is null || !NameRules.IsValid(source.Name, out _)) return false;
        return source.Kind switch
        {
            LauncherRegistrationKind.File or LauncherRegistrationKind.Directory => Path.IsPathFullyQualified(source.Target),
            LauncherRegistrationKind.Url => Uri.TryCreate(source.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
            LauncherRegistrationKind.StoreApp => !string.IsNullOrWhiteSpace(source.Target) && source.Target.Contains('!'),
            _ => false
        };
    }

    public static bool TryCreateNode(LauncherRegistrationDragInfo? source, IEnumerable<LauncherNode> siblings, out LauncherNode? node)
    {
        node = null;
        if (!IsValidSource(source) || source!.Kind == LauncherRegistrationKind.StoreApp) return false;
        if (source.Kind == LauncherRegistrationKind.Directory && siblings.Any(item => NameRules.Normalize(DataValidator.NodeLabel(item)).Equals(NameRules.Normalize(source.Target), StringComparison.OrdinalIgnoreCase))) return false;
        var name = source.Kind == LauncherRegistrationKind.Directory ? source.Target : UrlRegistrationRules.UniqueName(source.Name, siblings);
        node = source.Kind switch
        {
            LauncherRegistrationKind.File => new FileItem { Name = name, Target = source.Target, Icon = source.Icon },
            LauncherRegistrationKind.Directory => new DirectoryItem { Target = source.Target, Icon = source.Icon },
            LauncherRegistrationKind.Url => new UrlItem { Name = name, Target = source.Target, Icon = source.Icon },
            _ => null
        };
        return node is not null;
    }
}
