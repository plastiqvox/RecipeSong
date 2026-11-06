using System.IO.Compression;
using System.Text;
using RecipeSong.Core;
using RecipeSong.Core.Library;
using RecipeSong.Core.Providers;

static class ZipArchiveChecks
{
    public static async Task RunAsync(string root)
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var folder = Path.Combine(root, "album-zips");
        Directory.CreateDirectory(folder);
        var wav = Path.Combine(root, "tagged.wav");
        using (var writer = new BinaryWriter(File.Create(wav)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 1600);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(1600); writer.Write(new byte[1600]);
        }
        using (var tags = TagLib.File.Create(wav))
        {
            tags.Tag.AlbumArtists = ["Favourite artist"];
            tags.Tag.Performers = ["Guest performer"];
            tags.Tag.Album = "Unrated album";
            tags.Tag.Title = "Track";
            tags.Tag.Year = 2020;
            tags.Save();
        }
        var taggedZip = Path.Combine(folder, "arbitrary filename.zip");
        using (var zip = ZipFile.Open(taggedZip, ZipArchiveMode.Create)) zip.CreateEntryFromFile(wav, "../never-extract.wav");
        var standard = Path.Combine(folder, "[2021.02.03] (Album) Archive-only artist - Archive-only album.zip");
        using (var zip = ZipFile.Open(standard, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("01.mp3").Open();
            entry.Write([0, 1, 2, 3]);
        }
        var settings = new AppSettings { ArchiveFolders = [folder, folder] };
        var library = new LibraryService(settings);
        var before = File.ReadAllBytes(taggedZip);
        var progress = new ScanProgress();
        var albums = await library.GetArchiveAlbumsAsync(progress: progress);
        var completed = progress.Events.Last();
        Check(completed.Stage == ArchiveScanStage.Completed && completed.Processed == 2 && completed.Albums == 2
            && completed.Reused == 0 && completed.Warnings == 0, "Initial ZIP scan progress did not report its final counts.");
        Check(progress.Events.Any(p => p.Stage == ArchiveScanStage.Reading && p.CurrentPath == taggedZip)
            && progress.Events.Zip(progress.Events.Skip(1)).All(p => p.First.Processed <= p.Second.Processed),
            "Current ZIP path or monotonic processed count was missing.");
        var cachedProgress = new ScanProgress();
        await library.GetArchiveAlbumsAsync(refresh: true, progress: cachedProgress);
        Check(cachedProgress.Events.Last().Reused == 2 && cachedProgress.Events.Last().Processed == 2, "Cached ZIP progress omitted reuse counts.");
        Check(albums.Count == 2 && albums.Any(a => a.Artist == "Favourite artist" && a.Album == "Unrated album" && a.Year == 2020),
            "ZIP tag metadata, album artist priority or standard filename fallback failed.");
        Check(!File.Exists(Path.Combine(root, "never-extract.wav")) && before.SequenceEqual(File.ReadAllBytes(taggedZip)), "ZIP scan extracted or modified input files.");
        Check((await library.GetTracksAsync()).Count == 0 && (await library.GetPlaylistsAsync()).Count == 0, "ZIP albums leaked into taste tracks or playlists.");
        var owned = new OwnedLibrary([], albums);
        Check(owned.HasAlbum("Favourite artist", "Unrated album") && owned.HasArchiveAlbum("Archive-only artist", "Archive-only album"), "Archive ownership was not applied.");
        using var http = new HttpClient(new NoNetwork());
        var tasteTrack = new TrackRecord { Artist = "Favourite artist", Album = "Favourite album", Title = "Loved track", Loved = true, PlayCount = 42 };
        var playlist = new Playlist("Favourites", "1", null, PlaylistKind.User, [tasteTrack]);
        var discovery = new ArtistAlbumDiscovery(new DeezerClient(http), [tasteTrack], [playlist], [], archives: albums);
        Check(discovery.Musicians().Count == 1 && discovery.Musicians()[0].Tracks.Count == 1 && discovery.Musicians()[0].LovedCount == 1,
            "Unrated archive albums changed favourite artists or their taste counts.");
        var catalog = new DzAlbum(1, "Unrated album", null, "2020-01-01", "album", null, TrackCount: 1);
        Check(discovery.Ownership("Favourite artist", catalog, out _) == CatalogOwnership.InLibrary, "Archived album appeared missing.");
        Check(discovery.Ownership("Favourite artist", catalog with { TrackCount = 2 }, out _) == CatalogOwnership.Partial, "Incomplete archive appeared complete.");
        var withoutArchives = new ArtistAlbumDiscovery(new DeezerClient(http), [tasteTrack], [playlist], []);
        Check(discovery.EvidenceHash("Favourite artist") != withoutArchives.EvidenceHash("Favourite artist"), "Archive album evidence was not included in identity validation.");
        File.Delete(standard);
        Check((await library.GetArchiveAlbumsAsync(refresh: true)).Count == 1, "Removed ZIP remained owned after rescan.");
        Check((await new LibraryService(new AppSettings()).GetArchiveAlbumsAsync()).Count == 0, "Unregistered archive folders remained owned.");
        var missing = Path.Combine(root, "offline-folder");
        var snapshots = new JsonCache<List<ArchiveAlbum>>("zip-album-folders", TimeSpan.FromDays(36500));
        snapshots.Set(missing, [albums[0]]); snapshots.Save();
        var offline = new ZipAlbumLibrary();
        Check(offline.Scan([missing], default).Count == 1 && offline.Warnings.Count > 0, "Offline archive lost its last known ownership without a warning.");
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.zip"), "not a zip");
        var scanner = new ZipAlbumLibrary();
        var failedProgress = new ScanProgress();
        Check(scanner.Scan([folder], default, failedProgress).Count == 1 && scanner.Warnings.Count > 0, "Corrupt ZIP prevented valid albums from loading.");
        Check(failedProgress.Events.Last().Warnings == scanner.Warnings.Count, "ZIP scan progress omitted warnings.");
        Console.WriteLine("ZIP 태그·파일명·무수정·중립 취향·보유 판정·ID 근거·재검색·오프라인 검사 통과");
    }

    sealed class ScanProgress : IProgress<ArchiveScanProgress>
    {
        public List<ArchiveScanProgress> Events { get; } = [];
        public void Report(ArchiveScanProgress value) => Events.Add(value);
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Exception("ZIP archive checks must not access the network.");
    }
}
