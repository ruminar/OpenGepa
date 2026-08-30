using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

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
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(fullPath); image.DecodePixelWidth = 32; image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
