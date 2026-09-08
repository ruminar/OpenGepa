using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using OpenGepa;
using OpenGepa.McpProtocol;

public sealed class McpBackendClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private NamedPipeClientStream? _pipe;

    public async Task<string> CallAsync(string operation, object payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var request = new IpcRequest { SessionId = _sessionId, Operation = operation, Payload = JsonSerializer.SerializeToElement(payload, Wire.Options) };
            await Wire.WriteAsync(_pipe!, request, cancellationToken);
            var response = await Wire.ReadAsync<IpcResponse>(_pipe!, cancellationToken) ?? throw new EndOfStreamException("OpenGepa本体とのIPCが切断されました。");
            return response.Success && response.Data is JsonElement data
                ? data.GetRawText()
                : JsonSerializer.Serialize(new { status = response.Error ?? "failed", message = response.Message ?? "OpenGepa本体が要求を処理できませんでした。" }, Wire.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ResetPipe();
            return JsonSerializer.Serialize(new { status = "ipc_failed", message = ex.Message }, Wire.Options);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true) return;
        ResetPipe();
        if (await TryConnectAsync(150, cancellationToken)) return;
        var directory = AppContext.BaseDirectory;
        var executable = Path.Combine(directory, "OpenGepa.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("隣接するOpenGepa.exeが見つかりません。", executable);
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = directory });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken);
            if (await TryConnectAsync(250, cancellationToken)) return;
        }
        throw new TimeoutException("OpenGepa本体のMCP用IPCが10秒以内に準備されませんでした。");
    }

    private async Task<bool> TryConnectAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ResetPipe();
        var pipe = new NamedPipeClientStream(".", InstanceIdentity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken);
            _pipe = pipe; return true;
        }
        catch (TimeoutException) { pipe.Dispose(); return false; }
        catch (IOException) { pipe.Dispose(); return false; }
    }

    private void ResetPipe() { _pipe?.Dispose(); _pipe = null; }
    public ValueTask DisposeAsync() { ResetPipe(); _gate.Dispose(); return ValueTask.CompletedTask; }
}
