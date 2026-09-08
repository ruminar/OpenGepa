using OpenGepa.Services;

namespace OpenGepa;

public static class ShortcutConversionRules
{
    public static string? DescribeLostSettings(ShortcutTargetDetails details)
    {
        var lost = new List<string>();
        if (!string.IsNullOrWhiteSpace(details.Arguments)) lost.Add($"起動引数: {details.Arguments}");
        var targetDirectory = Path.GetDirectoryName(details.Target);
        if (!string.IsNullOrWhiteSpace(details.WorkingDirectory) && !PathsEqual(details.WorkingDirectory, targetDirectory)) lost.Add($"作業フォルダ: {details.WorkingDirectory}");
        return lost.Count == 0 ? null : string.Join("\n", lost.Select(value => "- " + value));
    }

    private static bool PathsEqual(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
