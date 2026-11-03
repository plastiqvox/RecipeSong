using System.Net;
using System.Text.Json;
using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

/// <summary>청취·좋아요가 있는 라이브러리 앨범의 세부 태그와 제작진. ZIP 보유는 취향 자료로 쓰지 않는다.</summary>
public sealed class MetadataAffinity(GenreSources? genres, HttpClient? http = null)
{
    public sealed record Credit(string Id, string Name, string Role);
    public sealed record Match(double Tags, double Credits, string? Tag, string? Crew)
    {
        // 소리 점수를 뒤집지 않게 상한을 둔다. 두 신호가 겹쳐도 추가 보상은 작게.
        public double Bonus => Math.Clamp(0.15 * Tags + 0.15 * Credits + 0.05 * Math.Min(Tags, Credits), 0, 0.35);
    }

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _nextRequest;
    private static DateTime _retryAfter;
    private readonly JsonCache<List<Credit>> _credits = new("musicbrainz-album-credits-v1", TimeSpan.FromDays(180));
    private readonly SemaphoreSlim _scoring = new(1, 1);
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RecipeSong/0.3.0 (+https://github.com/plastiqvox/RecipeSong)");
        return http;
    }

    private static string Key(string artist, string album) => $"{ListeningAnalyzer.NormalizeArtist(artist)}|{ListeningAnalyzer.NormalizeArtist(album)}";
    public static string NormalizeTag(string tag) => string.Join(' ', tag.Trim().ToLowerInvariant().Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static bool Specific(string tag) => GenreGroups.IsGenre(tag) && NormalizeTag(tag) is not
        ("pop" or "rock" or "indie" or "alternative" or "electronic" or "jazz" or "hip hop" or "r&b" or "classical" or "folk" or "soul");

    public async Task<IReadOnlyDictionary<long, Match>> ScoreAsync(
        IReadOnlyList<TrackRecord> library, IReadOnlyList<(long Id, string Artist, string Album)> albums,
        CancellationToken ct)
    {
        await _scoring.WaitAsync(ct);
        try { return await ScoreCoreAsync(library, albums, ct); }
        finally { _scoring.Release(); }
    }

    private async Task<IReadOnlyDictionary<long, Match>> ScoreCoreAsync(
        IReadOnlyList<TrackRecord> library, IReadOnlyList<(long Id, string Artist, string Album)> albums,
        CancellationToken ct)
    {
        var seeds = library.Where(t => !t.Disliked && t.Album.Length > 0)
            .GroupBy(t => (Artist: t.GroupArtist, t.Album))
            .Select(g => (g.Key.Artist, g.Key.Album, Weight: g.Sum(t => Math.Max(0, t.Weight - 0.35))))
            .Where(a => a.Weight > 0).OrderByDescending(a => a.Weight).Take(12).ToArray();
        if (seeds.Length == 0 || albums.Count == 0) return new Dictionary<long, Match>();
        var tags = new Dictionary<string, double>();
        var crew = new Dictionary<string, double>();
        var total = seeds.Sum(s => s.Weight);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        int seedFetches = 0, candidateFetches = 0;
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            await TagsAsync(seed.Artist, seed.Album, budget.Token);
            foreach (var tag in Tags(seed.Artist, seed.Album))
                tags[tag] = tags.GetValueOrDefault(tag) + seed.Weight / total;
            var key = Key(seed.Artist, seed.Album);
            if (!_credits.TryGet(key, out _) && seedFetches++ < 3)
                await FetchCreditsAsync(seed.Artist, seed.Album, budget.Token);
            if (_credits.TryGet(key, out var credits))
                foreach (var credit in credits.DistinctBy(c => (c.Id, c.Role)))
                {
                    var ck = $"{credit.Role}:{credit.Id}";
                    crew[ck] = crew.GetValueOrDefault(ck) + seed.Weight / total;
                }
        }
        var result = new Dictionary<long, Match>();
        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            await TagsAsync(album.Artist, album.Album, budget.Token);
            var candidateTags = Tags(album.Artist, album.Album);
            var tag = candidateTags.OrderByDescending(t => tags.GetValueOrDefault(t)).FirstOrDefault();
            double tagScore = candidateTags.Select(t => tags.GetValueOrDefault(t)).OrderDescending().Take(3).DefaultIfEmpty(0).Average();
            var key = Key(album.Artist, album.Album);
            if (!_credits.TryGet(key, out _) && candidateFetches++ < 3)
                await FetchCreditsAsync(album.Artist, album.Album, budget.Token);
            Credit? best = _credits.TryGet(key, out var credits) ? credits
                .OrderByDescending(c => CrewScore(c, crew)).FirstOrDefault() : null;
            double creditScore = best == null ? 0 : CrewScore(best, crew);
            result[album.Id] = new(tagScore, creditScore, tagScore > 0 ? tag : null, creditScore > 0 ? best?.Name : null);
        }
        ct.ThrowIfCancellationRequested();
        _credits.Save();
        genres?.Save();
        return result;
    }

    private static double CrewScore(Credit c, Dictionary<string, double> profile) =>
        profile.GetValueOrDefault($"{c.Role}:{c.Id}") * (c.Role switch { "producer" => 1, "composer" => 0.7, _ => 0.35 });
    private string[] Tags(string artist, string album) =>
        [.. (genres?.CachedTags(artist, album, 8, Specific) ?? []).Select(NormalizeTag).Distinct()];
    private async Task TagsAsync(string artist, string album, CancellationToken ct)
    {
        try { if (genres is { Enabled: true }) await genres.GroupAsync(artist, album, cachedOnly: false, ct); }
        catch (OperationCanceledException) { } // 메타데이터 조회 시간 제한. 사용자 취소는 바깥 반복문에서 확인한다.
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _retryAfter) return null;
            var delay = _nextRequest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _nextRequest = DateTime.UtcNow.AddMilliseconds(1100);
            using var response = await (http ?? Http).GetAsync("https://musicbrainz.org/ws/2/" + path, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _retryAfter = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        finally { Gate.Release(); }
    }

    private async Task FetchCreditsAsync(string artist, string album, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            static string Quote(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string query = Uri.EscapeDataString($"artist:\"{Quote(artist)}\" AND release:\"{Quote(album)}\"");
            using var search = await GetAsync($"release/?query={query}&fmt=json&limit=5", ct);
            if (search == null || !search.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return;
            var matches = releases.EnumerateArray().Where(r =>
                ListeningAnalyzer.NormalizeArtist(String(r, "title") ?? "") == ListeningAnalyzer.NormalizeArtist(album) &&
                Artists(r).Any(a => ListeningAnalyzer.NormalizeArtist(String(a, "name") ?? "") == ListeningAnalyzer.NormalizeArtist(artist))).ToArray();
            // 동명이인 후보가 여럿이면 이름만으로 제작진을 연결하지 않는다.
            var identities = matches.SelectMany(Artists).Select(a => String(a, "id")).Distinct().ToArray();
            if (matches.Length == 0 || identities.Length != 1 || !Guid.TryParse(identities[0], out _)) { _credits.Set(Key(artist, album), []); return; }
            var id = String(matches[0], "id");
            if (!Guid.TryParse(id, out _)) return;
            var inc = Uri.EscapeDataString("artist-rels+recordings+recording-level-rels+work-rels+work-level-rels");
            using var detail = await GetAsync($"release/{id}?inc={inc}&fmt=json", ct);
            if (detail == null) return;
            _credits.Set(Key(artist, album), ReadCredits(detail.RootElement));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { }
    }

    private static string? String(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IEnumerable<JsonElement> Artists(JsonElement release) => release.ValueKind == JsonValueKind.Object &&
        release.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array
        ? credits.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("artist", out _))
            .Select(c => c.GetProperty("artist")) : [];

    public static List<Credit> ReadCredits(JsonElement element)
    {
        var found = new List<Credit>();
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (String(node, "type") is { } role &&
                    role is "producer" or "composer" or "instrument" or "vocal" &&
                    node.TryGetProperty("artist", out var artist) && String(artist, "id") is { } id &&
                    Guid.TryParse(id, out _) && String(artist, "name") is { } name)
                    found.Add(new(id, name, role));
                foreach (var property in node.EnumerateObject()) Walk(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Walk(item);
        }
        Walk(element);
        return [.. found.DistinctBy(c => (c.Id, c.Role))];
    }
}
