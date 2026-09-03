using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using OpenGepa.Services;

namespace OpenGepa;

/// <summary>トレイと同じ優先順で、各Windowのタイトルバーアイコンを読み込みます。</summary>
public static class WindowIconService
{
    public static ImageSource Load(AppService app)
    {
        var relative = app.IconSetService.GetOpenGepaIcon();
        if (!string.IsNullOrWhiteSpace(relative))
        {
            try
            {
                var path = Path.Combine(app.Paths.BaseDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) return IconPathConverter.LoadImage(path, 32);
            }
            catch { }
        }
        return LoadEmbeddedApplicationIcon();
    }
    private static ImageSource LoadEmbeddedApplicationIcon()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenGepa.exe"));
            var source = Imaging.CreateBitmapSourceFromHIcon((icon ?? System.Drawing.SystemIcons.Application).Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        finally { icon?.Dispose(); }
    }
}
