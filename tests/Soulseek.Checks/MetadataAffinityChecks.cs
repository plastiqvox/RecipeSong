using System.Net;
using System.Text;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class MetadataAffinityChecks
{
    public static async Task RunAsync()
    {
        using var document = JsonDocument.Parse("""
        {"relations":[
          {"type":"producer","artist":{"id":"00000000-0000-0000-0000-000000000001","name":"Producer"}},
          {"type":"producer","artist":{"id":"00000000-0000-0000-0000-000000000001","name":"Producer"}},
          {"type":"engineer","artist":{"id":"00000000-0000-0000-0000-000000000002","name":"Engineer"}},
          {"type":"composer","artist":{"id":"invalid","name":"Invalid"}}]}
        """);
        if (MetadataAffinity.ReadCredits(document.RootElement).Count != 1) throw new Exception("Duplicate, unsupported or invalid credits included.");
        using var handler = new Fixture();
        using var client = new HttpClient(handler);
        var tagCache = new JsonCache<List<string>>("genre-album-tags", TimeSpan.FromDays(180));
        foreach (var (artist, album) in new[] { ("Seed", "Seed album"), ("Candidate", "Candidate album"), ("Wrong", "Wrong album") })
            tagCache.Set($"{ListeningAnalyzer.NormalizeArtist(artist)}|{TasteProfile.TitleKey(album)}", ["dream-pop", "rock"]);
        tagCache.Save();
        var artistTags = new JsonCache<List<string>>("genre-artist-tags", TimeSpan.FromDays(365));
        tagCache.Set($"{ListeningAnalyzer.NormalizeArtist("Fallback")}|{TasteProfile.TitleKey("Generic album")}", ["pop"]);
        tagCache.Save();
        artistTags.Set(ListeningAnalyzer.NormalizeArtist("Fallback"), ["dream pop"]);
        artistTags.Save();
        var genres = new GenreSources(client, new GenreSourceSettings());
        if (genres.CachedTags("Fallback", "Generic album", 8, t => t != "pop").Single() != "dream pop")
            throw new Exception("Generic album tags masked specific artist tags.");
        var affinity = new MetadataAffinity(genres, client);
        TrackRecord[] tracks = [new() { Artist = "Seed", Album = "Seed album", Title = "Liked track", Loved = true }];
        var matches = await affinity.ScoreAsync(tracks, [(1, "Candidate", "Candidate album"), (2, "Wrong", "Wrong album")], CancellationToken.None);
        var match = matches[1];
        if (match.Tag != "dream pop" || match.Crew != "Producer" || match.Bonus <= 0.3 || match.Bonus > 0.35)
            throw new Exception("Style and producer combination not applied or exceeded its cap.");
        if (matches[2].Credits != 0) throw new Exception("Wrong artist search result was trusted.");
        int requests = handler.MusicBrainzRequests;
        var cached = await new MetadataAffinity(genres, client).ScoreAsync(tracks, [(1, "Candidate", "Candidate album")], CancellationToken.None);
        if (handler.MusicBrainzRequests != requests || cached[1].Bonus != match.Bonus)
            throw new Exception("Credit cache did not survive a new scorer instance.");
        var neutral = await affinity.ScoreAsync([], [(1, "Candidate", "Candidate album")], CancellationToken.None);
        if (neutral.Count != 0) throw new Exception("An empty preference library generated affinity.");
        using var failureClient = new HttpClient(new Failure());
        var missing = await new MetadataAffinity(null, failureClient).ScoreAsync(tracks,
            [(3, "Unavailable", "Missing album")], CancellationToken.None);
        if (missing[3].Bonus != 0) throw new Exception("Missing metadata changed the score.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await affinity.ScoreAsync(tracks, [(1, "Candidate", "Candidate album")], cancelled.Token);
            throw new Exception("User cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("Metadata affinity: role filtering, combined bonus cap, wrong artist rejection, persistent cache and neutral ownership passed.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public int MusicBrainzRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string json;
            if (uri.Host == "ws.audioscrobbler.com") json = """{"toptags":{"tag":[{"name":"dream-pop","count":100},{"name":"rock","count":100}]}}""";
            else
            {
                MusicBrainzRequests++;
                if (uri.Query.Contains("query="))
                {
                    var query = Uri.UnescapeDataString(uri.Query);
                    string artist = query.Contains("Seed") ? "Seed" : query.Contains("Candidate") ? "Candidate" : "Another person";
                    string album = query.Contains("Seed") ? "Seed album" : query.Contains("Candidate") ? "Candidate album" : "Wrong album";
                    json = """{"releases":[{"id":"00000000-0000-0000-0000-000000000004","title":"ALBUM","artist-credit":[{"artist":{"id":"00000000-0000-0000-0000-000000000003","name":"ARTIST"}}]}]}""".Replace("ALBUM", album).Replace("ARTIST", artist);
                }
                else json = """{"relations":[{"type":"producer","artist":{"id":"00000000-0000-0000-0000-000000000001","name":"Producer"}}]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Failure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }
}
