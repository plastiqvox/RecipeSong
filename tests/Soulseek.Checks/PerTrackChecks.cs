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
            await BannedStopsAlbumAsync(root);
            PeerFailureKinds();
        }
        finally { CacheLocation.Directory = previous; }
    }

    // 상대가 차단하면 대기 중인 나머지 곡을 기다리지 않고 앨범을 안내와 함께 실패로 끝낸다
    private static async Task BannedStopsAlbumAsync(string root)
    {
        CacheLocation.Directory = Path.Combine(root, "banned-cache");
        Directory.CreateDirectory(CacheLocation.Directory);
        var job = new AlbumDownload
        {
            Artist = "Banned artist", Album = "Banned album", Owner = "test", Username = "peer",
            DownloadsDirectory = Path.Combine(root, "banned-originals"), ArchiveDirectory = Path.Combine(root, "banned-archives"),
            Files = [new(@"Album\01.flac", 5), new(@"Album\02.flac", 5), new(@"Album\03.flac", 5)],
        };
        await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(new[] { job }));
        int cancelled = 0;
        async Task Download(string user, SoulseekFile file, string path, CancellationToken ct, Guid? album)
        {
            if (file == job.Files[1]) throw new global::Soulseek.TransferRejectedException("Transfer rejected: Banned");
            // 나머지 곡은 상대 대기열에서 끝없이 기다리는 상태
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
        }
        await using (var downloads = new AlbumDownloads(() => true, () => "test", null, Download))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while ((await downloads.SnapshotAsync()).Single().State != "failed") await Task.Delay(50, timeout.Token);
            var status = (await downloads.SnapshotAsync()).Single();
            if (status.Detail != AlbumDownloads.PeerFailure(new global::Soulseek.TransferRejectedException("Transfer rejected: Banned"))?.Text)
                throw new Exception("Banned album did not explain the ban.");
            if (downloads.BannedMeAt("peer") == null || downloads.BannedMeAt("someone else") != null) throw new Exception("The banning peer was not remembered.");
        }
        if (cancelled != 2) throw new Exception("Queued tracks kept waiting after the peer banned us.");
        // 다시 켜도 검색 결과에 표시할 수 있게 남는다
        await using (var reopened = new AlbumDownloads(() => false, () => "test", (_, _) => Task.CompletedTask))
            if (reopened.BannedMeAt("peer") == null) throw new Exception("The ban record was not saved.");
        Console.WriteLine("Banned peer stops the remaining tracks, fails the album with guidance and is remembered");
    }

    private static void PeerFailureKinds()
    {
        static bool? Stops(Exception ex) => AlbumDownloads.PeerFailure(ex)?.StopAlbum;
        if (Stops(new global::Soulseek.TransferRejectedException("Transfer rejected: File not shared.")) != true) throw new Exception("Unshared file did not stop the album.");
        if (Stops(new global::Soulseek.TransferRejectedException("Transfer rejected: Too many files")) != false) throw new Exception("Queue limit stopped the album.");
        if (Stops(new global::Soulseek.UserOfflineException("peer appears to be offline")) != true) throw new Exception("Offline peer did not stop the album.");
        // 화면의 예: 바깥 예외 안에 연결 끊김이 들어 있다
        if (Stops(new global::Soulseek.SoulseekClientException("Failed to download file", new global::Soulseek.ConnectionReadException("Unable to read data"))) != false)
            throw new Exception("Dropped connection was not treated as resumable.");
        if (Stops(new global::Soulseek.TransferRejectedException("Transfer rejected: Something new")) != null || Stops(new IOException("local")) != null)
            throw new Exception("Unknown failures must keep the original message.");
        Console.WriteLine("Peer failure kinds: ban/unshared/offline stop, queue limit/connection resume, unknown keep original");
    }
}
