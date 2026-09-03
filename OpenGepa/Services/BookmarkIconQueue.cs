using System.Text;
using System.Threading.Channels;
using OpenGepa.Models;

namespace OpenGepa.Services;

/// <summary>ブックマーク由来のアイコンを一件ずつ解決する、実行中だけのワーカーです。</summary>
public sealed class BookmarkIconQueue
{
    private const int MaxEmbeddedBytes = 1_048_576;
    private const int ResultBatchSize = 200;
    private readonly IconService _icons;
    private readonly SiteIconService _sites;
    private readonly Action<IReadOnlyList<BookmarkIconResult>> _completed;
    private readonly Channel<IconBatch> _batches = Channel.CreateUnbounded<IconBatch>(new UnboundedChannelOptions { SingleReader = true });

    public BookmarkIconQueue(IconService icons, SiteIconService sites, Action<IReadOnlyList<BookmarkIconResult>> completed)
    {
        _icons = icons; _sites = sites; _completed = completed; _ = Task.Run(ProcessAsync);
    }

    public void Enqueue(string tabId, IEnumerable<BookmarkIconCandidate> candidates, int webLimit = 200)
    {
        var jobs = candidates.GroupBy(item => (item.Url, item.IconUri, item.ReplaceExisting), StringTupleComparer.Instance).Select(group => new IconJob(tabId, group.First().Url, group.First().Name, group.Select(item => item.ItemId).ToList(), group.First().EmbeddedIcon, group.First().IconUri, group.First().ReplaceExisting)).ToList();
        if (jobs.Count > 0) _batches.Writer.TryWrite(new IconBatch(jobs, webLimit));
    }

    public void EnqueueMissing(string tabId, IEnumerable<UrlItem> items, int webLimit = 200) => Enqueue(tabId, items.Where(item => item.Icon is null).Select(item => new BookmarkIconCandidate(item.Id, item.Name, item.Target, null, null)), webLimit);

    private async Task ProcessAsync()
    {
        await foreach (var batch in _batches.Reader.ReadAllAsync())
        {
            var pending = new List<BookmarkIconResult>();
            foreach (var job in batch.Jobs)
            {
                var icon = TryImportEmbedded(job.EmbeddedIcon, job.Name);
                if (icon is null && batch.WebRemaining-- > 0) icon = (await _sites.TryFetchBookmarkIconAsync(job.Url, job.IconUri, job.Name)).IconPath;
                if (icon is not null) pending.AddRange(job.ItemIds.Select(id => new BookmarkIconResult(job.TabId, id, icon, job.ReplaceExisting)));
                if (pending.Count >= ResultBatchSize) { _completed(pending); pending = []; }
            }
            if (pending.Count > 0) _completed(pending);
        }
    }

    private string? TryImportEmbedded(string? value, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
            var comma = value.IndexOf(','); if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            var encoded = value[(comma + 1)..]; if (encoded.Length > MaxEmbeddedBytes * 2) return null;
            var bytes = Convert.FromBase64String(encoded); if (bytes.Length > MaxEmbeddedBytes) return null;
            using var stream = new MemoryStream(bytes); return _icons.ImportImage(stream, name);
        }
        catch { return null; }
    }

    private sealed record IconJob(string TabId, string Url, string Name, IReadOnlyList<string> ItemIds, string? EmbeddedIcon, string? IconUri, bool ReplaceExisting);
    private sealed class IconBatch(IReadOnlyList<IconJob> jobs, int webRemaining) { public IReadOnlyList<IconJob> Jobs { get; } = jobs; public int WebRemaining { get; set; } = webRemaining; }
}

public sealed record BookmarkIconResult(string TabId, string ItemId, string IconPath, bool ReplaceExisting);

file sealed class StringTupleComparer : IEqualityComparer<(string Url, string? IconUri, bool ReplaceExisting)>
{
    public static readonly StringTupleComparer Instance = new();
    public bool Equals((string Url, string? IconUri, bool ReplaceExisting) x, (string Url, string? IconUri, bool ReplaceExisting) y) =>
        x.ReplaceExisting == y.ReplaceExisting && string.Equals(x.Url, y.Url, StringComparison.OrdinalIgnoreCase) && string.Equals(x.IconUri, y.IconUri, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode((string Url, string? IconUri, bool ReplaceExisting) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Url), value.IconUri is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.IconUri), value.ReplaceExisting);
}
