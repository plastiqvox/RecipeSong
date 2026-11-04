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
        // 아직 Last.fm 에 묻지 못한 아티스트는 Deezer 가산점을 절반만 (감점도 절반)
        if (ReleaseRecommender.SmallArtistBonus(null, 3_880, pending: true) != 0.2
            || ReleaseRecommender.SmallArtistBonus(null, 2_000_000, pending: true) != -0.1)
            throw new Exception("Pending Last.fm lookups did not halve the Deezer bonus.");

        var settings = new GenreSourceSettings();
        settings.SetLastFmKey("test-key");
        var handler = new Fixture();
        var genres = new GenreSources(new HttpClient(handler), settings);
        var stats = await genres.ArtistStatsAsync("Known Artist", CancellationToken.None);
        if (stats is not { Listeners: 48_656, Mbid: "00000000-0000-0000-0000-000000000005" }) throw new Exception("Artist listeners or MBID were not read.");
        if (await genres.ArtistStatsAsync("Missing Artist", CancellationToken.None) != null
            || !genres.TryCachedArtistStats("Missing Artist", out var missing) || missing != null)
            throw new Exception("A missing Last.fm artist was not remembered.");
        if (await genres.ArtistStatsAsync("Empty Artist", CancellationToken.None) != null
            || !genres.TryCachedArtistStats("Empty Artist", out var empty) || empty != null)
            throw new Exception("Zero Last.fm listeners were treated as a known audience.");
        int requests = handler.Requests;
        await genres.ArtistStatsAsync("Missing Artist", CancellationToken.None);
        await genres.ArtistStatsAsync("Known Artist", CancellationToken.None);
        if (handler.Requests != requests) throw new Exception("Cached artist stats were requested again.");

        var failing = new GenreSources(new HttpClient(new Failure()), settings);
        if (await failing.ArtistStatsAsync("Offline Artist", CancellationToken.None) != null || failing.TryCachedArtistStats("Offline Artist", out _))
            throw new Exception("A network failure was cached.");
        var slow = new GenreSources(new HttpClient(new TimingOut(null)), settings);
        if (await slow.ArtistStatsAsync("Slow Artist", CancellationToken.None) != null || slow.TryCachedArtistStats("Slow Artist", out _))
            throw new Exception("An HTTP timeout escaped as a cancellation or was cached.");
        // 요청 도중 사용자가 취소하면 같은 TaskCanceledException 이라도 그대로 올라가야 한다
        using var cancel = new CancellationTokenSource();
        try
        {
            await new GenreSources(new HttpClient(new TimingOut(cancel)), settings).ArtistStatsAsync("Other Artist", cancel.Token);
            throw new Exception("User cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
        // 예전 빌드가 캐시에 넣어 둔 청취자 0 도 모름
        if (ReleaseRecommender.SmallArtistBonus(0, 2_000_000) != -0.2) throw new Exception("Cached zero listeners were treated as a tiny audience.");
        Console.WriteLine("Artist stats: Last.fm listeners first, pending half bonus, Deezer fallback, zero and missing artist cache, timeouts and uncached failures passed.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var query = request.RequestUri!.Query;
            var json = query.Contains("Missing")
                ? """{"error":6,"message":"The artist you supplied could not be found"}"""
                : query.Contains("Empty")
                ? """{"artist":{"name":"Empty Artist","mbid":"","stats":{"listeners":"0","playcount":"0"}}}"""
                : """{"artist":{"name":"Known Artist","mbid":"00000000-0000-0000-0000-000000000005","stats":{"listeners":"48656","playcount":"900000"}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Failure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }

    // cancel 이 없으면 HttpClient.Timeout 이 지났을 때처럼 호출자의 토큰은 그대로인 채, 있으면 그 토큰을 취소한 뒤 TaskCanceledException 을 던진다
    private sealed class TimingOut(CancellationTokenSource? cancel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancel?.Cancel();
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out."));
        }
    }
}
