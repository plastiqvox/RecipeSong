using System.Net;
using System.Text;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class ArtistStatsChecks
{
    public static async Task RunAsync()
    {
        // Last.fm 청취자 수를 알면 Deezer 팬 수보다 먼저 본다
        if (ReleaseRecommender.SmallArtistBonus(514_311, 3_880) != 0) throw new Exception("Deezer fans overrode known Last.fm listeners.");
        if (ReleaseRecommender.SmallArtistBonus(40_000, 2_000_000) != 0.4) throw new Exception("Small Last.fm audience was not rewarded.");
        if (ReleaseRecommender.SmallArtistBonus(2_900_000, 0) != -0.2) throw new Exception("Large Last.fm audience was not penalised.");
        if (ReleaseRecommender.SmallArtistBonus(null, 3_880) != 0.4) throw new Exception("Deezer fallback changed.");
        if (ReleaseRecommender.SmallArtistBonus(null, 0) != null) throw new Exception("Unknown popularity produced a bonus.");

        var settings = new GenreSourceSettings();
        settings.SetLastFmKey("test-key");
        var handler = new Fixture();
        var genres = new GenreSources(new HttpClient(handler), settings);
        var stats = await genres.ArtistStatsAsync("Known Artist", CancellationToken.None);
        if (stats is not { Listeners: 48_656, Mbid: "00000000-0000-0000-0000-000000000005" }) throw new Exception("Artist listeners or MBID were not read.");
        if (await genres.ArtistStatsAsync("Missing Artist", CancellationToken.None) != null
            || !genres.TryCachedArtistStats("Missing Artist", out var missing) || missing != null)
            throw new Exception("A missing Last.fm artist was not remembered.");
        int requests = handler.Requests;
        await genres.ArtistStatsAsync("Missing Artist", CancellationToken.None);
        await genres.ArtistStatsAsync("Known Artist", CancellationToken.None);
        if (handler.Requests != requests) throw new Exception("Cached artist stats were requested again.");

        var failing = new GenreSources(new HttpClient(new Failure()), settings);
        if (await failing.ArtistStatsAsync("Offline Artist", CancellationToken.None) != null || failing.TryCachedArtistStats("Offline Artist", out _))
            throw new Exception("A network failure was cached.");
        Console.WriteLine("Artist stats: Last.fm listeners first, Deezer fallback, missing artist cache and uncached failures passed.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var json = request.RequestUri!.Query.Contains("Missing")
                ? """{"error":6,"message":"The artist you supplied could not be found"}"""
                : """{"artist":{"name":"Known Artist","mbid":"00000000-0000-0000-0000-000000000005","stats":{"listeners":"48656","playcount":"900000"}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Failure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }
}
