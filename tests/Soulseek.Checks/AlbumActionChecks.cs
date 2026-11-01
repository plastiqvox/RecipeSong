using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class AlbumActionChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string root, string ffmpeg)
    {
        var oldCache = CacheLocation.Directory;
        var source = Path.Combine(root, "test-tone.flac");
        var cover = Path.Combine(root, "test-cover.jpg");
        await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", source], default);
        await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "color=c=blue:s=128x128", "-frames:v", "1", cover], default);
        AlbumDownload Job(string name) => new()
        {
            Artist = "Test artist", Album = name, Owner = "test", RecordType = "album", ReleaseDate = new(2026, 11, 1),
            DownloadsDirectory = Path.Combine(root, "originals"), ArchiveDirectory = Path.Combine(root, "archives"),
            FfmpegPath = ffmpeg, CoverUrl = cover, Files = [new("Album\\01.flac", new FileInfo(source).Length)],
        };
        async Task Seed(AlbumDownload job)
        {
            CacheLocation.Directory = Path.Combine(root, "cache-" + job.Id.ToString("N"));
            Directory.CreateDirectory(CacheLocation.Directory);
            new SoulseekSettings { DownloadsDirectory = job.DownloadsDirectory, ArchiveDirectory = job.ArchiveDirectory }.Save();
            await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(new[] { job }));
            Directory.CreateDirectory(Path.GetDirectoryName(AlbumJobFiles.DownloadPath(job, 0))!);
            File.Copy(source, AlbumJobFiles.DownloadPath(job, 0));
        }
        async Task<AlbumDownload> Wait(AlbumDownloads downloads, string state)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var row = (await downloads.SnapshotAsync()).Single();
                if (row.State == state) return row;
                if (row.State == "failed" && state != "failed") throw new Exception(row.Detail);
                await Task.Delay(50, timeout.Token);
            }
        }
        try
        {
            var done = Job("success");
            await Seed(done);
            string zip;
            await using (var downloads = new AlbumDownloads(() => true, () => "test", null))
                zip = (await Wait(downloads, "done")).ArchivePath!;
            Check(!File.Exists(AlbumJobFiles.DownloadPath(done, 0)) && File.Exists(zip), "Successful ZIP must replace received originals.");
            using (var archive = ZipFile.OpenRead(zip)) Check(archive.Entries.Count == 2 && archive.GetEntry("cover.jpg") != null, "ZIP must contain converted track and cover.");
            Check(File.Exists(source), "Unrelated source must remain.");

            var failed = Job("failure");
            failed.CoverUrl = null;
            await Seed(failed);
            await using (var downloads = new AlbumDownloads(() => true, () => "test", null))
            {
                await Wait(downloads, "failed");
                Check(File.Exists(AlbumJobFiles.DownloadPath(failed, 0)), "Failed ZIP must retain original.");
                await downloads.CancelAsync(failed.Id);
                Check(!File.Exists(AlbumJobFiles.DownloadPath(failed, 0)), "Explicit stop must delete retained files.");
            }

            var stopped = Job("stop");
            await Seed(stopped);
            var path = AlbumJobFiles.DownloadPath(stopped, 0);
            await File.WriteAllTextAsync(path + ".partial", "partial");
            var extra = Path.Combine(Path.GetDirectoryName(path)!, "unrelated.keep");
            await File.WriteAllTextAsync(extra, "keep");
            var work = Path.Combine(stopped.ArchiveDirectory, ".recipesong-" + stopped.Id.ToString("N"));
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, "album.zip.partial"), "partial ZIP");
            await using (var downloads = new AlbumDownloads())
            {
                await downloads.CancelAsync(stopped.Id);
                Check(!File.Exists(path) && !File.Exists(path + ".partial") && !Directory.Exists(work), "Stop must remove original, partial and conversion files.");
                Check(File.Exists(extra) && File.Exists(zip), "Stop must preserve unrelated files and finished ZIP.");
                var rec = new ReleaseRecommendation("Test artist", "stop", "album", new(2026, 11, 1), null, null, 1234, null, null, Region.International, null, null, null, [], [], false, 0);
                await downloads.SetRecommendationAsync(stopped.Id, rec);
                Check((await downloads.SnapshotAsync()).Single().Recommendation?.DeezerAlbumId == 1234, "Recommendation identity must persist.");
                var saved = new SavedReleases(Path.Combine(root, "saved.json"));
                saved.Add(rec); Check(new SavedReleases(Path.Combine(root, "saved.json")).Contains(1234), "Favorite registration must persist.");
                saved.Remove(1234); Check(!new SavedReleases(Path.Combine(root, "saved.json")).Contains(1234), "Favorite removal must persist.");
            }

            // 링크를 통한 다른 폴더 삭제를 거부한다. 생성 권한이 없는 PC에서는 이 항목만 생략한다.
            var linked = Job("linked");
            var linkedRoot = Path.Combine(linked.DownloadsDirectory, linked.Id.ToString("N"));
            var target = Path.Combine(root, "link-target");
            Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, Path.GetFileName(AlbumJobFiles.DownloadPath(linked, 0)));
            await File.WriteAllTextAsync(sentinel, "must remain");
            var hasLink = false;
            try { Directory.CreateSymbolicLink(linkedRoot, target); hasLink = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("Symlink check skipped: link creation unavailable."); }
            if (hasLink)
            {
                try { AlbumJobFiles.Cleanup(linked); throw new Exception("Linked cleanup allowed."); }
                catch (IOException) { }
                Check(File.Exists(sentinel), "Linked target must remain.");
            }

            var activeStop = Job("active-stop");
            await Seed(activeStop);
            using (var stopListener = new TcpListener(IPAddress.Loopback, 0))
            {
                stopListener.Start();
                activeStop.CoverUrl = $"http://127.0.0.1:{((IPEndPoint)stopListener.LocalEndpoint).Port}/pending";
                await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(new[] { activeStop }));
                await using var downloads = new AlbumDownloads(() => true, () => "test", null);
                using var peer = await stopListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await downloads.CancelAsync(activeStop.Id).WaitAsync(TimeSpan.FromSeconds(8));
                Check(!File.Exists(AlbumJobFiles.DownloadPath(activeStop, 0)) && (await downloads.SnapshotAsync()).Single().State == "cancelled", "Active stop must wait for writers and then delete files.");
            }

            var shutdown = Job("shutdown");
            await Seed(shutdown);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            shutdown.CoverUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/pending";
            await File.WriteAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), JsonSerializer.Serialize(new[] { shutdown }));
            var manager = new AlbumDownloads(() => true, () => "test", null);
            var disposed = false;
            try
            {
                using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                disposed = true;
                var persisted = JsonSerializer.Deserialize<List<AlbumDownload>>(await File.ReadAllTextAsync(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json")))!;
                Check(persisted.Single().State == "waiting", "Shutdown must mark interrupted work as resumable.");
            }
            finally { if (!disposed) await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)); }
            Check(File.Exists(AlbumJobFiles.DownloadPath(shutdown, 0)), "Application shutdown must retain resumable originals.");
            await CleanupRecoveryChecks.RunAsync(root, ffmpeg, source, cover);
            Console.WriteLine("ZIP cleanup, explicit stop, unrelated-file protection, metadata/favorites and shutdown checks passed");
        }
        finally { CacheLocation.Directory = oldCache; }
    }
}
