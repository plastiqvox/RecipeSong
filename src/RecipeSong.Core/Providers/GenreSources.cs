using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeSong.Core.Providers;

/// <summary>
/// 추천 칸 (장르 묶음). 값은 캐시·칩 순서에 쓰는 데이터라 번역하지 않고, 보일 때만 L.T 로 번역한다.
/// 국적은 나누지 않는다: 일본 인디도 영국 인디도 같은 '인디·록'.
/// </summary>
public static class GenreGroups
{
    public const string Indie = "인디·록", Electronic = "일렉트로닉", HipHop = "힙합·R&B", Jazz = "재즈", Classical = "클래식·OST", Other = "그 밖";

    /// <summary>기본 칩 순서</summary>
    public static readonly string[] All = [Indie, Electronic, HipHop, Jazz, Classical, Other];

    // 태그 → 칸. 위에서부터 처음 맞는 규칙 (구체적인 말을 먼저: "chamber pop" 은 클래식이 아니라 인디, "synth-pop" 은 일렉트로닉).
    // "pop" · "k-pop" · "j-pop" · "asian music" · 국적 · 연도 같은 넓은 태그는 칸을 정하지 못한다 → 다음 태그나 소리로.
    private static readonly (string[] Words, string Group)[] Rules =
    [
        (["dream pop", "indie pop", "noise pop", "jangle", "twee", "chamber pop", "art pop", "baroque pop", "power pop", "bedroom pop", "shoegaze"], Indie),
        (["soundtrack", "score", "film", "game", "stage & screen", "musical", "classical", "opera", "orchestral", "baroque", "choral", "neoclassical",
          "chamber music"], Classical),
        (["jazz", "bossa nova", "bebop", "big band", "swing"], Jazz),
        (["hip hop", "hip-hop", "hiphop", "rap", "trap", "grime", "drill", "r&b", "rnb", "rhythm and blues", "soul", "funk", "gospel", "boom bap"], HipHop),
        (["indie"], Indie),
        (["electronic", "electronica", "electro", "house", "techno", "ambient", "idm", "dance", "disco", "synth", "edm", "trance", "drum and bass",
          "drum n bass", "dnb", "dubstep", "downtempo", "trip-hop", "trip hop", "club", "breakbeat", "footwork", "jungle", "uk garage", "chillwave",
          "vaporwave", "glitch", "leftfield", "minimal", "acid", "hardcore techno", "hyperpop"], Electronic),
        (["rock", "punk", "alternative", "folk", "singer-songwriter", "singer & songwriter", "singer/songwriter", "metal", "emo", "grunge", "post-",
          "psychedelic", "garage", "lo-fi", "lofi", "slowcore", "americana", "country", "blues", "math", "noise", "krautrock", "acoustic"], Indie),
        (["reggae", "dub", "ska", "latin", "brazil", "mpb", "samba", "afro", "african", "world", "cumbia", "salsa", "flamenco", "fado", "highlife"], Other),
    ];

    /// <summary>태그 하나 → 칸 (모르면 null)</summary>
    public static string? Of(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().ToLowerInvariant();
        foreach (var (words, group) in Rules)
            if (words.Any(w => t.Contains(w))) return group;
        return null;
    }

    /// <summary>순서대로 본 태그 중 처음 칸이 정해지는 것</summary>
    public static string? Of(IEnumerable<string>? tags) => tags?.Select(Of).FirstOrDefault(g => g != null);

