using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class DownloadHistoryChecks
{
    public static async Task RunAsync(string root)
    {
        var oldCache = CacheLocation.Directory;
        CacheLocation.Directory = Path.Combine(root, "history-cache");
        Directory.CreateDirectory(CacheLocation.Directory);
        try
        {
            var archive = Path.Combine(root, "keep-album.zip");
            var original = Path.Combine(root, "keep-original.mp3");
            await File.WriteAllTextAsync(archive, "archive must remain");
            await File.WriteAllTextAsync(original, "audio must remain");
            var jobs = new[] { "done", "failed", "cancelled", "waiting", "downloading", "packing" }
                .Select(state => new AlbumDownload { Album = state, State = state, ArchivePath = archive, DownloadsDirectory = root }).ToList();
            var path = Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(jobs));
            await using (var downloads = new AlbumDownloads())
            {
                Check(await downloads.DeleteHistoryAsync(jobs[3].Id) == 0, "Waiting job must remain");
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { await downloads.DeleteHistoryAsync(jobs[0].Id); throw new Exception("History save failure ignored"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Check((await downloads.SnapshotAsync()).Count == jobs.Count, "Save failure preserves in-memory history");
                }
                Check(await downloads.DeleteHistoryAsync(jobs[1].Id) == 1, "Delete selected failed history");
                Check(!(await downloads.SnapshotAsync()).Any(j => j.Id == jobs[1].Id), "Selected history removed");
                Check(await downloads.DeleteHistoryAsync() == 2, "Clear only completed, failed or cancelled history");
                var remaining = await downloads.SnapshotAsync();
                Check(remaining.Count == 3 && remaining.All(j => j.State == "waiting"), "Queued and resumable jobs remain");
            }
            var persisted = JsonSerializer.Deserialize<List<AlbumDownload>>(await File.ReadAllTextAsync(path))!;
            Check(persisted.Count == 3 && persisted.All(j => j.State == "waiting"), "History deletion persisted");
            Check(await File.ReadAllTextAsync(archive) == "archive must remain" && await File.ReadAllTextAsync(original) == "audio must remain", "History deletion preserves ZIP and audio files");
            var row = new SoulseekTransfer(Guid.NewGuid(), true, "peer", @"Music\Album\track.mp3", "uploading", 5, 10, 10, null)
            { LocalPath = @"D:\Music\track.mp3", Endpoint = "127.0.0.1:50001", StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10) };
            Check(row.Percent == 50 && row.Information.Contains(row.LocalPath) && row.Information.Contains(row.Endpoint) && row.Information.Contains(row.Filename), "Upload information includes local/remote paths and endpoint");
            Console.WriteLine("History deletion, persistence, active-job/source preservation and upload information checks passed");
        }
        finally { CacheLocation.Directory = oldCache; }
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
}
