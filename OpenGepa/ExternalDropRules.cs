using System.Text;

namespace OpenGepa;

/// <summary>エクスプローラーと主要ブラウザからの外部ドロップを読み取ります。</summary>
public static class ExternalDropRules
{
    public static bool TryGetUrl(System.Windows.IDataObject data, out string url)
    {
        foreach (var format in new[] { System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text, "text/uri-list", "UniformResourceLocator", "UniformResourceLocatorW" })
        {
            if (!data.GetDataPresent(format)) continue;
            var value = data.GetData(format); var text = value as string;
            if (text is null && value is Stream stream)
            {
                var position = stream.CanSeek ? stream.Position : -1;
                try
                {
                    using var copy = new MemoryStream(); stream.CopyTo(copy);
                    text = format == "UniformResourceLocatorW" ? Encoding.Unicode.GetString(copy.ToArray()) : Encoding.UTF8.GetString(copy.ToArray());
                }
                finally { if (position >= 0) stream.Position = position; }
            }
            var parsed = ExtractUrlFromText(text);
            if (parsed is not null) { url = parsed; return true; }
        }
        url = ""; return false;
    }

    public static string? ExtractUrlFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => !x.StartsWith('#')))
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
        return null;
    }
}
