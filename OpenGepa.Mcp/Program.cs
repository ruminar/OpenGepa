using ModelContextProtocol.Server;
using Microsoft.Extensions.AI;
using OpenGepa;

await using var backend = new McpBackendClient();
var tools = new OpenGepaTools(backend);
var options = new McpServerOptions
{
    ServerInfo = new() { Name = "OpenGepa.Mcp", Version = "0.4.1" },
    ToolCollection =
    [
        McpServerTool.Create(tools.list_tabs, new() { Name = "list_tabs", Description = "OpenGepaの現在の縦タブ一覧を返します。" }),
        McpServerTool.Create(tools.browse_items, new() { Name = "browse_items", Description = "OpenGepaのタブまたはGroup直下の項目を一覧します。" }),
        McpServerTool.Create(tools.search_items, new()
        {
            Name = "search_items",
            Description = "OpenGepaの表示名と説明を検索します。ベクトル検索や外部AI APIは使用しません。",
            SchemaCreateOptions = new AIJsonSchemaCreateOptions { TransformSchemaNode = (_, node) => GeminiSchemaCompatibility.NormalizeNullableArray(node) }
        }),
        McpServerTool.Create(tools.get_item, new() { Name = "get_item", Description = "OpenGepa項目の現在の詳細を返します。include_target=trueの場合だけファイルパスやURLを返します。" }),
        McpServerTool.Create(tools.launch_item, new() { Name = "launch_item", Description = "OpenGepaへ登録済みでMCP実行が許可された項目を起動します。任意パス、任意URL、任意コマンドは受け付けません。" }),
        McpServerTool.Create(tools.get_recent_items, new() { Name = "get_recent_items", Description = "OpenGepaに記録された最近の成功起動を新しい順に返します。" }),
        McpServerTool.Create(tools.get_frequent_items, new() { Name = "get_frequent_items", Description = "OpenGepaの使用頻度を返します。periodは30dまたはallです。" }),
        McpServerTool.Create(tools.update_descriptions, new() { Name = "update_descriptions", Description = "OpenGepa管理項目の説明を原子的に更新します。利用者から別の指定がない限り、説明は日本語で記述してください。" })
    ]
};

await using var transport = new StdioServerTransport(options);
await using var server = McpServer.Create(transport, options);
using var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, InstanceIdentity.McpShutdownEventName);
using var shutdownCancellation = new CancellationTokenSource();
using var stopWatcher = new CancellationTokenSource();
var shutdownWatcher = Task.Run(() =>
{
    if (WaitHandle.WaitAny([shutdownEvent, stopWatcher.Token.WaitHandle]) == 0)
        shutdownCancellation.Cancel();
});

try
{
    await server.RunAsync(shutdownCancellation.Token);
}
catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
{
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    await Console.Error.WriteLineAsync($"OpenGepa.Mcp: {ex}");
    Environment.ExitCode = 1;
}
finally
{
    stopWatcher.Cancel();
    await shutdownWatcher;
}
