using System.Collections.Concurrent;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class AlbumSchedulingChecks
{
    public static async Task RunAsync(string root)
    {
        var oldCache = CacheLocation.Directory;
        CacheLocation.Directory = Path.Combine(root, "album-scheduling");
        Directory.CreateDirectory(CacheLocation.Directory);
        new SoulseekSettings { DownloadsDirectory = Path.Combine(root, "originals"), ArchiveDirectory = Path.Combine(root, "archives") }.Save();
        var jobs = new[]
        {
            new AlbumDownload { Album = "remote-queued", Owner = "test" },
            new AlbumDownload { Album = "downloading", Owner = "test" },
            new AlbumDownload { Album = "added-later", Owner = "test", State = "cancelled" },
            new AlbumDownload { Album = "failed", Owner = "test" },
            new AlbumDownload { Album = "other-account", Owner = "other" },
        };
        await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(jobs));
        var calls = new ConcurrentDictionary<string, int>();
        var started = jobs.ToDictionary(j => j.Album, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var stopped = new ConcurrentDictionary<string, bool>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Process(AlbumDownload job, CancellationToken ct)
        {
            calls.AddOrUpdate(job.Album, 1, (_, count) => count + 1);
            started[job.Album].TrySetResult();
            if (job.Album == "failed") { job.State = "failed"; throw new IOException("simulated failure"); }
            // waiting을 유지해도 실행 중인 같은 ID를 중복 시작하지 않아야 한다.
            try { await hold.Task.WaitAsync(ct); }
            finally { stopped[job.Album] = true; }
        }
        var downloads = new AlbumDownloads(() => true, () => "test", Process);
        try
        {
            await Task.WhenAll(started["remote-queued"].Task, started["downloading"].Task, started["failed"].Task).WaitAsync(TimeSpan.FromSeconds(5));
            var ffmpeg = Path.Combine(root, "fake-ffmpeg.exe");
            await File.WriteAllTextAsync(ffmpeg, "test only");
            await downloads.RetryAsync(jobs[2].Id, new SoulseekSettings { FfmpegPath = ffmpeg, ArchiveDirectory = root });
            await started["added-later"].Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(2200);
            if (calls.Values.Any(n => n != 1) || calls.ContainsKey("other-account")) throw new Exception("Duplicate or wrong-account album started.");
            if (downloads.StartupError != "simulated failure") throw new Exception("Worker failure was not observed.");
        }
        finally
        {
            try { await downloads.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { CacheLocation.Directory = oldCache; }
        }
        if (stopped.Count != 3) throw new Exception("Shutdown did not drain every active album.");
        Console.WriteLine("Parallel albums, newly queued work, failure isolation, duplicate/account filtering and shutdown checks passed");
    }
}
