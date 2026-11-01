using System.Collections.Concurrent;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class PerTrackChecks
{
    public static async Task RunAsync(string root)
    {
        var previous = CacheLocation.Directory;
        CacheLocation.Directory = Path.Combine(root, "per-track-cache");
        Directory.CreateDirectory(CacheLocation.Directory);
        var job = new AlbumDownload
        {
            Artist = "Test artist", Album = "Test album", Owner = "test", Username = "peer",
            DownloadsDirectory = Path.Combine(root, "originals"), ArchiveDirectory = Path.Combine(root, "archives"),
            Files = [new(@"Album\01.flac", 5), new(@"Album\02.flac", 5), new(@"Album\03.flac", 5)],
        };
        new SoulseekSettings { DownloadsDirectory = job.DownloadsDirectory, ArchiveDirectory = job.ArchiveDirectory }.Save();
        await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(new[] { job }));
        var requests = new ConcurrentBag<string>();
        async Task Download(string user, SoulseekFile file, string path, CancellationToken ct, Guid? album)
        {
            if (user != "peer" || album != job.Id || !job.Files.Contains(file)) throw new Exception("File request lost its album identity.");
            requests.Add(file.Filename);
            if (file == job.Files[0])
            {
                await File.WriteAllBytesAsync(path + ".partial", new byte[2], ct);
                throw new IOException("first track failed");
            }
            await Task.Delay(200, ct);
            await File.WriteAllBytesAsync(path, new byte[5], ct);
        }
        try
        {
            await using (var downloads = new AlbumDownloads(() => true, () => "test", null, Download))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while ((await downloads.SnapshotAsync()).Single().State != "failed") await Task.Delay(50, timeout.Token);
                var status = (await downloads.SnapshotAsync()).Single();
                if (status.Detail != "first track failed") throw new Exception("File error was not retained.");
            }
            if (requests.Distinct().Count() != 3 || requests.Count != 3) throw new Exception("Each selected track must receive one independent request.");
            if (!File.Exists(AlbumJobFiles.DownloadPath(job, 0) + ".partial")
                || !File.Exists(AlbumJobFiles.DownloadPath(job, 1)) || !File.Exists(AlbumJobFiles.DownloadPath(job, 2)))
                throw new Exception("One failed track cancelled successful siblings.");
            if (Directory.Exists(job.ArchiveDirectory) && Directory.EnumerateFiles(job.ArchiveDirectory, "*.zip").Any()) throw new Exception("Incomplete album must not be archived.");
            Console.WriteLine("Per-track requests, sibling failure isolation, resumable files and incomplete ZIP prevention checks passed");
        }
        finally { CacheLocation.Directory = previous; }
    }
}
