using System.ComponentModel;

public sealed class OpenGepaTools(McpBackendClient backend)
{
    public Task<string> list_tabs([Description("非表示タブも含める場合はtrue。既定はfalse。") ] bool include_hidden = false, CancellationToken cancellationToken = default)
        => backend.CallAsync("list_tabs", new { include_hidden }, cancellationToken);

    public Task<string> browse_items([Description("list_tabs等から取得した親のref。") ] string parent_ref, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default)
        => backend.CallAsync("browse_items", new { parent_ref, limit, cursor }, cancellationToken);

    public Task<string> search_items(string query, bool include_hidden = false, string[]? scopes = null, int limit = 30, string? cursor = null, CancellationToken cancellationToken = default)
        => backend.CallAsync("search_items", new { query, include_hidden, scopes, limit, cursor }, cancellationToken);

    public Task<string> get_item(string @ref, bool include_target = false, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_item", new { @ref, include_target }, cancellationToken);

    public Task<string> launch_item(string @ref, CancellationToken cancellationToken = default)
        => backend.CallAsync("launch_item", new { @ref }, cancellationToken);

    public Task<string> get_recent_items(int limit = 20, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_recent_items", new { limit }, cancellationToken);

    public Task<string> get_frequent_items(string period = "30d", int limit = 20, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_frequent_items", new { period, limit }, cancellationToken);

    public Task<string> update_descriptions(DescriptionUpdate[] updates, CancellationToken cancellationToken = default)
        => backend.CallAsync("update_descriptions", new { updates }, cancellationToken);
}

public sealed record DescriptionUpdate(string @ref, string? description);
