using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed record ThemeColors(
    string AppBackground, string PanelBackground, string Border,
    string TabBackground, string TabForeground,
    string GroupBackground, string GroupForeground,
    string ItemBackground, string ItemForeground,
    string SelectionBackground, string SelectionForeground, string MutedForeground,
    string ControlBackground, string ControlForeground,
    string DisabledBackground, string DisabledForeground,
    string HoverBackground, string PressedBackground,
    string ScrollTrack, string ScrollThumb, string ScrollThumbHover,
    string TabOverlayBackground, string TabOverlayForeground,
    string PinHoverBackground, string PinCheckedBackground, string PinCheckedHoverBackground, string PinCheckedBorder, string PinCheckedForeground);

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
    private static readonly ThemeColors Light = new(
        "#F7F8FA", "#FFFFFF", "#D9DDE5", "#E7ECF5", "#101828",
        "#F1F5F9", "#101828", "#FFFFFF", "#101828", "#CCE5FF", "#101828", "#667085",
        "#FFFFFF", "#101828", "#EEF0F3", "#8A93A3", "#E7ECF5", "#D8E2F0",
        "#FFF7F8FA", "#FFB8C0CC", "#FF8B96A6", "#B3FFFFFF", "#FF101828",
        "#FFD8E2F0", "#FF0B3D70", "#FF092F57", "#FF062E57", "#FFFFFFFF");
    private static readonly ThemeColors Dark = new(
        "#171A1F", "#202631", "#3B4658", "#2B3440", "#F8FAFC",
        "#2B3440", "#F8FAFC", "#202631", "#E5E7EB", "#365A7A", "#FFFFFF", "#AAB4C4",
        "#252C37", "#F3F4F6", "#1B2028", "#7F8998", "#344050", "#17202B",
        "#FF171C24", "#FF566273", "#FF748297", "#B3000000", "#FFFFFFFF",
        "#FF182737", "#FF062A4C", "#FF073962", "#FF45A2E8", "#FFFFFFFF");
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
        resources["ControlBackgroundBrush"] = Brush(colors.ControlBackground); resources["ControlForegroundBrush"] = Brush(colors.ControlForeground);
        resources["DisabledBackgroundBrush"] = Brush(colors.DisabledBackground); resources["DisabledForegroundBrush"] = Brush(colors.DisabledForeground);
        resources["HoverBackgroundBrush"] = Brush(colors.HoverBackground); resources["PressedBackgroundBrush"] = Brush(colors.PressedBackground);
        resources["ScrollTrackBrush"] = Brush(colors.ScrollTrack); resources["ScrollThumbBrush"] = Brush(colors.ScrollThumb); resources["ScrollThumbHoverBrush"] = Brush(colors.ScrollThumbHover);
        resources["TabOverlayBackgroundBrush"] = Brush(colors.TabOverlayBackground); resources["TabOverlayForegroundBrush"] = Brush(colors.TabOverlayForeground);
        resources["PinHoverBackgroundBrush"] = Brush(colors.PinHoverBackground); resources["PinCheckedBackgroundBrush"] = Brush(colors.PinCheckedBackground); resources["PinCheckedHoverBackgroundBrush"] = Brush(colors.PinCheckedHoverBackground); resources["PinCheckedBorderBrush"] = Brush(colors.PinCheckedBorder); resources["PinCheckedForegroundBrush"] = Brush(colors.PinCheckedForeground);
        foreach (Window window in System.Windows.Application.Current.Windows) ApplyWindowChrome(window, appearance);
    }
    public static void ApplyWindowChrome(Window window, AppearanceSettings appearance)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var isDark = appearance.Theme == "dark";
        var dark = isDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));

        // Windows 11: explicitly color the non-client area as well. Older systems simply
        // reject these attributes, in which case immersive dark mode above still applies.
        var caption = isDark ? ColorRef(Dark.PanelBackground) : unchecked((int)0xFFFFFFFF);
        var text = isDark ? ColorRef("#FFFFFF") : unchecked((int)0xFFFFFFFF);
        var border = isDark ? ColorRef(Dark.Border) : unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }
    private static int ColorRef(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        return color.R | (color.G << 8) | (color.B << 16);
    }
    private static SolidColorBrush Brush(string hex) { var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!; var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
