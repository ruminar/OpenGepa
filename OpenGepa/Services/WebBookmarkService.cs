using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using OpenGepa.Models;

namespace OpenGepa.Services;

/// <summary>Edge / Chrome / Firefox が扱える Netscape Bookmark HTML を扱います。</summary>
public sealed class WebBookmarkService
{
    public const long MaxBytes = 16L * 1024 * 1024;
    public const int MaxNodes = 100_000;
    public const int MaxDepth = 256;

    public BookmarkImportResult Import(string path, IEnumerable<LauncherNode> existingTopLevel)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("ブックマークHTMLが見つかりません。", path);
        if (info.Length > MaxBytes) throw new InvalidDataException($"ブックマークHTMLは {MaxBytes / 1024 / 1024} MiB 以下にしてください。");
        string html;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true)) html = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(html) > MaxBytes * 2) throw new InvalidDataException("ブックマークHTMLの展開後サイズが大きすぎます。");
        var parsed = Parse(html);
        var imported = parsed.Nodes;
        PruneEmptyGroups(imported);
        if (imported.Count == 0) return new BookmarkImportResult(null, parsed.Skipped, []);
        var rootName = UniqueName(existingTopLevel, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        return new BookmarkImportResult(new GroupNode { Name = rootName, Children = imported }, parsed.Skipped, parsed.IconCandidates);
    }

    public void Export(string path, LauncherTab tab)
    {
        if (!tab.IsWebTab) throw new InvalidOperationException("WebランチャーだけをブックマークHTMLへ書き出せます。");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        writer.WriteLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        writer.WriteLine("<TITLE>Bookmarks</TITLE>");
        writer.WriteLine("<H1>Bookmarks</H1>");
        WriteList(writer, tab.Children, 0);
    }

    private static ParsedBookmarks Parse(string html)
    {
        if (!Regex.IsMatch(html, "<dl\\b", RegexOptions.IgnoreCase)) throw new InvalidDataException("ブックマークHTMLのDL構造が見つかりません。");
        var root = new ObservableCollection<LauncherNode>();
        var skipped = new List<SkippedBookmark>();
        var iconCandidates = new List<BookmarkIconCandidate>();
        var stack = new List<ObservableCollection<LauncherNode>> { root };
        GroupNode? pendingGroup = null;
        var nodes = 0;
        var tokenPattern = new Regex("<(?<tag>H3|A)\\b(?<attrs>[^>]*)>(?<content>.*?)</\\k<tag>\\s*>|<(?<open>DL)\\b[^>]*>|</DL\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match token in tokenPattern.Matches(html))
        {
            if (token.Groups["open"].Success)
            {
                if (pendingGroup is not null)
                {
                    if (stack.Count - 1 >= MaxDepth) throw new InvalidDataException($"ブックマークの階層は {MaxDepth} 階層以下にしてください。");
                    stack.Add(pendingGroup.Children); pendingGroup = null;
                }
                continue;
            }
            if (token.Value.StartsWith("</", StringComparison.Ordinal))
            {
                pendingGroup = null;
                if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var tag = token.Groups["tag"].Value;
            if (++nodes > MaxNodes) throw new InvalidDataException($"ブックマークの項目数は {MaxNodes:N0} 件以下にしてください。");
            var name = NormalizeHtmlText(token.Groups["content"].Value);
            if (tag.Equals("H3", StringComparison.OrdinalIgnoreCase))
            {
                var group = new GroupNode { Name = UniqueName(stack[^1], name), Order = stack[^1].Count };
                stack[^1].Add(group); pendingGroup = group;
                continue;
            }
            if (pendingGroup is not null) throw new InvalidDataException("ブックマークHTMLのGroup階層が不正です。");
            var href = ReadAttribute(token.Groups["attrs"].Value, "href");
            var rawUrl = WebUtility.HtmlDecode(href ?? string.Empty);
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                skipped.Add(new SkippedBookmark(name, rawUrl));
                pendingGroup = null;
                continue;
            }
            var item = new UrlItem { Name = UniqueName(stack[^1], name), Target = uri.AbsoluteUri, Order = stack[^1].Count };
            stack[^1].Add(item);
            iconCandidates.Add(new BookmarkIconCandidate(item.Id, item.Name, item.Target, ReadAttribute(token.Groups["attrs"].Value, "icon"), ReadAttribute(token.Groups["attrs"].Value, "icon_uri")));
            pendingGroup = null;
        }
        return new ParsedBookmarks(root, skipped, iconCandidates);
    }

    private static bool PruneEmptyGroups(ObservableCollection<LauncherNode> nodes)
    {
        foreach (var group in nodes.OfType<GroupNode>().ToList()) if (PruneEmptyGroups(group.Children)) nodes.Remove(group);
        return nodes.Count == 0;
    }

    private static string NormalizeHtmlText(string value)
    {
        var decoded = Regex.Replace(WebUtility.HtmlDecode(value), "<[^>]*>", string.Empty);
        decoded = Regex.Replace(decoded, "\\s+", " ").Trim();
        if (!NameRules.IsValid(decoded, out var error)) throw new InvalidDataException($"ブックマーク名が不正です: {error}");
        return NameRules.Normalize(decoded);
    }

    private static string? ReadAttribute(string source, string name)
    {
        var match = Regex.Match(source, $"\\b{Regex.Escape(name)}\\s*=\\s*(?:['\"](?<quoted>[^'\"]*)['\"]|(?<bare>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value) : null;
    }

    private static string UniqueName(IEnumerable<LauncherNode> siblings, string requested)
    {
        var normalized = NameRules.Normalize(requested);
        if (!NameRules.IsValid(normalized, out var error)) throw new InvalidDataException(error);
        var names = siblings.Select(DataValidator.NodeLabel).Select(NameRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(normalized)) return normalized;
        for (var index = 1; ; index++)
        {
            var candidate = $"{normalized}_{index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private static void WriteList(TextWriter writer, IEnumerable<LauncherNode> nodes, int depth)
    {
        var indent = new string(' ', depth * 4);
        writer.WriteLine(indent + "<DL><p>");
        foreach (var node in nodes.OrderBy(node => node.Order))
        {
            switch (node)
            {
                case GroupNode group:
                    writer.WriteLine($"{indent}    <DT><H3>{WebUtility.HtmlEncode(group.Name)}</H3>");
                    WriteList(writer, group.Children, depth + 1);
                    break;
                case UrlItem url:
                    if (!Uri.TryCreate(url.Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new InvalidDataException($"{url.Name} のURLが不正です。");
                    writer.WriteLine($"{indent}    <DT><A HREF=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{WebUtility.HtmlEncode(url.Name)}</A>");
                    break;
                default: throw new InvalidDataException("WebランチャーにはURL以外を登録できません。");
            }
        }
        writer.WriteLine(indent + "</DL><p>");
    }
}

public sealed record SkippedBookmark(string Name, string Url);
public sealed record BookmarkIconCandidate(string ItemId, string Name, string Url, string? EmbeddedIcon, string? IconUri);
internal sealed record ParsedBookmarks(ObservableCollection<LauncherNode> Nodes, IReadOnlyList<SkippedBookmark> Skipped, IReadOnlyList<BookmarkIconCandidate> IconCandidates);
public sealed record BookmarkImportResult(GroupNode? Root, IReadOnlyList<SkippedBookmark> Skipped, IReadOnlyList<BookmarkIconCandidate> IconCandidates)
{
    public int ImportedCount => Root is null ? 0 : CountUrls(Root.Children);
    private static int CountUrls(IEnumerable<LauncherNode> nodes) => nodes.Sum(node => node switch { UrlItem => 1, GroupNode group => CountUrls(group.Children), _ => 0 });
}
