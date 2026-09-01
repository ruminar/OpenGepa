using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using OpenGepa.Models;

namespace OpenGepa;

public sealed class IconPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string relative || string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var basePath = Path.GetFullPath(AppContext.BaseDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(basePath, relative));
            var iconRoot = Path.Combine(basePath, "icon") + Path.DirectorySeparatorChar;
            var iconSetRoot = Path.Combine(basePath, "iconSet") + Path.DirectorySeparatorChar;
            if ((!fullPath.StartsWith(iconRoot, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(iconSetRoot, StringComparison.OrdinalIgnoreCase)) || !File.Exists(fullPath)) return null;
            var decodeWidth = parameter is not null && int.TryParse(parameter.ToString(), out var requested) ? requested : 32;
            return LoadImage(fullPath, decodeWidth);
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static BitmapImage LoadImage(string fullPath, int decodeWidth)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(fullPath); image.DecodePixelWidth = Math.Clamp(decodeWidth, 1, 256); image.EndInit(); image.Freeze(); return image;
    }
}

public sealed class NodeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LauncherNode node) return null;
        if (node is FileItem { IsTargetMissing: true }) { var image = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Error.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32)); image.Freeze(); return image; }
        var relative = node.Icon ?? (System.Windows.Application.Current is App ? App.Services.IconSetService.GetDefaultNodeIcon(node) ?? node switch
        {
            GroupNode => App.Services.Data.DefaultIcons.GroupIcon,
            DirectoryItem => App.Services.Data.DefaultIcons.DirectoryIcon,
            UrlItem => App.Services.Data.DefaultIcons.UrlIcon,
            _ => null
        } : null);
        return new IconPathConverter().Convert(relative, targetType, parameter, culture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TabIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LauncherTab tab) return null;
        var icon = tab.Icon ?? (System.Windows.Application.Current is App ? App.Services.IconSetService.GetAppIcon(tab, App.Services.Data.Tabs) : null);
        return new IconPathConverter().Convert(icon, targetType, parameter, culture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
