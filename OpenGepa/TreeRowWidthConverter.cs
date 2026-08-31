using System.Globalization;
using System.Windows.Data;

namespace OpenGepa;

public sealed class TreeRowWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width) return 0d;
        var reserved = parameter is not null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
        return Math.Max(0, width - reserved);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
