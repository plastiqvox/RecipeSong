using System.Net;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class ArtistIdentityStoreChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var directory = Path.Combine(CacheLocation.Directory, "identity-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "deezer-artist-ids.json"), """{"Old artist":{"Value":44},"Missing":{"Value":null}}""");
        var path = Path.Combine(directory, "artist-identities.sqlite3");
        var db = new ArtistIdentityStore(path);
        Check(db.Read("Old artist")?.Source == ArtistIdentitySource.Legacy && db.FindValid("Old artist", "hash") == null,
            "Legacy IDs must not become confirmed mappings.");
        var song = new TrackRecord { Artist = "Artist", Album = "Owned album", Title = "Song A" };
        using var http = new HttpClient(new CatalogHandler());
        var client = new DeezerClient(http);
        var discovery = new ArtistAlbumDiscovery(client, [song], [], [], identityStore: db);
        var hash = discovery.EvidenceHash("Artist")!;
        Check(hash == new ArtistAlbumDiscovery(client, [song with { PlayCount = 100, Loved = true }], [], [], identityStore: db).EvidenceHash("Artist"),
            "Play-count or like changes must not invalidate identity evidence.");
        var evidence = new StoredArtistIdentity("Artist", "Catalog artist", 22, ArtistIdentitySource.Automatic, hash, ["album:ownedalbum"], 2, null, DateTimeOffset.UtcNow);
        db.Save(evidence);
        Check(new ArtistIdentityStore(path).FindValid("Artist", hash)?.ArtistId == 22, "Identity did not survive reopening the database.");
        Check(db.FindValid("Artist", "changed") == null, "Changed library evidence reused an automatic ID.");
        var musician = new PlaylistMusician("Artist", [song]);
        var loaded = await discovery.ResolveArtistAsync(musician, CancellationToken.None);
        Check(loaded.FromStore && loaded.SelectedId == 22, "Stored automatic ID performed an unnecessary network lookup.");
        discovery.ConfirmArtist(musician, new(33, "Catalog artist", null, null));
        Check(!db.Save(evidence) && db.FindValid("Artist", "changed")?.ArtistId == 33, "Automatic result overwrote a manual selection.");
        var revision = db.Revision;
        var engine = new ReleaseRecommender(client, identityStore: new(path));
        engine.SetIdentityLibrary([song]);
        Check(engine.KnownIdentity("Artist")?.ArtistId == 33, "General recommendations did not share the discovery mapping.");
        var mine = new PlaylistArtist("Artist", "Library", null, "", 1, new HashSet<string>());
        await engine.RefreshMineAsync([mine]);
        var pool = await engine.BuildPoolAsync([], [mine]);
        Check(pool.Artists.Single().Id == 33, "Recommendation pool did not use the selected ID.");
        var oldGeneration = db.Generation("Artist");
        discovery.ForgetArtist("Artist");
        Check(db.Revision > revision && engine.KnownIdentity("Artist") == null, "Reset was invisible to an existing recommendation reader.");
        Check(!db.Save(evidence, oldGeneration) && db.Read("Artist") == null, "An old in-flight lookup resurrected a reset ID.");
        var missingPool = await engine.BuildPoolAsync([], [mine]);
        Check(missingPool.Artists.Count == 0 && missingPool.MinePending == 1, "Reset ID was recovered from an obsolete JSON cache.");
        db.Forget("Old artist");
        Check(new ArtistIdentityStore(path).Read("Old artist") == null, "Restart imported a deliberately deleted legacy mapping again.");
        var expired = evidence with { CheckedAt = DateTimeOffset.UtcNow.AddDays(-91) };
        db.Save(expired);
        Check(db.FindValid("Artist", hash) == null, "Expired automatic evidence was trusted.");
        var tasks = Enumerable.Range(1, 8).Select(i => Task.Run(() => new ArtistIdentityStore(path).Save(evidence with { Name = "Parallel " + i })));
        await Task.WhenAll(tasks);
        Check(Enumerable.Range(1, 8).All(i => db.Read("Parallel " + i) != null), "Concurrent DB writes lost mappings.");
        var header = File.ReadAllBytes(path).Take(16).ToArray();
        Check(System.Text.Encoding.ASCII.GetString(header) == "SQLite format 3\0", "Identity store is not an SQLite database.");
        var raceDb = new ArtistIdentityStore(Path.Combine(directory, "race.sqlite3"));
        using var raceHttp = new HttpClient(new RacingSelectionHandler(raceDb));
        var raceDiscovery = new ArtistAlbumDiscovery(new DeezerClient(raceHttp), [song, song with { Album = "Other proof album" }], [], [], identityStore: raceDb);
        var raceResult = await raceDiscovery.ResolveArtistAsync(musician, CancellationToken.None);
        Check(raceResult.FromStore && raceResult.SelectedId == 99 && raceResult.Candidates.Single().Artist.Id == 99,
            "An in-flight automatic resolution hid or overwrote a concurrent manual selection outside its candidate list.");
        Console.WriteLine("Identity SQLite: persistence, migration, shared recommendation pool, manual priority, evidence/expiry, reset epochs and parallel writes: PASS");
    }
    sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/artist/33/albums") throw new Exception("Stored identity unexpectedly searched the network: " + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new DzAlbum(330, "Found album", null, "2000-01-01", "album", null) } })) });
        }
    }
    sealed class RacingSelectionHandler(ArtistIdentityStore db) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object data;
            if (path == "/search/artist") data = new[] { new DzArtist(22, "Artist", null, null) };
            else if (path == "/search/album") data = Array.Empty<DzAlbum>();
            else if (path == "/artist/22/albums")
            {
                db.Save(new("Artist", "Selected elsewhere", 99, ArtistIdentitySource.Manual, "", [], 0, null, DateTimeOffset.UtcNow));
                data = new[] { new DzAlbum(220, "Owned album", null, "2000-01-01", "album", null), new DzAlbum(221, "Other proof album", null, "2000-01-01", "album", null) };
            }
            else throw new Exception("Unexpected concurrent identity request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data })) });
        }
    }
}
