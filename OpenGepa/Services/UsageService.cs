using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class UsageData
{
    public const int CurrentFormatVersion = 1;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public List<LaunchHistoryRecord> History { get; set; } = [];
    public List<UsageAggregate> Frequencies { get; set; } = [];
    public List<ExcludedLaunchTarget> Excluded { get; set; } = [];
}

public sealed class LaunchTargetIdentity
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? Source { get; set; }
    [JsonIgnore] public string Key => UsageIdentity.Key(this);
}

public sealed class LaunchHistoryRecord
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("D");
    public LaunchTargetIdentity Target { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class UsageAggregate
{
    public LaunchTargetIdentity Target { get; set; } = new();
    public long TotalCount { get; set; }
    public DateTimeOffset LastLaunchedAtUtc { get; set; }
    public List<DailyUsageCount> DailyCounts { get; set; } = [];
}

public sealed class DailyUsageCount
{
    public DateOnly Date { get; set; }
    public int Count { get; set; }
}

public sealed class ExcludedLaunchTarget
{
    public LaunchTargetIdentity Target { get; set; } = new();
    public string Name { get; set; } = string.Empty;
}

public sealed record UsageExclusionView(string Key, string Name, LauncherNode? CurrentItem, bool IsAvailable);

public static class UsageIdentity
{
    public const string Node = "node";
    public const string WindowsMenu = "windowsMenu";
    public const string StoreApp = "storeApp";
    public const string Preset = "preset";

    public static LaunchTargetIdentity? FromNode(LauncherNode node)
    {
        if (node is UsageDisplayItem display)
            return new LaunchTargetIdentity { Kind = display.TargetKind, Id = display.TargetId, Source = display.TargetSource };
        return node switch
        {
            WindowsMenuShortcutItem item => new LaunchTargetIdentity { Kind = WindowsMenu, Id = item.RelativePath, Source = item.Source.ToString() },
            StoreAppItem item => new LaunchTargetIdentity { Kind = StoreApp, Id = item.Aumid },
            PresetItem { RecordLaunch: true } item => new LaunchTargetIdentity { Kind = Preset, Id = item.PresetId },
            FileItem or DirectoryItem or UrlItem => new LaunchTargetIdentity { Kind = Node, Id = node.Id },
            _ => null
        };
    }

    public static string Key(LaunchTargetIdentity identity) => $"{identity.Kind}\u001f{identity.Source ?? string.Empty}\u001f{identity.Id}".ToUpperInvariant();

    public static bool IsValid(LaunchTargetIdentity? identity) => identity is not null && identity.Kind is Node or WindowsMenu or StoreApp or Preset && !string.IsNullOrWhiteSpace(identity.Id) && (identity.Kind != WindowsMenu || Enum.TryParse<WindowsMenuSource>(identity.Source, out _));
}

public sealed class UsageStore
{
    private readonly AppPaths _paths;
    public JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public UsageStore(AppPaths paths) => _paths = paths;

    public UsageData Load()
    {
        if (!File.Exists(_paths.UsageFile)) return new UsageData();
        try { return Deserialize(File.ReadAllText(_paths.UsageFile, Encoding.UTF8)); }
        catch { return new UsageData(); }
    }

    public UsageData Clone(UsageData data) => Deserialize(JsonSerializer.Serialize(data, JsonOptions));

    public void Save(UsageData data)
    {
        Validate(data);
        using (var stream = new FileStream(_paths.UsageTemporaryFile, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(data, JsonOptions)); writer.Flush(); stream.Flush(true);
        }
        _ = Deserialize(File.ReadAllText(_paths.UsageTemporaryFile, Encoding.UTF8));
        if (File.Exists(_paths.UsageFile)) File.Replace(_paths.UsageTemporaryFile, _paths.UsageFile, null, true); else File.Move(_paths.UsageTemporaryFile, _paths.UsageFile);
    }

    private UsageData Deserialize(string json)
    {
        var data = JsonSerializer.Deserialize<UsageData>(json, JsonOptions) ?? throw new InvalidDataException("起動記録データが空です。");
        Validate(data); return data;
    }

    private static void Validate(UsageData data)
    {
        if (data.FormatVersion != UsageData.CurrentFormatVersion) throw new InvalidDataException($"未対応の起動記録formatVersionです: {data.FormatVersion}");
        data.History ??= []; data.Frequencies ??= []; data.Excluded ??= [];
        if (data.History.Count > 1_000) throw new InvalidDataException("起動履歴が上限を超えています。");
        if (data.History.Any(item => !Guid.TryParse(item.EventId, out _) || !UsageIdentity.IsValid(item.Target) || string.IsNullOrWhiteSpace(item.Name))) throw new InvalidDataException("起動履歴が不正です。");
        if (data.Frequencies.Any(item => !UsageIdentity.IsValid(item.Target) || item.TotalCount < 0 || item.DailyCounts is null || item.DailyCounts.Any(day => day.Count < 0))) throw new InvalidDataException("使用頻度が不正です。");
        if (data.Excluded.Any(item => !UsageIdentity.IsValid(item.Target) || string.IsNullOrWhiteSpace(item.Name))) throw new InvalidDataException("起動記録の除外設定が不正です。");
        if (data.Frequencies.Select(item => item.Target.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Frequencies.Count || data.Excluded.Select(item => item.Target.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Excluded.Count) throw new InvalidDataException("起動記録の識別子が重複しています。");
    }
}

public sealed class UsageSaveQueue : IDisposable
{
    private readonly UsageStore _store;
    private readonly Channel<SaveRequest> _requests = Channel.CreateUnbounded<SaveRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private bool _disposed;
    public Exception? LastError { get; private set; }
    public UsageSaveQueue(UsageStore store) { _store = store; _worker = Task.Run(WriteLoopAsync); }
    public void RequestSave(UsageData snapshot) { ThrowIfDisposed(); if (!_requests.Writer.TryWrite(new(snapshot, null))) throw new InvalidOperationException("起動記録保存キューを追加できませんでした。"); }
    public Task SaveNowAsync(UsageData snapshot) { ThrowIfDisposed(); var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); if (!_requests.Writer.TryWrite(new(snapshot, completion))) throw new InvalidOperationException("起動記録保存キューを追加できませんでした。"); return completion.Task; }
    public void Flush() { ThrowIfDisposed(); var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); if (!_requests.Writer.TryWrite(new(null, completion))) throw new InvalidOperationException("起動記録保存キューを終了できませんでした。"); completion.Task.GetAwaiter().GetResult(); }
    private async Task WriteLoopAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync())
        {
            try { if (request.Snapshot is not null) _store.Save(request.Snapshot); LastError = null; request.Completion?.TrySetResult(); }
            catch (Exception ex) { LastError = ex; request.Completion?.TrySetException(ex); }
        }
    }
    public void Dispose() { if (_disposed) return; Flush(); _disposed = true; _requests.Writer.TryComplete(); _worker.GetAwaiter().GetResult(); }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(UsageSaveQueue)); }
    private sealed record SaveRequest(UsageData? Snapshot, TaskCompletionSource? Completion);
}

