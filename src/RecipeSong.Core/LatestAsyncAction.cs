namespace RecipeSong.Core;

/// <summary>동작 하나만 실행하고, 실행 중 들어온 여러 요청은 최신 상태를 읽는 한 번의 후속 동작으로 합친다.</summary>
public sealed class LatestAsyncAction(Func<Task> action)
{
    private readonly object _gate = new();
    private TaskCompletionSource? _pending;
    private bool _requested;

    public Task RequestAsync()
    {
        lock (_gate)
        {
            _requested = true;
            if (_pending != null) return _pending.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion;
            _ = DrainAsync(completion);
            return completion.Task;
        }
    }

    private async Task DrainAsync(TaskCompletionSource completion)
    {
        // 요청한 UI 문맥을 유지하고, 같은 이벤트에서 들어온 변경도 먼저 합친다.
        await Task.Yield();
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_requested)
                    {
                        _pending = null;
                        completion.TrySetResult();
                        return;
                    }
                    _requested = false;
                }
                await action();
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _pending = null;
                if (ex is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
                else completion.TrySetException(ex);
            }
        }
    }
}