    /// <summary>장르 배지로 보일 만한 태그: 칸이 정해지는 태그, 또는 칸은 못 정해도 장르 이름인 "pop" 계열 (k-pop · city pop)</summary>
    public static bool IsGenre(string? tag) => Of(tag) != null || (tag?.Contains("pop", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// 장르 DB 키: %LOCALAPPDATA%\RecipeSong\genre-sources.json. 키는 Windows 계정으로 암호화(DPAPI).
/// Last.fm 은 앱에 넣어 배포하는 키(BuiltInLastFmKey)가 있으면 그걸 쓰고, 사용자가 넣은 키가 있으면 그게 먼저다.
/// Discogs 는 개인 토큰을 넣었을 때만 쓴다 (앱 비밀 키를 배포 파일에 넣지 않는다).
/// </summary>
public sealed class GenreSourceSettings
{
    /// <summary>
    /// 배포용 Last.fm API 키 (읽기 전용 조회라 비밀 키는 필요 없다). 빌드할 때 secrets/lastfm.key 에서 어셈블리 메타데이터로 들어온다
    /// (RecipeSong.Core.csproj). 비어 있으면 사용자가 넣은 키만
    /// </summary>
    public static readonly string BuiltInLastFmKey =
        typeof(GenreSourceSettings).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "LastFmApiKey")?.Value ?? "";

    public string? LastFmEncryptedKey { get; set; }
    public string? DiscogsEncryptedToken { get; set; }

    private static string FilePath => Path.Combine(JsonCache<object>.Dir, "genre-sources.json");

    public static GenreSourceSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<GenreSourceSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool HasStoredLastFmKey => LastFmEncryptedKey != null;
    public bool HasStoredDiscogsToken => DiscogsEncryptedToken != null;

    public void SetLastFmKey(string? key) => LastFmEncryptedKey = Protect(key, "RecipeSong.LastFm");
    public void SetDiscogsToken(string? token) => DiscogsEncryptedToken = Protect(token, "RecipeSong.Discogs");

    public string? LastFmKey => Unprotect(LastFmEncryptedKey, "RecipeSong.LastFm") ?? (BuiltInLastFmKey.Length > 0 ? BuiltInLastFmKey : null);
    public string? DiscogsToken => Unprotect(DiscogsEncryptedToken, "RecipeSong.Discogs");

    private static string? Protect(string? key, string entropy) =>
        string.IsNullOrWhiteSpace(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? stored, string entropy)
    {
        if (stored == null) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// 앨범 장르 태그: Last.fm(앨범 태그 → 아티스트 태그) 먼저, 칸이 안 정해지면 Discogs(스타일 → 장르, 토큰이 있을 때만).
/// 태그는 잘 바뀌지 않아 오래 캐시한다. 못 찾은 것(빈 목록)도 캐시해 다시 묻지 않는다 (네트워크 실패는 캐시하지 않음).
/// 한도: Last.fm 은 IP 마다 초당 5회 안팎으로 알려져 있어 250ms, Discogs 는 분당 60회라 1.1초 간격.
/// </summary>
public sealed class GenreSources
{
    private readonly HttpClient _http;
    private readonly JsonCache<List<string>> _albumTags = new("genre-album-tags", TimeSpan.FromDays(180));
    private readonly JsonCache<List<string>> _artistTags = new("genre-artist-tags", TimeSpan.FromDays(365));
    private readonly JsonCache<List<string>> _discogs = new("genre-discogs", TimeSpan.FromDays(365));
    // Last.fm 비슷한 아티스트 (후보 모으기에 Deezer 연관과 같이 쓴다). Deezer 연관 캐시와 같은 14일
    private readonly JsonCache<List<SimilarArtist>> _similar = new("lastfm-similar", TimeSpan.FromDays(14));
    private readonly Throttle _lastFmGate = new(TimeSpan.FromMilliseconds(250));
    private readonly Throttle _discogsGate = new(TimeSpan.FromMilliseconds(1100));

    public GenreSourceSettings Settings { get; set; }

    public GenreSources(HttpClient http, GenreSourceSettings settings)
    {
        _http = http;
        Settings = settings;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RecipeSong/0.1 (personal music discovery desktop app)");
    }

    public bool Enabled => Settings.LastFmKey != null || Settings.DiscogsToken != null;

    /// <summary>Last.fm 키가 있다 (비슷한 아티스트를 물을 수 있다)</summary>
    public bool HasLastFm => Settings.LastFmKey != null;

    /// <summary>
    /// Last.fm 비슷한 아티스트 (artist.getSimilar, 닮음 순). Last.fm 은 청취 기록 전체로 잇기 때문에 Deezer 연관이 약한 인디·한국 아티스트도 잘 잇는다.
    /// 키가 없거나 실패하면 빈 목록 (네트워크 실패는 캐시하지 않는다)
    /// </summary>
    public async Task<IReadOnlyList<SimilarArtist>> SimilarArtistsAsync(string artist, int limit, CancellationToken ct)
    {
        if (Settings.LastFmKey is not { } key) return [];
        var k = $"{ArtistKey(artist)}:{limit}";
        if (_similar.TryGet(k, out var hit)) return hit;
        await _lastFmGate.WaitAsync(ct);
        try
        {
            var r = await _http.GetFromJsonAsync<LastFmSimilar>(
                $"https://ws.audioscrobbler.com/2.0/?method=artist.getsimilar&artist={Esc(artist)}&limit={limit}&autocorrect=1&api_key={key}&format=json", ct);
            if (r == null) return [];
            if (r.Error != null)
            {
                if (r.Error == 6) _similar.Set(k, []);  // 6: 그런 아티스트 없음
                return [];
            }
            List<SimilarArtist> list = [.. (r.Similar?.Artist ?? [])
                .Where(a => a.Name is { Length: > 0 })
                .Select(a => new SimilarArtist(a.Name!, double.TryParse(a.Match, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0))];
            _similar.Set(k, list);
            return list;
        }
        catch (HttpRequestException) { return []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// 칸을 정할 때까지 차례로 본다: Last.fm 앨범 태그 → 아티스트 태그 → Discogs.
    /// cachedOnly 면 캐시만 보고, 묻지 않은 곳이 남아 칸을 못 정했으면 NeedsFetch=true. 어디에도 없으면 Group=null
    /// </summary>
    public async Task<(string? Group, bool NeedsFetch)> GroupAsync(string artist, string album, bool cachedOnly, CancellationToken ct)
    {
        var steps = new List<(JsonCache<List<string>> Cache, string Key, Func<Task<List<string>?>> Fetch)>();
        if (Settings.LastFmKey is { } key)
        {
            steps.Add((_albumTags, AlbumKey(artist, album),
                () => LastFmTags(key, "album.gettoptags", $"&artist={Esc(artist)}&album={Esc(SearchTitle(album))}", ct)));
            steps.Add((_artistTags, ArtistKey(artist), () => LastFmTags(key, "artist.gettoptags", $"&artist={Esc(artist)}", ct)));
        }
        if (Settings.DiscogsToken is { } token)
            steps.Add((_discogs, AlbumKey(artist, album), () => DiscogsTags(token, artist, album, ct)));
        foreach (var (cache, k, fetch) in steps)
        {
            List<string>? tags;
            if (cache.TryGet(k, out var hit)) tags = hit;
            else if (cachedOnly) return (null, true);
            else if ((tags = await fetch()) != null) cache.Set(k, tags);  // 네트워크 실패(null)는 캐시하지 않는다
            if (GenreGroups.Of(tags) is { } g) return (g, false);
        }
        return (null, false);
    }

    /// <summary>
    /// 카드 장르 배지: 받아 둔 태그 중 장르로 읽히는 것 최대 max 개. Last.fm 앨범 태그 → 아티스트 태그 → Discogs 중 먼저 나오는 곳 하나에서만 고른다.
    /// 캐시만 본다 (묻지 않는다 — 못 받은 앨범은 WarmGenresAsync 가 채운 뒤 다음에 보일 때 붙는다). "seen live" · 국적 · 아티스트 이름 같은 태그는 뺀다
    /// </summary>
    public IReadOnlyList<string> CachedTags(string artist, string album, int max = 3)
    {
        var self = ArtistKey(artist);
        foreach (var (cache, k) in new[] { (_albumTags, AlbumKey(artist, album)), (_artistTags, self), (_discogs, AlbumKey(artist, album)) })
        {
            if (!cache.TryGet(k, out var tags)) continue;
            var seen = new HashSet<string>();
            List<string> picked = [.. tags.Where(t => GenreGroups.IsGenre(t) && ListeningAnalyzer.NormalizeArtist(t) is { Length: > 0 } n && n != self && seen.Add(n))
                .Take(max)];
            if (picked.Count > 0) return picked;
        }
        return [];
    }

    public void Save()
    {
        _albumTags.Save();
        _artistTags.Save();
        _discogs.Save();
        _similar.Save();
    }

    /// <summary>Last.fm 태그 (많이 붙은 순). count 가 10 미만인 태그는 몇 사람만 붙인 것이라 뺀다. 실패하면 null</summary>
    private async Task<List<string>?> LastFmTags(string key, string method, string query, CancellationToken ct)
    {
        await _lastFmGate.WaitAsync(ct);
        try
        {
            var r = await _http.GetFromJsonAsync<LastFmTopTags>(
                $"https://ws.audioscrobbler.com/2.0/?method={method}{query}&autocorrect=1&api_key={key}&format=json", ct);
            if (r == null) return null;
            if (r.Error != null) return r.Error == 6 ? [] : null;  // 6: 그런 앨범·아티스트 없음
            return [.. (r.TopTags?.Tag ?? []).Where(t => t.Count >= 10).Take(8).Select(t => t.Name)];
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Discogs 검색 첫 결과의 스타일 → 장르 순서. 실패하면 null</summary>
    private async Task<List<string>?> DiscogsTags(string token, string artist, string album, CancellationToken ct)
    {
        await _discogsGate.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.discogs.com/database/search?artist={Esc(artist)}&release_title={Esc(SearchTitle(album))}&type=release&per_page=3");
            req.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={token}");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var r = await res.Content.ReadFromJsonAsync<DiscogsSearch>(ct);
            var hit = r?.Results?.FirstOrDefault();
            return hit == null ? [] : [.. (hit.Style ?? []).Concat(hit.Genre ?? [])];
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    /// <summary>검색용 제목: 괄호(Deluxe·Remaster 등)와 " - EP"·" - Single" 을 뗀다 (떼고 나서 비면 원래 제목)</summary>
    private static string SearchTitle(string album) =>
        System.Text.RegularExpressions.Regex.Replace(album, @"\s*(\(.*?\)|\[.*?\])|\s+-\s+(single|ep)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim() is { Length: > 0 } t ? t : album;
    private static string ArtistKey(string artist) => ListeningAnalyzer.NormalizeArtist(artist);
    private static string AlbumKey(string artist, string album) => $"{ArtistKey(artist)}|{TasteProfile.TitleKey(album)}";

    private sealed class Throttle(TimeSpan interval)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _next = DateTime.MinValue;

        public async Task WaitAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _next = DateTime.UtcNow + interval;
            }
            finally { _gate.Release(); }
        }
    }

    private sealed record LastFmTopTags(
        [property: JsonPropertyName("toptags")] LastFmTagList? TopTags,
        [property: JsonPropertyName("error")] int? Error);
    private sealed record LastFmTagList([property: JsonPropertyName("tag")] List<LastFmTag>? Tag);
    private sealed record LastFmTag([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("count")] int Count);

    private sealed record LastFmSimilar(
        [property: JsonPropertyName("similarartists")] LastFmSimilarList? Similar,
        [property: JsonPropertyName("error")] int? Error);
    private sealed record LastFmSimilarList([property: JsonPropertyName("artist")] List<LastFmSimilarItem>? Artist);
    // match 는 "0.81" 처럼 문자열로 온다
    private sealed record LastFmSimilarItem([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("match")] string? Match);

    private sealed record DiscogsSearch([property: JsonPropertyName("results")] List<DiscogsResult>? Results);
    private sealed record DiscogsResult(
        [property: JsonPropertyName("genre")] List<string>? Genre,
        [property: JsonPropertyName("style")] List<string>? Style);
}

/// <summary>Last.fm 비슷한 아티스트 하나 (Match: 0~1, 1이 가장 닮음)</summary>
public sealed record SimilarArtist(string Name, double Match);