public sealed class UsageService : IDisposable
{
    private readonly UsageStore _store;
    private readonly UsageSaveQueue _queue;
    private readonly Func<LaunchTargetIdentity, LauncherNode?> _resolve;
    private readonly object _gate = new();
    public UsageData Data { get; private set; }
    public event EventHandler? Changed;

    public UsageService(AppPaths paths, Func<LaunchTargetIdentity, LauncherNode?> resolve)
    {
        _store = new UsageStore(paths); _queue = new UsageSaveQueue(_store); _resolve = resolve; Data = _store.Load();
    }

    public void RecordSuccessfulLaunch(LauncherNode source)
    {
        var node = source is UsageDisplayItem display ? display.CurrentItem : source;
        if (node is null || UsageIdentity.FromNode(node) is not { } identity) return;
        lock (_gate)
        {
            if (Data.Excluded.Any(item => item.Target.Key == identity.Key)) return;
            var candidate = _store.Clone(Data); var now = DateTimeOffset.UtcNow; var today = DateOnly.FromDateTime(DateTime.Now);
            candidate.History.Insert(0, new LaunchHistoryRecord { Target = identity, Name = DataValidator.NodeLabel(node), Icon = node.Icon, OccurredAtUtc = now });
            if (candidate.History.Count > 1_000) candidate.History.RemoveRange(1_000, candidate.History.Count - 1_000);
            var aggregate = candidate.Frequencies.FirstOrDefault(item => item.Target.Key == identity.Key);
            if (aggregate is null) { aggregate = new UsageAggregate { Target = identity }; candidate.Frequencies.Add(aggregate); }
            aggregate.TotalCount++; aggregate.LastLaunchedAtUtc = now;
            var daily = aggregate.DailyCounts.FirstOrDefault(item => item.Date == today);
            if (daily is null) aggregate.DailyCounts.Add(new DailyUsageCount { Date = today, Count = 1 }); else daily.Count++;
            PruneOldDailyCounts(candidate, today); Data = candidate; _queue.RequestSave(_store.Clone(candidate));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryExclude(LauncherNode source, out string error)
    {
        var node = source is UsageDisplayItem display ? display.CurrentItem : source;
        if (node is null || UsageIdentity.FromNode(node) is not { } identity) { error = "この項目は起動記録の対象ではありません。"; return false; }
        return TryChange(candidate =>
        {
            if (candidate.Excluded.All(item => item.Target.Key != identity.Key)) candidate.Excluded.Add(new ExcludedLaunchTarget { Target = identity, Name = DataValidator.NodeLabel(node) });
            candidate.History.RemoveAll(item => item.Target.Key == identity.Key); candidate.Frequencies.RemoveAll(item => item.Target.Key == identity.Key);
        }, out error);
    }

    public bool TryRemoveExclusion(string key, out string error) => TryChange(candidate => candidate.Excluded.RemoveAll(item => item.Target.Key == key), out error);
    public bool TryClearHistory(out string error) => TryChange(candidate => candidate.History.Clear(), out error);
    public bool TryClearFrequency(out string error) => TryChange(candidate => candidate.Frequencies.Clear(), out error);
    public bool TryClearAll(out string error) => TryChange(candidate => { candidate.History.Clear(); candidate.Frequencies.Clear(); }, out error);

    private bool TryChange(Action<UsageData> change, out string error)
    {
        try
        {
            lock (_gate) { var candidate = _store.Clone(Data); change(candidate); _queue.SaveNowAsync(candidate).GetAwaiter().GetResult(); Data = candidate; }
            Changed?.Invoke(this, EventArgs.Empty); error = string.Empty; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public ObservableCollection<LauncherNode> BuildHistory()
    {
        List<LaunchHistoryRecord> records; lock (_gate) records = Data.History.OrderByDescending(item => item.OccurredAtUtc).ToList();
        var result = new ObservableCollection<LauncherNode>(); var today = DateOnly.FromDateTime(DateTime.Now); var groupOrder = 0;
        foreach (var dateGroup in records.GroupBy(item => DateOnly.FromDateTime(item.OccurredAtUtc.ToLocalTime().DateTime)))
        {
            var label = dateGroup.Key == today ? "今日" : dateGroup.Key == today.AddDays(-1) ? "昨日" : dateGroup.Key.ToString("yyyy年M月d日");
            var group = new GroupNode { Id = RuntimeNodeIds.Create("history-group:" + dateGroup.Key.ToString("O")), Name = label, Order = groupOrder++ };
            foreach (var targetGroup in dateGroup.GroupBy(record => record.Target.Key).OrderByDescending(records => records.Max(record => record.OccurredAtUtc)))
            {
                var latest = targetGroup.OrderByDescending(record => record.OccurredAtUtc).First(); var current = _resolve(latest.Target); var available = current is not null;
                var time = latest.OccurredAtUtc.ToLocalTime().ToString("HH:mm"); var count = targetGroup.Count();
                group.Children.Add(new UsageDisplayItem { Id = latest.EventId, TargetKind = latest.Target.Kind, TargetId = latest.Target.Id, TargetSource = latest.Target.Source, UsageKey = latest.Target.Key, Name = available ? DataValidator.NodeLabel(current!) : latest.Name, Icon = available ? current!.Icon : latest.Icon, CurrentItem = current, IsAvailable = available, Detail = $"最終 {time}  {count:N0}回", TimeDetail = $"最終 {time}", CountDetail = $"{count:N0}回", StatusDetail = available ? null : "利用不可", Order = group.Children.Count });
            }
            result.Add(group);
        }
        return result;
    }

    public ObservableCollection<LauncherNode> BuildFrequency(string period)
    {
        List<UsageAggregate> aggregates;
        lock (_gate)
        {
            if (PruneOldDailyCounts(Data, DateOnly.FromDateTime(DateTime.Now))) _queue.RequestSave(_store.Clone(Data));
            aggregates = _store.Clone(Data).Frequencies;
        }
        var today = DateOnly.FromDateTime(DateTime.Now); var from = today.AddDays(-29);
        var rows = aggregates.Select(item => (Item: item, Current: _resolve(item.Target), Count: period == UsagePeriods.AllTime ? item.TotalCount : item.DailyCounts.Where(day => day.Date >= from && day.Date <= today).Sum(day => (long)day.Count)))
            .Where(row => row.Current is not null && row.Count > 0)
            .OrderByDescending(row => row.Count).ThenByDescending(row => row.Item.LastLaunchedAtUtc).ThenBy(row => DataValidator.NodeLabel(row.Current!), StringComparer.OrdinalIgnoreCase).ToList();
        return new ObservableCollection<LauncherNode>(rows.Select((row, index) => new UsageDisplayItem { Id = RuntimeNodeIds.Create($"frequency:{row.Item.Target.Key}"), TargetKind = row.Item.Target.Kind, TargetId = row.Item.Target.Id, TargetSource = row.Item.Target.Source, UsageKey = row.Item.Target.Key, Name = DataValidator.NodeLabel(row.Current!), Icon = row.Current!.Icon, CurrentItem = row.Current, IsAvailable = true, Detail = $"{row.Count:N0}回", CountDetail = $"{row.Count:N0}回", Order = index }));
    }

    public IReadOnlyList<UsageExclusionView> GetExclusions()
    {
        lock (_gate) return Data.Excluded.Select(item => { var current = _resolve(item.Target); return new UsageExclusionView(item.Target.Key, current is null ? item.Name : DataValidator.NodeLabel(current), current, current is not null); }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool PruneOldDailyCounts(UsageData data, DateOnly today)
    {
        var minimum = today.AddDays(-29); var removed = 0;
        foreach (var item in data.Frequencies) removed += item.DailyCounts.RemoveAll(day => day.Date < minimum);
        return removed > 0;
    }

    public void Flush() => _queue.Flush();
    public void Dispose() => _queue.Dispose();
}
