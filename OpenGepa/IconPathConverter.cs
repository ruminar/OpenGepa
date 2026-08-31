using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
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
            if (!fullPath.StartsWith(Path.Combine(basePath, "icon") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;
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
        var relative = node.Icon ?? (System.Windows.Application.Current is App ? node switch
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
