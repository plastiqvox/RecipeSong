using System.Net;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

/// <summary>Deezer 에 없는 발매작을 Apple 카탈로그에서 보충하는 경로 (가짜 HTTP).</summary>
static class AppleCatalogChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var owned = new TrackRecord { Artist = "Ashe", Album = "Rae", Title = "Love Is Weird" };
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var deezer = new DeezerClient(http);
        var apple = new ITunesSearchClient(http);
        var discovery = new ArtistAlbumDiscovery(deezer, [owned], [], [], apple: apple);
        var artist = new DzArtist(70, "Ashe", null, null);

        var found = await discovery.AlbumsAsync(artist, CancellationToken.None, includeOwned: true);
        var extra = found.Where(a => a.Apple != null).ToArray();
        Check(extra.Select(a => a.Title).Order().SequenceEqual(["Stop The Wedding!", "The Girl Of Your Dreams"]),
            "Apple-only releases must exclude Deezer titles, future dates, featured appearances and duplicate clean/explicit editions: " + string.Join(", ", extra.Select(a => a.Title)));
        Check(extra.Single(a => a.Title == "Stop The Wedding!").Album.RecordType == "single", "Apple ' - Single' suffix must become a single record type.");
        Check(extra.All(a => a.Album.Id == 0 && a.Apple!.Id > 0), "Apple albums must not reuse Apple IDs as Deezer album IDs.");
        Check(!handler.Requests.Any(r => r.Contains("id=901")), "Apple artists with a different name must not be considered.");
        Check(found.Single(a => a.Title == "Rae").Apple == null, "Deezer albums must stay Deezer albums.");

        var details = await discovery.DetailsAsync(extra.Single(a => a.Title == "The Girl Of Your Dreams"), CancellationToken.None);
        Check(details.Recommendation.DeezerAlbumId == 0 && details.Recommendation.Link == "https://music.apple.com/kr/album/girl/501",
            "Apple album details must keep Deezer ID 0 and a local Apple Music link.");
        Check(details.Tracks.Select(t => t.Title).SequenceEqual(["One", "Two"]), "Apple tracks must be ordered by disc and track number.");

        // 같은 아티스트 캐시는 Apple 에 다시 묻지 않는다
        var before = handler.Requests.Count(r => r.Contains("musicArtist"));
        await discovery.AlbumsAsync(artist, CancellationToken.None, includeOwned: true);
        Check(handler.Requests.Count(r => r.Contains("musicArtist")) == before, "Apple catalog lookup must be cached per Deezer artist.");

        // 동점 후보는 정하지 않는다 (동명이인 둘 다 같은 수의 제목이 겹치면 누구인지 모른다)
        var tied = await apple.MissingAlbumsAsync(71, "Twin", [Album("Same")], CancellationToken.None);
        Check(tied.Count == 0, "Tied Apple artists must not be guessed.");

        // Apple 실패는 Deezer 목록을 막지 않고, 실패는 캐시하지 않는다
        handler.FailApple = true;
        var failed = await new ArtistAlbumDiscovery(deezer, [owned], [], [], apple: apple).AlbumsAsync(new(72, "Ashe", null, null), CancellationToken.None, includeOwned: true);
        Check(failed.Count > 0 && failed.All(a => a.Apple == null), "Apple failure must keep the Deezer list.");
        handler.FailApple = false;
        var retried = await new ArtistAlbumDiscovery(deezer, [owned], [], [], apple: apple).AlbumsAsync(new(72, "Ashe", null, null), CancellationToken.None, includeOwned: true);
        Check(retried.Any(a => a.Apple != null), "Apple failure must not be cached as 'not found'.");

        await RecommendationAsync(handler, http, deezer, owned);
        Console.WriteLine("Apple catalog fallback: artist match, title dedupe, single suffix, tracks, cache, ties, failures and archive recommendations: PASS");
    }

    /// <summary>아카이브 뮤지션 추천: 뒤에서 받은 Apple 카탈로그가 후보에 음수 ID 로 들어가고, 소리·Deezer 에 묻지 않는다</summary>
    static async Task RecommendationAsync(Handler handler, HttpClient http, DeezerClient deezer, TrackRecord owned)
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var path = Path.Combine(CacheLocation.Directory, "apple-identity-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        var db = new ArtistIdentityStore(path);
        var hash = new ArtistAlbumDiscovery(deezer, [owned], [], [], identityStore: db).EvidenceHash("Ashe")!;
        db.Save(new StoredArtistIdentity("Ashe", "Ashe", 73, ArtistIdentitySource.Automatic, hash, ["album:rae"], 2, null, DateTimeOffset.UtcNow));
        var apple = new ITunesSearchClient(http) { PacedInterval = TimeSpan.Zero };
        var engine = new ReleaseRecommender(deezer, identityStore: db, apple: apple);
        engine.SetIdentityLibrary([owned]);
        var mine = new PlaylistArtist("Ashe", TasteProfile.LibraryTag, null, "", 1, new HashSet<string>());
        await engine.RefreshMineAsync([mine]);

        var before = await engine.BuildPoolAsync([], [mine]);
        Check(before.ApplePending == 1 && before.Artists.Single().Albums.All(a => a.Id > 0),
            "Apple albums must come from the background cache only, and an unchecked artist must be pending.");
        Check(await engine.RefreshAppleAsync([mine]) == 1, "Background Apple refresh must report the recent Apple-only release.");
        var pool = await engine.BuildPoolAsync([], [mine]);
        var girl = pool.Artists.Single().Albums.SingleOrDefault(a => a.Title == "The Girl Of Your Dreams");
        Check(pool.ApplePending == 0 && girl != null && AppleAlbumId.Is(girl.Id) && AppleAlbumId.Collection(girl.Id) == 501,
            "Apple-only release must join the archive artist's albums with a negative ID.");

        var period = Periods.Create(PeriodKind.Week, Handler.Recent.ToDateTime(TimeOnly.MinValue));
        var result = await engine.RecommendAsync(pool, new OwnedLibrary([owned]), period);
        var card = result.Releases.SingleOrDefault(r => r.Album == "The Girl Of Your Dreams");
        Check(card != null && AppleAlbumId.Is(card.DeezerAlbumId) && card.Taste == null && card.FromPlaylist == TasteProfile.LibraryTag
            && TasteGate.Assess(card) == TasteDecision.Recommended && card.Link == "https://music.apple.com/kr/album/girl/501",
            "Apple-only archive release must be recommended without a sound score and keep its Apple link.");
        var tracks = await engine.AlbumTracksAsync(card!.DeezerAlbumId);
        Check(tracks.Select(t => t.Title).SequenceEqual(["One", "Two"]) && tracks.All(t => t.Preview == null && t.Id == 0),
            "Apple-only tracks must list titles without previews or Deezer-like track IDs.");
        Check(!handler.Requests.Any(r => r.StartsWith("/album/-")), "Negative Apple IDs must never be sent to Deezer.");
    }

    static DzAlbum Album(string title) => new(1, title, null, "2020-01-01", "album", null);

    sealed class Handler : HttpMessageHandler
    {
        public bool FailApple;
        public List<string> Requests { get; } = [];
        /// <summary>Apple 에만 있는 앨범의 발매일 (최근 60일 안이어야 뒤 작업이 '새로 찾음'으로 센다)</summary>
        public static readonly DateOnly Recent = DateOnly.FromDateTime(DateTime.Today.AddDays(-20));
        static string Collection(long id, string name, string date, int tracks, string artist = "Ashe") =>
            $$"""{"wrapperType":"collection","collectionType":"Album","artistName":"{{artist}}","collectionId":{{id}},"collectionName":"{{name}}","releaseDate":"{{date}}T07:00:00Z","trackCount":{{tracks}},"collectionViewUrl":"https://music.apple.com/us/album/girl/{{id}}?uo=4","artworkUrl100":"https://a/100x100bb.jpg"}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.PathAndQuery;
            lock (Requests) Requests.Add(path);
            if (uri.Host == "itunes.apple.com" && FailApple) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var json = uri.Host == "itunes.apple.com" ? path switch
            {
                _ when path.StartsWith("/search?term=Ashe&entity=musicArtist") =>
                    """{"results":[{"wrapperType":"artist","artistName":"Ashe","artistId":800},{"wrapperType":"artist","artistName":"ASHE","artistId":900},{"wrapperType":"artist","artistName":"Ashes","artistId":901}]}""",
                _ when path.StartsWith("/search?term=Twin&entity=musicArtist") =>
                    """{"results":[{"artistName":"Twin","artistId":810},{"artistName":"Twin","artistId":811}]}""",
                _ when path.StartsWith("/lookup?id=800&entity=album") => "{\"results\":[{\"wrapperType\":\"artist\",\"artistId\":800}," + string.Join(",",
                    Collection(500, "Rae", "2022-10-14", 13), Collection(502, "Ashlyn", "2021-05-07", 15),
                    Collection(501, "The Girl Of Your Dreams", Recent.ToString("yyyy-MM-dd"), 2), Collection(503, "The Girl Of Your Dreams", Recent.ToString("yyyy-MM-dd"), 2),
                    Collection(504, "Stop The Wedding! - Single", "2026-08-07", 1), Collection(505, "Tomorrow", "2999-01-01", 1),
                    Collection(506, "Get Lost (feat. Ashe) - Single", "2018-04-13", 1, "Breathe Carolina")) + "]}",
                _ when path.StartsWith("/lookup?id=900&entity=album") => """{"results":[]}""",
                _ when path.StartsWith("/lookup?id=810&entity=album") || path.StartsWith("/lookup?id=811&entity=album") =>
                    "{\"results\":[" + Collection(600, "Same", "2020-01-01", 1, "Twin") + "]}",
                _ when path.StartsWith("/lookup?id=501&entity=song") =>
                    """{"results":[{"wrapperType":"collection","collectionId":501},{"wrapperType":"track","kind":"song","trackId":2,"trackName":"Two","discNumber":1,"trackNumber":2},{"wrapperType":"track","kind":"song","trackId":1,"trackName":"One","discNumber":1,"trackNumber":1,"previewUrl":"https://p/1.m4a"}]}""",
                _ => throw new Exception("Unexpected Apple request: " + path),
            } : path switch
            {
                "/artist/70/albums?limit=100" or "/artist/72/albums?limit=100" or "/artist/73/albums?limit=100" =>
                    """{"data":[{"id":1,"title":"Rae","nb_tracks":13,"release_date":"2022-10-14","record_type":"album"},{"id":2,"title":"Ashlyn","nb_tracks":15,"release_date":"2021-05-07","record_type":"album"}]}""",
                _ => throw new Exception("Unexpected Deezer request: " + path),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
