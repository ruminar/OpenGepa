using OpenGepa.Models;
using OpenGepa.Services;

namespace OpenGepa;

public static class UrlRegistrationRules
{
    public static string UniqueDroppedName(Uri uri, IEnumerable<LauncherNode> siblings)
    {
        var used = siblings.Select(DataValidator.NodeLabel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        var candidates = new[] { uri.Host, uri.Host + path, uri.Host + path + uri.Query }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates) if (!used.Contains(NameRules.Normalize(candidate))) return candidate;
        var stem = uri.Host + path + uri.Query; for (var number = 1; ; number++) { var candidate = stem + "_" + number; if (!used.Contains(NameRules.Normalize(candidate))) return candidate; }
    }
    public static string UniqueName(string name, IEnumerable<LauncherNode> siblings, string? excludeId = null)
    {
        var used = siblings.Where(x => !x.Id.Equals(excludeId, StringComparison.OrdinalIgnoreCase)).Select(DataValidator.NodeLabel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        name = NameRules.Normalize(name); if (!used.Contains(name)) return name;
        for (var number = 1; ; number++) { var candidate = name + "_" + number; if (!used.Contains(candidate)) return candidate; }
    }
}
