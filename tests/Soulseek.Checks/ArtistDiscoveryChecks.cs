using System.Net;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class ArtistDiscoveryChecks
{
    public static async Task RunAsync()
    {
        await ArtistIdentityChecks.RunAsync();
        await ArtistIdentityStoreChecks.RunAsync();
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        static TrackRecord Song(string artist, string album, string title) => new() { Artist = artist, Album = album, Title = title };
        var loved = Song("Artist", "Partial", "One");
        var whole = new[] { loved, Song("Artist", "Owned", "First"), Song("Artist", "Owned", "Second"), Song("Artist", "Original", "Title") };
        var first = new Playlist("Favorites", "1", null, PlaylistKind.User, [loved]);
        var second = new Playlist("More", "2", null, PlaylistKind.User, [loved, Song("Other", "Album", "Song")]);
        var system = new Playlist("Library", "3", null, PlaylistKind.System, whole);
        using var http = new HttpClient(new CatalogHandler());
        var client = new DeezerClient(http);
        var discovery = new ArtistAlbumDiscovery(client, whole, [first, second, system], [new(10, "Followed", "", DateTime.Now)]);
        var musicians = discovery.Musicians();
        Check(musicians.Count == 3 && musicians.Single(m => m.Name == "Artist").Tracks.Count == 1, "Curated playlist union must deduplicate songs and exclude system library tracks.");
        Check(discovery.Musicians(first, false).Single().Name == "Artist", "Specific playlist selection leaked other artists or follows.");
        var topLibrary = new[]
        {
            loved with { PlayCount = 8 }, loved with { PlayCount = 8 },
            Song("Artist", "Most played", "Track A") with { PlayCount = 5 },
            Song("Artist", "Most played", "Track B") with { PlayCount = 5 },
            Song("Artist", "Third", "Track") with { PlayCount = 7 },
            Song("Artist", "Fourth", "Track") with { PlayCount = 1 },
            Song("Other", "Unrelated", "Track") with { PlayCount = 300 },
        };
        AlbumSummary? requestedCover = null;
        var topDiscovery = new ArtistAlbumDiscovery(client, topLibrary, [first], [new(99, "No library", "", DateTime.Now)],
            (album, _) => { requestedCover = album; return Task.FromResult<string?>("file:///test-cover.png"); });
        var topMusician = topDiscovery.Musicians().Single(m => m.Name == "Artist");
        Check(topMusician.TopAlbums.First().Album is { Album: "Most played", PlayCount: 10 },
            "Artist cover must use summed album plays from the full library, not playlist plays, the top single track or duplicate records.");
        Check(topMusician.TopAlbums.Select(a => a.Album.Album).SequenceEqual(["Most played", "Partial", "Third"]), "Artist thumbnails must include only the top three albums in play-count order.");
        Check(await topDiscovery.ArtistCoverAsync(topMusician.TopAlbums.First().Album, CancellationToken.None) == "file:///test-cover.png"
            && requestedCover?.Album == "Most played", "Artist thumbnail requested the wrong album artwork.");
        Check(topDiscovery.Musicians().Single(m => m.Name == "No library").TopAlbums.Count == 0, "Follow-only artist must not borrow another artist's cover.");
        Check(new ArtistAlbumDiscovery(client, [loved], [first], []).Musicians().Single().TopAlbums.Count == 1,
            "One owned album must produce one thumbnail.");
        Check(new ArtistAlbumDiscovery(client, [loved, Song("Artist", "Second", "Track")], [first], []).Musicians().Single().TopAlbums.Count == 2,
            "Two owned albums must produce two thumbnails without duplicate padding.");
        var rankedSongs = new[]
        {
            Song("Quiet", "Album", "One"), Song("Quiet", "Album", "Two"),
            Song("Liked", "Album", "Heart") with { Loved = true },
            Song("Played", "Album", "Frequent") with { PlayCount = 100 },
            Song("A tie", "Album", "One") with { PlayCount = 12 },
            Song("B tie", "Album", "One") with { PlayCount = 12 },
        };
        var rankedPlaylist = new Playlist("Ranked", "ranked", null, PlaylistKind.User, rankedSongs);
        var rankedDiscovery = new ArtistAlbumDiscovery(client, rankedSongs, [rankedPlaylist, rankedPlaylist], []);
        var ranked = rankedDiscovery.Musicians();
        Check(ranked.Select(a => a.Name).SequenceEqual(["Liked", "Played", "A tie", "B tie", "Quiet"]),
            "Likes, saturated play weights, play-count ties and name ties must determine artist order.");
        Check(ranked.Single(a => a.Name == "Liked").LovedCount == 1 && ranked.Single(a => a.Name == "Played").PlayCount == 100,
            "Repeated playlists must not inflate likes or play counts.");
        Check(rankedDiscovery.Musicians(rankedPlaylist, false).Select(a => a.Name).SequenceEqual(ranked.Select(a => a.Name)),
            "Specific playlists must use the same taste ordering.");
        Check(discovery.Ownership("Artist", Album(1, "Owned", 2), out _) == CatalogOwnership.InLibrary, "Ownership must include albums outside playlists.");
        Check(discovery.Ownership("Artist", Album(2, "Partial", 3), out var count) == CatalogOwnership.Partial && count == 1, "Partial album not detected.");
        Check(discovery.Ownership("Artist", Album(3, "Original (Deluxe Edition)", 4), out _) == CatalogOwnership.PossibleEdition, "Edition match was not marked uncertain.");
        Check(discovery.Ownership("Unrelated", Album(1, "Owned", 2), out _) == CatalogOwnership.Missing, "Another artist's album was treated as owned.");
        Check(discovery.Ownership("Artist", Album(4, "Original (Live at Rome)", 2), out _) == CatalogOwnership.Missing, "Live album should not be conflated with studio edition.");
        Check(!ArtistAlbumDiscovery.MatchesType(Album(4, "Original (Live at Rome)", 2), true, true, false, false, false), "Live exclusion failed.");
        Check(!ArtistAlbumDiscovery.MatchesType(Album(4, "Best Of", 2), true, true, false, false, false), "Compilation exclusion failed.");
        Check(ArtistAlbumDiscovery.MatchesType(Album(4, "Best Of", 2), true, true, false, false, true), "Compilation opt-in failed.");
        var found = await discovery.AlbumsAsync(new(7, "Artist", null, null), CancellationToken.None);
        Check(found.Select(a => a.Title).ToHashSet().SetEquals(["Partial", "Missing"]), "Paged discography must exclude owned/future albums and retain partial albums.");
        var details = await discovery.DetailsAsync(found.Single(a => a.Title == "Missing"), CancellationToken.None);
        Check(details.Tracks.Count == 2 && details.Recommendation.DeezerAlbumId == 3 && details.Recommendation.Artist == "Artist", "Paged tracks or Soulseek metadata failed.");
        using var brokenHttp = new HttpClient(new CatalogHandler(true));
        try { await new DeezerClient(brokenHttp).ArtistAlbumsAllAsync(7); throw new Exception("Failed page was silently accepted."); }
        catch (HttpRequestException) { }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await client.ArtistAlbumsAllAsync(7, canceled.Token); throw new Exception("Canceled request completed."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Curated artists, library ownership, partial/edition labels, filters, pagination, failure and cancellation: PASS");
    }

    static DzAlbum Album(long id, string title, int count) => new(id, title, null, "2000-01-01", "album", null, TrackCount: count);
    sealed class CatalogHandler(bool failSecond = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (failSecond && path.Contains("index=100")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json = path switch
            {
                "/artist/7/albums?limit=100" => """{"data":[{"id":1,"title":"Owned","release_date":"2000-01-01","record_type":"album"},{"id":2,"title":"Partial","nb_tracks":3,"release_date":"2001-01-01","record_type":"album"}],"next":"https://api.deezer.com/artist/7/albums?index=100"}""",
                "/artist/7/albums?index=100" => """{"data":[{"id":3,"title":"Missing","nb_tracks":2,"release_date":"2002-01-01","record_type":"album"},{"id":4,"title":"Future","release_date":"2999-01-01","record_type":"album"}]}""",
                "/album/1" => """{"id":1,"title":"Owned","nb_tracks":2,"release_date":"2000-01-01","record_type":"album"}""",
                "/album/3" => """{"id":3,"title":"Missing","nb_tracks":2,"release_date":"2002-01-01","record_type":"album"}""",
                "/album/3/tracks?limit=100" => """{"data":[{"id":30,"title":"Track one"}],"next":"https://api.deezer.com/album/3/tracks?index=100"}""",
                "/album/3/tracks?index=100" => """{"data":[{"id":31,"title":"Track two"}]}""",
                _ => throw new Exception("Unexpected catalog request: " + path),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
