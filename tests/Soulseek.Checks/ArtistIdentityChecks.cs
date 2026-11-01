using System.Net;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class ArtistIdentityChecks
{
    public static async Task RunAsync()
    {
        static TrackRecord Song(string album, string title) => new() { Artist = "Shared", Album = album, Title = title };
        var all = new[] { Song("Amber Lights", "Morning Sun"), Song("Blue Skies", "Evening Moon") };
        foreach (var mode in new[] { "albums", "songs", "ambiguous", "failed", "none", "album-search", "generic", "editions" })
        {
            using var http = new HttpClient(new Handler(mode));
            var library = mode switch
            {
                "songs" => new[] { all[0], Song("Amber Lights", "Evening Moon") },
                "generic" => new[] { Song("Greatest Hits", "Intro"), Song("Live", "Outro") },
                "editions" => new[] { all[0], Song("Amber Lights (Deluxe Edition)", "Morning Sun") },
                _ => all,
            };
            var playlist = new Playlist("Favorites", "1", null, PlaylistKind.User, [library[0]]);
            var discovery = new ArtistAlbumDiscovery(new DeezerClient(http), library, [playlist], [new(1, "Shared", "", DateTime.Now)]);
            var resolved = await discovery.ResolveArtistAsync(discovery.Musicians().Single(), CancellationToken.None);
            var expected = mode is "albums" or "songs" or "album-search" ? (long?)2 : null;
            if (resolved.SelectedId != expected) throw new Exception($"Identity mode {mode}: expected {expected}, got {resolved.SelectedId}.");
            if (mode == "songs" && resolved.Candidates.Single(c => c.Artist.Id == 2).TrackMatches != 2)
                throw new Exception("Single-album evidence must compare distinct song titles.");
            if (mode == "album-search" && !resolved.Candidates.Any(c => c.Artist.Id == 2))
                throw new Exception("Album search failed to recover a candidate missing from name search.");
        }
        using var followHttp = new HttpClient(new Handler("no requests"));
        var followDiscovery = new ArtistAlbumDiscovery(new DeezerClient(followHttp), [], [], [new(99, "Follow only", "", DateTime.Now)]);
        var follow = followDiscovery.Musicians().Single();
        if ((await followDiscovery.ResolveArtistAsync(follow, CancellationToken.None)).SelectedId != 99)
            throw new Exception("Explicit follow-only ID was not preserved.");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await followDiscovery.ResolveArtistAsync(follow, cancel.Token); throw new Exception("Identity lookup ignored cancellation."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Artist identity: owned albums/tracks, wrong follow ID, ambiguous/failed/generic/edition evidence, album-search recovery and cancellation: PASS");
    }

    sealed class Handler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            static DzArtist Artist(long id) => new(id, "Shared", null, null, id == 1 ? 100000 : 1);
            static DzAlbum Album(long id, string title, long artist) => new(id, title, null, "2000-01-01", "album", null, Artist: Artist(artist));
            object data;
            if (path == "/search/artist") data = mode == "album-search" ? new[] { Artist(1) } : new[] { Artist(1), Artist(2) };
            else if (path == "/search/album") data = mode is "generic" or "none" ? Array.Empty<DzAlbum>() : new[] { Album(201, "Amber Lights", 2) };
            else if (path is "/artist/1/albums" or "/artist/2/albums")
            {
                var id = path.Contains("/1/") ? 1 : 2;
                if (id == 1 && mode == "failed") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var correct = id == 2 || mode == "ambiguous";
                data = mode == "none" ? new[] { Album(id * 100 + 1, "Unrelated", id) }
                    : mode == "generic" ? new[] { Album(id * 100 + 1, "Greatest Hits", id), Album(id * 100 + 2, "Live", id) }
                    : correct || mode is "songs" or "editions" ? new[] { Album(id * 100 + 1, "Amber Lights", id), Album(id * 100 + 2, "Blue Skies", id) }
                    : new[] { Album(id * 100 + 1, "Unrelated", id) };
            }
            else if (path.StartsWith("/album/") && path.EndsWith("/tracks"))
            {
                var correct = path.Contains("/201/");
                data = correct ? new[] { new DzTrack("Morning Sun", null, null), new DzTrack("Evening Moon", null, null) }
                    : new[] { new DzTrack("Other Track", null, null) };
            }
            else throw new Exception("Unexpected identity request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data })) });
        }
    }
}
