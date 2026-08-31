using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed record ThemeColors(string AppBackground, string PanelBackground, string Border, string TabBackground, string TabForeground, string GroupBackground, string GroupForeground, string ItemBackground, string ItemForeground, string SelectionBackground, string SelectionForeground, string MutedForeground);

public static class AppearanceRules
{
    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    public static void Validate(AppearanceSettings? appearance)
    {
        if (appearance is null) throw new InvalidDataException("外観設定がありません。");
        appearance.Theme = appearance.Theme?.Trim().ToLowerInvariant() ?? "";
        if (appearance.Theme is not ("light" or "dark" or "custom")) throw new InvalidDataException("テーマはlight、dark、customのいずれかで指定してください。");
        appearance.GroupBackgroundColor = NormalizeColor(appearance.GroupBackgroundColor, "Group背景色");
        appearance.GroupForegroundColor = NormalizeColor(appearance.GroupForegroundColor, "Group文字色");
        appearance.LauncherItemBackgroundColor = NormalizeColor(appearance.LauncherItemBackgroundColor, "項目背景色");
        appearance.LauncherItemForegroundColor = NormalizeColor(appearance.LauncherItemForegroundColor, "項目文字色");
    }
    private static string NormalizeColor(string? value, string label)
    {
        var color = value?.Trim() ?? "";
        if (!ColorPattern.IsMatch(color)) throw new InvalidDataException($"{label}は#RRGGBB形式で指定してください。");
        return color.ToUpperInvariant();
    }
}

public static class ThemePalette
{
    private static readonly ThemeColors Light = new("#F7F8FA", "#FFFFFF", "#D9DDE5", "#E7ECF5", "#101828", "#F1F5F9", "#101828", "#FFFFFF", "#101828", "#CCE5FF", "#101828", "#667085");
    private static readonly ThemeColors Dark = new("#171A1F", "#202631", "#3B4658", "#2B3440", "#F8FAFC", "#2B3440", "#F8FAFC", "#202631", "#E5E7EB", "#365A7A", "#FFFFFF", "#AAB4C4");
    public static ThemeColors Resolve(AppearanceSettings appearance)
    {
        if (appearance.Theme == "dark") return Dark;
        if (appearance.Theme != "custom") return Light;
        return Light with { GroupBackground = appearance.GroupBackgroundColor, GroupForeground = appearance.GroupForegroundColor, ItemBackground = appearance.LauncherItemBackgroundColor, ItemForeground = appearance.LauncherItemForegroundColor };
    }
    public static void Apply(AppearanceSettings appearance)
    {
        if (System.Windows.Application.Current is null) return;
        var colors = Resolve(appearance); var resources = System.Windows.Application.Current.Resources;
        resources["AppBackgroundBrush"] = Brush(colors.AppBackground); resources["PanelBackgroundBrush"] = Brush(colors.PanelBackground); resources["BorderBrush"] = Brush(colors.Border);
        resources["TabBackgroundBrush"] = Brush(colors.TabBackground); resources["TabForegroundBrush"] = Brush(colors.TabForeground); resources["GroupBackgroundBrush"] = Brush(colors.GroupBackground); resources["GroupForegroundBrush"] = Brush(colors.GroupForeground);
        resources["ItemBackgroundBrush"] = Brush(colors.ItemBackground); resources["ItemForegroundBrush"] = Brush(colors.ItemForeground); resources["SelectionBackgroundBrush"] = Brush(colors.SelectionBackground); resources["SelectionForegroundBrush"] = Brush(colors.SelectionForeground); resources["MutedForegroundBrush"] = Brush(colors.MutedForeground);
    }
    private static SolidColorBrush Brush(string hex) { var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!; var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
