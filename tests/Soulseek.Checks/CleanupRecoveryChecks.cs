using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class CleanupRecoveryChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string root, string ffmpeg, string source, string cover)
    {
        var oldCache = CacheLocation.Directory;
        var folder = Path.Combine(root, "cleanup-recovery");
        CacheLocation.Directory = Path.Combine(folder, "cache");
        Directory.CreateDirectory(CacheLocation.Directory);
        var job = new AlbumDownload
        {
            Artist = "Test", Album = "Offline complete", Owner = "old-account", State = "packing", RecordType = "album", ReleaseDate = new(2026, 11, 1),
            DownloadsDirectory = Path.Combine(folder, "originals"), ArchiveDirectory = Path.Combine(folder, "archives"),
            FfmpegPath = ffmpeg, CoverUrl = cover, Files = [new("Album\\tone.flac", new FileInfo(source).Length)],
        };
        new SoulseekSettings { DownloadsDirectory = job.DownloadsDirectory, ArchiveDirectory = job.ArchiveDirectory }.Save();
        var history = Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json");
        var original = AlbumJobFiles.DownloadPath(job, 0);
        async Task Seed()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(source, original, true);
            await File.WriteAllTextAsync(history, JsonSerializer.Serialize(new[] { job }));
        }
        async Task<AlbumDownload> Wait(AlbumDownloads downloads, Func<AlbumDownload, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (true)
            {
                var snapshot = (await downloads.SnapshotAsync()).Single();
                if (predicate(snapshot)) return snapshot;
                if (snapshot.State == "failed") throw new Exception(snapshot.Detail);
                await Task.Delay(50, timeout.Token);
            }
        }
        try
        {
            await Seed();
            await using (var downloads = new AlbumDownloads())
            {
                job = await Wait(downloads, j => j.State == "done" && j.FilesRemoved);
                Check(!downloads.Session.IsLoggedIn && !File.Exists(original) && job.CleanupCompletedAt != null, "Fully received album must finish and clean up offline after restart.");
            }
            var zip = job.ArchivePath!;
            job.FilesRemoved = false; job.CleanupPending = true; job.CleanupCompletedAt = null;
            await Seed();
            using (var locked = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await using var downloads = new AlbumDownloads();
                job = await Wait(downloads, j => j.CleanupPending && j.CleanupError.Length > 0);
                Check(File.Exists(original) && job.CleanupAttemptedAt != null, "Locked files must remain tracked with pending cleanup.");
                Check(await downloads.DeleteHistoryAsync(job.Id) == 0, "History must retain ownership of remaining files.");
            }
            await using (var downloads = new AlbumDownloads())
            {
                job = await Wait(downloads, j => j.FilesRemoved && !j.CleanupPending);
                Check(!File.Exists(original) && File.Exists(zip) && job.CleanupError.Length == 0, "Restart must retry and clear the persisted cleanup error.");
            }
            job.FilesRemoved = false; job.CleanupPending = true;
            var missingZip = Path.Combine(job.ArchiveDirectory, "restored.zip");
            job.ArchivePath = missingZip;
            await Seed();
            await using (var downloads = new AlbumDownloads())
            {
                job = await Wait(downloads, j => j.CleanupError.Length > 0);
                Check(File.Exists(original), "Missing ZIP must preserve originals.");
                File.Copy(zip, missingZip);
                Check(await downloads.RetryCleanupAsync(job.Id) == 1 && !File.Exists(original), "Manual cleanup retry must use remembered paths.");
            }
            job.CleanupError = ""; job.FilesRemoved = false; job.CleanupPending = true;
            job.ArchivePath = Path.Combine(job.ArchiveDirectory, "invalid.zip");
            await File.WriteAllTextAsync(job.ArchivePath, "not a ZIP");
            await Seed();
            await using (var downloads = new AlbumDownloads())
            {
                await Wait(downloads, j => j.CleanupError.Length > 0);
                Check(File.Exists(original), "Invalid ZIP must preserve originals.");
            }
            Console.WriteLine("Offline packing, persisted cleanup failure/restart retry, manual cleanup, history ownership and ZIP validation checks passed");
        }
        finally { CacheLocation.Directory = oldCache; }
    }
}
