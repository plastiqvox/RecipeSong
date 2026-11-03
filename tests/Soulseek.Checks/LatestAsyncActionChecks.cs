using RecipeSong.Core;

static class LatestAsyncActionChecks
{
    public static async Task RunAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        int current = 1, active = 0, maxActive = 0;
        var action = new LatestAsyncAction(async () =>
        {
            maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
            seen.Add(current);
            if (seen.Count == 1) { started.SetResult(); await release.Task; }
            Interlocked.Decrement(ref active);
        });
        var first = action.RequestAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = new List<Task> { first };
        for (int i = 2; i <= 161; i++) { current = i; requests.Add(action.RequestAsync()); }
        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        if (maxActive != 1 || !seen.SequenceEqual(new[] { 1, 161 }))
            throw new Exception("Intermediate preference snapshots were queued or executed concurrently.");
        current = 162;
        await action.RequestAsync();
        if (seen.Last() != 162) throw new Exception("A later request was lost after completion.");
        int attempts = 0;
        var failed = new LatestAsyncAction(() => ++attempts == 1 ? Task.FromException(new IOException("fixture")) : Task.CompletedTask);
        try { await failed.RequestAsync(); throw new Exception("Failure was swallowed."); }
        catch (IOException) { }
        await failed.RequestAsync();
        if (attempts != 2) throw new Exception("A failed update prevented a retry.");
        Console.WriteLine("Latest async action: 161 requests collapsed to active + latest; serial execution and retry passed.");
    }
}
