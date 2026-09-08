using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

/// <summary>Windows Menuから通常ランチャーへ渡すショートカットのスナップショットです。</summary>
public sealed record WindowsMenuShortcutDragInfo(string Name, string ShortcutPath);

public static class WindowsMenuShortcutRegistrationRules
{
    public const string DragFormat = "OpenGepa.WindowsMenuShortcut";

    public static bool IsValidSource(WindowsMenuShortcutDragInfo? source) =>
        source is not null && NameRules.IsValid(source.Name, out _) &&
        Path.IsPathFullyQualified(source.ShortcutPath) &&
        Path.GetExtension(source.ShortcutPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public static bool TryCreateFileItem(WindowsMenuShortcutDragInfo? source, IEnumerable<LauncherNode> siblings, out FileItem? item)
    {
        item = null;
        if (!IsValidSource(source)) return false;
        var validSource = source!;

        item = new FileItem
        {
            Name = UrlRegistrationRules.UniqueName(validSource.Name, siblings),
            Target = validSource.ShortcutPath
        };
        return true;
    }
}
