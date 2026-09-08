using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class OpenGepaTools(McpBackendClient backend)
{
    [McpServerTool, Description("OpenGepaの現在の縦タブ一覧を返します。")]
    public Task<string> list_tabs([Description("非表示タブも含める場合はtrue。既定はfalse。") ] bool include_hidden = false, CancellationToken cancellationToken = default)
        => backend.CallAsync("list_tabs", new { include_hidden }, cancellationToken);

    [McpServerTool, Description("OpenGepaのタブまたはGroup直下の項目を一覧します。")]
    public Task<string> browse_items([Description("list_tabs等から取得した親のref。") ] string parent_ref, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default)
        => backend.CallAsync("browse_items", new { parent_ref, limit, cursor }, cancellationToken);

    [McpServerTool, Description("OpenGepaの表示名と説明を検索します。ベクトル検索や外部AI APIは使用しません。")]
    public Task<string> search_items(string query, bool include_hidden = false, string[]? scopes = null, int limit = 30, string? cursor = null, CancellationToken cancellationToken = default)
        => backend.CallAsync("search_items", new { query, include_hidden, scopes, limit, cursor }, cancellationToken);

    [McpServerTool, Description("OpenGepa項目の現在の詳細を返します。include_target=trueの場合だけファイルパスやURLを返します。")]
    public Task<string> get_item(string @ref, bool include_target = false, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_item", new { @ref, include_target }, cancellationToken);

    [McpServerTool, Description("OpenGepaへ登録済みでMCP実行が許可された項目を起動します。任意パス、任意URL、任意コマンドは受け付けません。")]
    public Task<string> launch_item(string @ref, CancellationToken cancellationToken = default)
        => backend.CallAsync("launch_item", new { @ref }, cancellationToken);

    [McpServerTool, Description("OpenGepaに記録された最近の成功起動を新しい順に返します。")]
    public Task<string> get_recent_items(int limit = 20, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_recent_items", new { limit }, cancellationToken);

    [McpServerTool, Description("OpenGepaの使用頻度を返します。periodは30dまたはallです。")]
    public Task<string> get_frequent_items(string period = "30d", int limit = 20, CancellationToken cancellationToken = default)
        => backend.CallAsync("get_frequent_items", new { period, limit }, cancellationToken);

    [McpServerTool, Description("OpenGepa管理項目の説明を原子的に更新します。利用者から別の指定がない限り、説明は日本語で記述してください。")]
    public Task<string> update_descriptions(DescriptionUpdate[] updates, CancellationToken cancellationToken = default)
        => backend.CallAsync("update_descriptions", new { updates }, cancellationToken);
}

public sealed record DescriptionUpdate(string @ref, string? description);
