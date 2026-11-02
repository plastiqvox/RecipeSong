using System.Net;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class FollowedDuplicateChecks
{
    public static async Task RunAsync()
    {
        var shared = new DzAlbum(819085511, "Children of the Baked Potato", null, "2000-05-01", "album", null);
        var cache = new JsonCache<List<DzAlbum>>("deezer-discography", TimeSpan.FromDays(3));
        cache.Set("546520", [shared, shared, shared with { Id = 101, Title = "First artist solo" }]);
        cache.Set("14425359", [shared, shared with { Id = 102, Title = "Second artist solo" }]);
        cache.Save();
        var handler = new AlbumHandler();
        using var http = new HttpClient(handler);
        var recommender = new ReleaseRecommender(new DeezerClient(http));
        var follows = new[]
        {
            new PlaylistArtist("First artist", FollowList.Tag, null, "", 0, new HashSet<string>(), 546520),
            new PlaylistArtist("Second artist", FollowList.Tag, null, "", 0, new HashSet<string>(), 14425359),
        };
        var result = await recommender.FollowedAsync(follows, new OwnedLibrary([]),
            Periods.Create(PeriodKind.Month, new(2000, 5, 1)), new RecommendOptions());
        var indexed = result.ToDictionary(r => r.DeezerAlbumId);
        if (indexed.Count != 3 || result.Count(r => r.DeezerAlbumId == shared.Id) != 1
            || result.Any(r => r.FromPlaylist != FollowList.Tag) || handler.Requests != 3)
            throw new Exception("A shared followed album was duplicated, scored repeatedly, or displaced a solo release.");
        Console.WriteLine("팔로우 2명 공유 앨범 819085511·아티스트 내부 중복·개별 앨범 보존·단일 분석 검사 통과");
    }

    sealed class AlbumHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.StartsWith("/album/", StringComparison.Ordinal))
                throw new Exception("Unexpected request: " + request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"label":"Test label","genres":{"data":[]},"contributors":[]} """) });
        }
    }
}
