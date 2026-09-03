using System.Windows.Media;
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
        return IconPathConverter.LoadImage(Path.Combine(AppContext.BaseDirectory, "Assets", "OpenGepa.ico"), 32);
    }
}
