using System.Threading.Channels;
using OpenGepa.Models;

namespace OpenGepa.Services;

/// <summary>opengepa.json 系だけを一列に保存する単一ライターです。</summary>
/// <remarks>遅延保存は最後のスナップショットへ集約します。別用途のファイル操作はこのキューへ入れません。</remarks>
public sealed class DataSaveQueue : IDisposable
{
    private readonly DataStore _store;
    private readonly Channel<SaveRequest> _requests = Channel.CreateUnbounded<SaveRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly System.Threading.Timer _deferredTimer;
    private readonly Task _worker;
    private OpenGepaData? _deferredData;
    private long _deferredVersion;
    private long _lastSavedVersion = -1;
    private bool _disposed;

    public DataSaveQueue(DataStore store, TimeSpan? deferredDelay = null)
    {
        _store = store;
        _deferredTimer = new System.Threading.Timer(_ => EnqueueDeferred(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        DeferredDelay = deferredDelay ?? TimeSpan.FromMilliseconds(300);
        _worker = Task.Run(WriteLoopAsync);
    }

    public TimeSpan DeferredDelay { get; }
    public Exception? LastDeferredError { get; private set; }

    /// <summary>完了結果を利用者へ返す必要がある保存です。</summary>
    public Task SaveNowAsync(OpenGepaData snapshot, long version)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new SaveRequest(snapshot, version, true, completion))) throw new InvalidOperationException("設定保存キューを追加できませんでした。");
        return completion.Task;
    }

    /// <summary>頻繁なUI状態変更用です。最後の状態だけを遅延保存します。</summary>
    public void RequestDeferredSave(OpenGepaData snapshot, long version)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            _deferredData = snapshot;
            _deferredVersion = version;
            _deferredTimer.Change(DeferredDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>終了前に遅延分を含むすべての保存を完了させます。</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        EnqueueDeferred();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new SaveRequest(null, long.MaxValue, false, completion))) throw new InvalidOperationException("設定保存キューを終了できませんでした。");
        completion.Task.GetAwaiter().GetResult();
    }

    private void EnqueueDeferred()
    {
        OpenGepaData? snapshot;
        long version;
        lock (_gate)
        {
            snapshot = _deferredData;
            version = _deferredVersion;
            _deferredData = null;
            _deferredTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        if (snapshot is not null && !_disposed) _requests.Writer.TryWrite(new SaveRequest(snapshot, version, false, null));
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync())
        {
            try
            {
                if (request.Snapshot is not null && request.Version >= _lastSavedVersion)
                {
                    if (request.Validate) _store.Save(request.Snapshot); else _store.SaveWithoutValidation(request.Snapshot);
                    _lastSavedVersion = request.Version;
                    LastDeferredError = null;
                }
                request.Completion?.TrySetResult();
            }
            catch (Exception ex)
            {
                if (request.Completion is not null) request.Completion.TrySetException(ex);
                else LastDeferredError = ex;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _deferredTimer.Dispose();
        _requests.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DataSaveQueue));
    }

    private sealed record SaveRequest(OpenGepaData? Snapshot, long Version, bool Validate, TaskCompletionSource? Completion);
}
