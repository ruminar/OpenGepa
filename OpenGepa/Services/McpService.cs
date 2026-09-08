using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using OpenGepa.McpProtocol;
using OpenGepa.Models;

namespace OpenGepa.Services;

public sealed class McpCoordinator
{
    private readonly AppService _app;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private long _generation;

    public McpCoordinator(AppService app)
    {
        _app = app;
        _app.DataChanged += (_, _) => Interlocked.Increment(ref _generation);
        _app.EnvironmentDataChanged += (_, _) => Interlocked.Increment(ref _generation);
        _app.UsageDataChanged += (_, _) => Interlocked.Increment(ref _generation);
    }

    public void EndSession(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId)) _sessions.TryRemove(sessionId, out _);
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) return IpcResponse.Fail("invalid_request", "MCPセッション識別子がありません。");
        if (!_app.Data.Mcp.Enabled) return IpcResponse.Ok(new { status = "disabled" });
        var session = _sessions.GetOrAdd(request.SessionId, _ => new McpSession());
        try
        {
            return request.Operation switch
            {
                "list_tabs" => IpcResponse.Ok(ListTabs(session, GetBool(request.Payload, "include_hidden"))),
                "browse_items" => IpcResponse.Ok(BrowseItems(session, request.Payload)),
                "search_items" => IpcResponse.Ok(SearchItems(session, request.Payload)),
                "get_item" => IpcResponse.Ok(GetItem(session, RequiredString(request.Payload, "ref"), GetBool(request.Payload, "include_target"))),
                "launch_item" => IpcResponse.Ok(await LaunchItemAsync(session, RequiredString(request.Payload, "ref"), cancellationToken)),
                "get_recent_items" => IpcResponse.Ok(GetRecentItems(session, GetLimit(request.Payload, 20))),
                "get_frequent_items" => IpcResponse.Ok(GetFrequentItems(session, request.Payload)),
                "update_descriptions" => IpcResponse.Ok(UpdateDescriptions(session, request.Payload)),
                _ => IpcResponse.Fail("invalid_request", "未対応のMCP操作です。")
            };
        }
        catch (McpRequestException ex) { return IpcResponse.Fail(ex.Code, ex.Message); }
        catch (Exception ex) { return IpcResponse.Fail("failed", ex.Message); }
    }

    private object ListTabs(McpSession session, bool includeHidden)
    {
        var tabs = _app.Data.Tabs.OrderBy(tab => tab.Order).Where(tab => includeHidden || tab.IsVisible).Select(tab => new
        {
            @ref = session.RefFor(new McpTarget(tab.Id, null)),
            name = tab.Name,
            tab_type = tab.Kind,
            visible = tab.IsVisible,
            order = tab.Order,
            description = tab.IsSystemTab ? SystemTabDescription(tab.Kind) : tab.Description,
            describable = !tab.IsSystemTab,
            browsable = tab.Kind is not (LauncherTabKinds.History or LauncherTabKinds.Frequency)
        }).ToList();
        return new { items = tabs };
    }

    private object BrowseItems(McpSession session, JsonElement payload)
    {
        var parentRef = RequiredString(payload, "parent_ref");
        var target = session.RequireTarget(parentRef);
        var (tab, children) = ResolveChildren(target);
        if (tab.Kind is LauncherTabKinds.History or LauncherTabKinds.Frequency) throw new McpRequestException("not_allowed", "起動履歴と使用頻度は専用Toolで参照してください。");
        var signature = $"browse:{parentRef}:{GetLimit(payload, 50)}";
        var page = Page(session, "browse_items", signature, children.ToList(), payload, 50);
        return new { items = page.Items.Select(node => ItemSummary(session, tab, node)).ToList(), next_cursor = page.NextCursor };
    }

    private object SearchItems(McpSession session, JsonElement payload)
    {
        var query = RequiredString(payload, "query").Trim();
        if (query.Length == 0) throw new McpRequestException("invalid_request", "検索文字列が空です。");
        var includeHidden = GetBool(payload, "include_hidden");
        var scopes = GetStrings(payload, "scopes");
        var scopeSet = scopes.Count == 0 ? null : scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = GetLimit(payload, 30);
        var matches = new List<(LauncherTab Tab, LauncherNode Node, int Rank, int Sequence)>();
        var sequence = 0;
        foreach (var tab in _app.Data.Tabs.OrderBy(tab => tab.Order))
        {
            if ((!includeHidden && !tab.IsVisible) || tab.Kind is LauncherTabKinds.History or LauncherTabKinds.Frequency || !InScope(tab, scopeSet)) continue;
            foreach (var node in Walk(_app.GetDisplayChildren(tab)))
            {
                var name = McpName(node); var description = NodeDescription(node);
                var rank = MatchRank(name, description, query);
                if (rank < int.MaxValue) matches.Add((tab, node, rank, sequence));
                sequence++;
            }
        }
        var ordered = matches.OrderBy(row => row.Rank).ThenBy(row => row.Sequence).Select(row => (row.Tab, row.Node)).ToList();
        var signature = $"search:{query}:{includeHidden}:{string.Join(',', scopes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}:{limit}";
        var page = Page(session, "search_items", signature, ordered, payload, 30);
        return new { items = page.Items.Select(row => ItemSummary(session, row.Tab, row.Node)).ToList(), next_cursor = page.NextCursor };
    }

    private object GetItem(McpSession session, string reference, bool includeTarget)
    {
        var target = session.RequireTarget(reference);
        if (target.NodeId is null)
        {
            var tab = FindTab(target.TabId) ?? throw Expire(session, reference);
            return new { @ref = reference, kind = "tab", name = tab.Name, description = tab.IsSystemTab ? SystemTabDescription(tab.Kind) : tab.Description, tab_name = tab.Name, available = true, launchable = false, describable = !tab.IsSystemTab };
        }
        var (resolvedTab, node) = ResolveNode(target);
        if (node is null) throw Expire(session, reference);
        var summary = ItemSummary(session, resolvedTab, node);
        object? actualTarget = includeTarget && !resolvedTab.IsSystemTab ? node switch
        {
            FileItem file => new { kind = "file", value = file.Target },
            DirectoryItem directory => new { kind = "directory", value = directory.Target },
            UrlItem url => new { kind = "url", value = url.Target },
            _ => null
        } : null;
        return new { summary.Ref, summary.Kind, summary.Name, summary.Description, summary.TabName, summary.Available, summary.Launchable, summary.Describable, target = actualTarget };
    }

    private async Task<object> LaunchItemAsync(McpSession session, string reference, CancellationToken cancellationToken)
    {
        if (!_app.Data.Mcp.AllowLaunch) return new { status = "disabled" };
        var target = session.RequireTarget(reference);
        if (target.NodeId is null) return new { status = "not_allowed" };
        var (_, node) = ResolveNode(target);
        if (node is null) throw Expire(session, reference);
        if (node is PresetItem { AllowMcp: false } || node is GroupNode or SeparatorItem or UsageDisplayItem) return new { status = "not_allowed" };
        if (!IsAvailable(node)) return new { status = "unavailable" };
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _app.LaunchService.LaunchAsync(node, waitForUsageSave: true);
        return result.Success
            ? new { status = "launched", usage_warning = result.UsageError }
            : new { status = IsAvailable(node) ? "failed" : "unavailable", message = result.Error };
    }

    private object GetRecentItems(McpSession session, int limit)
    {
        var records = _app.UsageService.Snapshot().History.OrderByDescending(item => item.OccurredAtUtc).Take(limit).Select(record =>
        {
            var node = _app.ResolveUsageTargetForMcp(record.Target);
            var reference = node is null ? null : RefForResolvedNode(session, node);
            return new { @ref = reference, name = node is null ? record.Name : McpName(node), description = node is null ? null : NodeDescription(node), launched_at = record.OccurredAtUtc, available = node is not null };
        }).ToList();
        return new { items = records };
    }

    private object GetFrequentItems(McpSession session, JsonElement payload)
    {
        var period = GetString(payload, "period") ?? "30d";
        if (period is not ("30d" or "all")) throw new McpRequestException("invalid_request", "periodは30dまたはallで指定してください。");
        var limit = GetLimit(payload, 20); var today = DateOnly.FromDateTime(DateTime.Now); var from = today.AddDays(-29);
        var rows = _app.UsageService.Snapshot().Frequencies.Select(item => new
        {
            Item = item,
            Node = _app.ResolveUsageTargetForMcp(item.Target),
            Count = period == "all" ? item.TotalCount : item.DailyCounts.Where(day => day.Date >= from && day.Date <= today).Sum(day => (long)day.Count)
        }).Where(row => row.Node is not null && row.Count > 0).OrderByDescending(row => row.Count).ThenByDescending(row => row.Item.LastLaunchedAtUtc).ThenBy(row => McpName(row.Node!), StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(row => new { @ref = RefForResolvedNode(session, row.Node!), name = McpName(row.Node!), description = NodeDescription(row.Node!), count = row.Count, last_launched_at = row.Item.LastLaunchedAtUtc }).ToList();
        return new { items = rows };
    }

    private object UpdateDescriptions(McpSession session, JsonElement payload)
    {
        if (!_app.Data.Mcp.AllowDescriptionEdit) return new { status = "disabled" };
        if (!payload.TryGetProperty("updates", out var updatesElement) || updatesElement.ValueKind != JsonValueKind.Array) throw new McpRequestException("invalid_request", "updatesを配列で指定してください。");
        var updates = updatesElement.EnumerateArray().ToList();
        if (updates.Count is < 1 or > 100) throw new McpRequestException("invalid_request", "updatesは1件以上100件以下で指定してください。");
        var changes = new List<(McpTarget Target, string? Description)>(); var seen = new HashSet<McpTarget>();
        foreach (var update in updates)
        {
            var target = session.RequireTarget(RequiredString(update, "ref"));
            if (!seen.Add(target)) throw new McpRequestException("invalid_request", "同じ対象が重複しています。");
            var description = update.TryGetProperty("description", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
            description = DataValidator.ValidateDescription(description);
            if (!IsDescribable(target)) throw new McpRequestException("not_allowed", "説明を更新できない対象です。");
            changes.Add((target, description));
        }
        if (!_app.TryCommit(data =>
        {
            foreach (var change in changes)
            {
                var tab = data.Tabs.FirstOrDefault(tab => tab.Id.Equals(change.Target.TabId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("対象が見つかりません。");
                if (change.Target.NodeId is null) tab.Description = change.Description;
                else (FindNode(tab.Children, change.Target.NodeId) ?? throw new InvalidDataException("対象が見つかりません。")).Description = change.Description;
            }
        }, out var error)) throw new McpRequestException("save_failed", error);
        return new { status = "updated", count = changes.Count };
    }

    private bool IsDescribable(McpTarget target)
    {
        var tab = FindTab(target.TabId);
        if (tab is null || tab.IsSystemTab) return false;
        return target.NodeId is null || FindNode(tab.Children, target.NodeId) is not null and not SeparatorItem;
    }

    private ItemSummaryResult ItemSummary(McpSession session, LauncherTab tab, LauncherNode node) => new(
        session.RefFor(new McpTarget(tab.Id, node.Id)),
        NodeKind(node),
        McpName(node),
        NodeDescription(node),
        tab.Name,
        IsAvailable(node),
        IsLaunchable(node),
        !tab.IsSystemTab && node is not SeparatorItem,
        node is GroupNode);

    private string? RefForResolvedNode(McpSession session, LauncherNode node)
    {
        foreach (var tab in _app.Data.Tabs)
            if (Walk(_app.GetDisplayChildren(tab)).Any(candidate => ReferenceEquals(candidate, node))) return session.RefFor(new McpTarget(tab.Id, node.Id));
        return null;
    }

    private (LauncherTab Tab, IReadOnlyList<LauncherNode> Children) ResolveChildren(McpTarget target)
    {
        var tab = FindTab(target.TabId) ?? throw new McpRequestException("invalid_ref", "参照先が失効しています。");
        if (target.NodeId is null) return (tab, _app.GetDisplayChildren(tab));
        var group = FindNode(_app.GetDisplayChildren(tab), target.NodeId) as GroupNode ?? throw new McpRequestException("invalid_ref", "参照先が存在しないか、Groupではありません。");
        return (tab, group.Children);
    }

    private (LauncherTab Tab, LauncherNode? Node) ResolveNode(McpTarget target)
    {
        var tab = FindTab(target.TabId) ?? throw new McpRequestException("invalid_ref", "参照先が失効しています。");
        return (tab, target.NodeId is null ? null : FindNode(_app.GetDisplayChildren(tab), target.NodeId));
    }

    private LauncherTab? FindTab(string id) => _app.Data.Tabs.FirstOrDefault(tab => tab.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    private static LauncherNode? FindNode(IEnumerable<LauncherNode> nodes, string id) { foreach (var node in nodes) { if (node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return node; if (node is GroupNode group && FindNode(group.Children, id) is { } found) return found; } return null; }
    private static IEnumerable<LauncherNode> Walk(IEnumerable<LauncherNode> nodes) { foreach (var node in nodes) { yield return node; if (node is GroupNode group) foreach (var child in Walk(group.Children)) yield return child; } }
    private static string McpName(LauncherNode node) => node switch { DirectoryItem directory => Path.GetFileName(directory.Target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : "Directory", _ => DataValidator.NodeLabel(node) };
    private static string NodeKind(LauncherNode node) => node switch { WindowsMenuShortcutItem => "windows_menu", StoreAppItem => "store_app", PresetItem => "preset", GroupNode => "group", FileItem => "file", DirectoryItem => "directory", UrlItem => "url", SeparatorItem => "separator", _ => "item" };
    private static string? NodeDescription(LauncherNode node) => node is PresetItem preset ? preset.FixedDescription : node.Description;
    private static bool IsAvailable(LauncherNode node) => node switch { WindowsMenuShortcutItem item => File.Exists(item.Target), FileItem item => File.Exists(item.Target), DirectoryItem item => Directory.Exists(item.Target), UrlItem item => Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", UsageDisplayItem item => item.IsAvailable, _ => true };
    private static bool IsLaunchable(LauncherNode node) => node switch { PresetItem item => item.AllowMcp, FileItem or DirectoryItem or UrlItem or StoreAppItem => IsAvailable(node), _ => false };
    private static string? SystemTabDescription(string kind) => kind switch { LauncherTabKinds.WindowsMenu => "Windowsのスタートメニューから取得した項目です。", LauncherTabKinds.StoreApps => "現在のWindowsユーザーが利用できるストアアプリです。", LauncherTabKinds.Presets => "OpenGepaが提供するWindows主要操作です。", LauncherTabKinds.History => "最近の成功起動履歴です。", LauncherTabKinds.Frequency => "項目の使用頻度です。", _ => null };
    private static bool InScope(LauncherTab tab, HashSet<string>? scopes) => scopes is null || scopes.Contains(tab.Kind) || tab.Kind == LauncherTabKinds.Presets && scopes.Contains("primaryOperations");
    private static int MatchRank(string name, string? description, string query) => name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ? 3 : int.MaxValue;
    private McpRequestException Expire(McpSession session, string reference) { session.RemoveRef(reference); return new("invalid_ref", "参照先が失効しています。"); }

    private PageResult<T> Page<T>(McpSession session, string operation, string signature, IReadOnlyList<T> items, JsonElement payload, int defaultLimit)
    {
        var limit = GetLimit(payload, defaultLimit); var cursor = GetString(payload, "cursor"); var offset = 0;
        if (cursor is not null) offset = session.RequireCursor(cursor, operation, signature, Interlocked.Read(ref _generation));
        if (offset > items.Count) throw new McpRequestException("invalid_cursor", "一覧が変更されたためcursorが失効しました。");
        var result = items.Skip(offset).Take(limit).ToList(); var nextOffset = offset + result.Count;
        var next = nextOffset < items.Count ? session.NewCursor(operation, signature, Interlocked.Read(ref _generation), nextOffset) : null;
        return new(result, next);
    }

    private static string RequiredString(JsonElement element, string name) => GetString(element, name) is { Length: > 0 } value ? value : throw new McpRequestException("invalid_request", $"{name}を指定してください。");
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool GetBool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static IReadOnlyList<string> GetStrings(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : [];
    private static int GetLimit(JsonElement element, int defaultValue)
    {
        var value = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var result) ? result : defaultValue;
        if (value is < 1 or > 100) throw new McpRequestException("invalid_request", "limitは1以上100以下で指定してください。");
        return value;
    }

    private sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);
    private sealed record ItemSummaryResult(
        string Ref,
        string Kind,
        string Name,
        string? Description,
        string TabName,
        bool Available,
        bool Launchable,
        bool Describable,
        bool HasChildren);
}

public sealed class McpPipeServer : IDisposable
{
    private readonly McpCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    public McpPipeServer(McpCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator; _dispatcher = dispatcher; _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(InstanceIdentity.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_shutdown.Token); _ = HandleConnectionAsync(pipe); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch { pipe.Dispose(); await Task.Delay(100, _shutdown.Token).ConfigureAwait(false); }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        string? sessionId = null;
        await using (pipe)
        {
            try
            {
                while (pipe.IsConnected && !_shutdown.IsCancellationRequested)
                {
                    var request = await Wire.ReadAsync<IpcRequest>(pipe, _shutdown.Token);
                    if (request is null) break;
                    sessionId ??= request.SessionId;
                    if (!string.Equals(sessionId, request.SessionId, StringComparison.Ordinal)) { await Wire.WriteAsync(pipe, IpcResponse.Fail("invalid_request", "接続中にMCPセッションを変更できません。"), _shutdown.Token); continue; }
                    var response = await _dispatcher.InvokeAsync(() => _coordinator.HandleAsync(request, _shutdown.Token)).Task.Unwrap();
                    await Wire.WriteAsync(pipe, response, _shutdown.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch { }
            finally { _coordinator.EndSession(sessionId); }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _acceptLoop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}

public sealed class McpSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, McpTarget> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<McpTarget, string> _reverseRefs = [];
    private readonly Dictionary<string, McpCursor> _cursors = new(StringComparer.Ordinal);

    public string RefFor(McpTarget target)
    {
        lock (_gate)
        {
            if (_reverseRefs.TryGetValue(target, out var existing)) return existing;
            var value = NewToken(); _refs.Add(value, target); _reverseRefs.Add(target, value); return value;
        }
    }
    public McpTarget RequireTarget(string reference) { lock (_gate) return _refs.TryGetValue(reference, out var target) ? target : throw new McpRequestException("invalid_ref", "refが無効または失効しています。"); }
    public void RemoveRef(string reference) { lock (_gate) { if (_refs.Remove(reference, out var target)) _reverseRefs.Remove(target); } }
    public string NewCursor(string operation, string signature, long generation, int offset) { lock (_gate) { var value = NewToken(); _cursors[value] = new(operation, signature, generation, offset); return value; } }
    public int RequireCursor(string cursor, string operation, string signature, long generation)
    {
        lock (_gate)
        {
            if (!_cursors.TryGetValue(cursor, out var state) || state.Operation != operation || state.Signature != signature || state.Generation != generation) throw new McpRequestException("invalid_cursor", "cursorが無効または失効しています。");
            return state.Offset;
        }
    }
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record McpTarget(string TabId, string? NodeId);
public sealed record McpCursor(string Operation, string Signature, long Generation, int Offset);
public sealed class McpRequestException(string code, string message) : Exception(message) { public string Code { get; } = code; }
