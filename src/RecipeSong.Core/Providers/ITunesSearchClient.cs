using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RecipeSong.Core.Providers;

/// <summary>
/// Apple iTunes Search API (인증 불필요). Deezer에 없는 앨범의 아트 보조용.
/// 제한이 엄격하다(분당 약 20회).
/// </summary>
public sealed class ITunesSearchClient(HttpClient http, string country = "KR")
{
    public async Task<AlbumArt?> FindAlbumAsync(string artist, string album, CancellationToken ct = default)
    {
        var term = Uri.EscapeDataString($"{artist} {album}".Trim());
        // 한국 스토어는 검색 API에 음악이 안 나와서 미국 스토어로 한 번 더
        foreach (var store in new[] { country, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var r = await http.GetFromJsonAsync<ItResults>(
                    $"https://itunes.apple.com/search?term={term}&entity=album&limit=1&country={store}", ct);
                // 100x100 썸네일 URL을 600x600으로 키운다
                if (r?.Results.FirstOrDefault()?.Artwork?.Replace("100x100bb", "600x600bb") is { } art) return new AlbumArt(art);
            }
            catch (HttpRequestException) { return null; }
        }
        return null;
    }

    /// <summary>Apple Music 앨범 주소 (music.apple.com/…). 아티스트가 같고 제목이 가장 비슷한 것, 없으면 null.</summary>
    public async Task<string?> FindAlbumLinkAsync(string artist, string album, CancellationToken ct = default)
    {
        var term = Uri.EscapeDataString($"{artist} {album}".Trim());
        string N(string? s) => ListeningAnalyzer.NormalizeArtist(s ?? "");
        // 한국 스토어는 검색 API에 음악이 안 나온다 → 미국·일본 스토어에서 찾고 주소만 내 나라로 (앨범 id는 같다)
        foreach (var store in new[] { country, "US", "JP" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var r = await http.GetFromJsonAsync<ItResults>(
                    $"https://itunes.apple.com/search?term={term}&entity=album&limit=10&country={store}", ct);
                var sameArtist = (r?.Results ?? []).Where(x => x.Url != null && N(x.Artist) == N(artist)).ToList();
                var best = sameArtist.FirstOrDefault(x => N(x.Album) == N(album))
                           ?? sameArtist.FirstOrDefault(x => N(x.Album).StartsWith(N(album), StringComparison.Ordinal) || N(album).StartsWith(N(x.Album), StringComparison.Ordinal));
                if (best?.Url is { } url)
                    return System.Text.RegularExpressions.Regex.Replace(url.Split('?')[0], @"music\.apple\.com/[a-z]{2}/", $"music.apple.com/{country.ToLowerInvariant()}/");
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    /// <summary>Apple Music 웹 검색 주소 (앨범을 못 찾았을 때)</summary>
    public string SearchLink(string artist, string album) =>
        $"https://music.apple.com/{country.ToLowerInvariant()}/search?term={Uri.EscapeDataString($"{artist} {album}".Trim())}";

    // 캐시 두 개: Deezer 아티스트 ID → Apple 아티스트 ID (0 = 같은 사람을 확인하지 못함, 30일) / Apple 아티스트 ID → 발매작 (3일).
    // 대조는 아티스트당 요청 2~4번이라 오래 두고, 신보를 보는 목록만 사흘마다 요청 1번으로 새로 받는다. 네트워크 실패는 캐시하지 않는다.
    private JsonCache<long>? _artistIds;
    private JsonCache<List<AppleAlbum>>? _albumLists;
    private JsonCache<long> ArtistIds => _artistIds ??= new("itunes-artist-ids", TimeSpan.FromDays(30));
    private JsonCache<List<AppleAlbum>> AlbumLists => _albumLists ??= new("itunes-discography", TimeSpan.FromDays(3));

    /// <summary>뒤에서 모을 때(paced) 요청 사이 간격. Search API 제한(분당 약 20회)에 맞춘다.</summary>
    public TimeSpan PacedInterval { get; init; } = TimeSpan.FromSeconds(3.2);
    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _lastPaced = DateTime.MinValue;

    /// <summary>
    /// Deezer 디스코그래피에 없는 Apple 발매작 (이 지역 Deezer 에 없거나 늦게 들어오는 앨범).
    /// Apple 아티스트는 이름이 정확히 같은 후보 중 Deezer 앨범 제목과 겹치는 것이 가장 많은 쪽으로 정한다 —
    /// Deezer ID 는 이미 보유 앨범으로 검증했으므로, 제목이 겹치면 같은 사람이다. 동명이인을 막으려고 겹침이 없으면 쓰지 않는다.
    /// </summary>
    /// <param name="paced">뒤에서 여러 명을 모을 때: 요청 간격을 PacedInterval 로 벌리고, 캐시 저장은 부르는 쪽(SaveCaches)이 한다</param>
    public async Task<IReadOnlyList<AppleAlbum>> MissingAlbumsAsync(long deezerArtistId, string artist,
        IReadOnlyList<DzAlbum> deezerAlbums, CancellationToken ct = default, bool paced = false)
    {
        var albums = await CatalogAsync(deezerArtistId, artist, deezerAlbums, paced, ct);
        if (!paced) SaveCaches();
        return Missing(albums, deezerAlbums);
    }

    /// <summary>캐시에 있는 것만 (요청 없음). fresh=false 면 아직 받지 않았거나 받은 지 오래됐다 (뒤에서 다시 받을 대상).</summary>
    public IReadOnlyList<AppleAlbum> CachedMissingAlbums(long deezerArtistId, IReadOnlyList<DzAlbum> deezerAlbums, out bool fresh)
    {
        fresh = false;
        if (!ArtistIds.TryGetStale(Key(deezerArtistId), out var appleId, out var idFresh)) return [];
        if (appleId == 0) { fresh = idFresh; return []; }
        if (!AlbumLists.TryGetStale(Key(appleId), out var albums, out var listFresh)) return [];
        fresh = idFresh && listFresh;
        return Missing(albums, deezerAlbums);
    }

    public void SaveCaches() { _artistIds?.Save(); _albumLists?.Save(); }

    private static string Key(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<List<AppleAlbum>> CatalogAsync(long deezerArtistId, string artist, IReadOnlyList<DzAlbum> deezerAlbums,
        bool paced, CancellationToken ct)
    {
        var key = Key(deezerArtistId);
        if (ArtistIds.TryGet(key, out var appleId))
        {
            if (appleId == 0) return [];
            if (AlbumLists.TryGet(Key(appleId), out var cached)) return cached;
            var albums = await ArtistCollectionsAsync(appleId, artist, paced, ct);
            AlbumLists.Set(Key(appleId), albums);
            return albums;
        }
        var (id, found) = await FindArtistAsync(artist, deezerAlbums, paced, ct);
        ArtistIds.Set(key, id);
        if (id != 0) AlbumLists.Set(Key(id), found);
        return found;
    }

    /// <summary>
    /// Deezer 에 없는 것만. 제목이 같으면 같은 발매작이고, 같은 날 나온 Deezer 발매작과 제목 앞부분이 같아도
    /// (피처링 표기·부제 차이) 같은 것으로 본다 — 아카이브 뮤지션 추천에 같은 앨범이 두 장 나오지 않게.
    /// </summary>
    private static AppleAlbum[] Missing(IReadOnlyList<AppleAlbum> apple, IReadOnlyList<DzAlbum> deezer)
    {
        var known = deezer.Select(a => TitleKey(a.Title)).ToHashSet();
        bool SameDay(AppleAlbum a) => deezer.Any(d => d.ReleaseDate == a.ReleaseDate
            && (TitleKey(d.Title).StartsWith(TitleKey(a.Title), StringComparison.Ordinal) || TitleKey(a.Title).StartsWith(TitleKey(d.Title), StringComparison.Ordinal)));
        return [.. apple.Where(a => !known.Contains(TitleKey(a.Title)) && !SameDay(a))];
    }

    private async Task<ItResults?> GetAsync(string url, bool paced, CancellationToken ct)
    {
        if (!paced) return await http.GetFromJsonAsync<ItResults>(url, ct);
        await _pace.WaitAsync(ct);
        try
        {
            var wait = _lastPaced + PacedInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try { return await http.GetFromJsonAsync<ItResults>(url, ct); }
            finally { _lastPaced = DateTime.UtcNow; }
        }
        finally { _pace.Release(); }
    }

    /// <summary>Apple 앨범 수록곡 (디스크·곡 순서). 미리듣기는 30초 m4a.</summary>
    public async Task<IReadOnlyList<DzTrack>> AlbumTracksAsync(long collectionId, CancellationToken ct = default)
    {
        var r = await http.GetFromJsonAsync<ItResults>($"https://itunes.apple.com/lookup?id={collectionId}&entity=song&limit=200&country=US", ct);
        return (r?.Results ?? []).Where(x => x.WrapperType == "track" && x.Kind == "song" && !string.IsNullOrWhiteSpace(x.Track))
            .OrderBy(x => x.Disc ?? 1).ThenBy(x => x.TrackNumber ?? 0)
            .Select(x => new DzTrack(x.Track!, x.Preview, x.TrackUrl, (int)((x.Millis ?? 0) / 1000), Id: x.TrackId ?? 0)).ToArray();
    }

    /// <returns>Apple 아티스트 ID (0 = 확인하지 못함)와 그 발매작</returns>
    private async Task<(long Id, List<AppleAlbum> Albums)> FindArtistAsync(string artist, IReadOnlyList<DzAlbum> deezerAlbums,
        bool paced, CancellationToken ct)
    {
        string N(string? s) => ListeningAnalyzer.NormalizeArtist(s ?? "");
        // 한국 스토어는 검색 API 에 음악이 없다 → 미국 스토어
        var found = await GetAsync(
            $"https://itunes.apple.com/search?term={Uri.EscapeDataString(artist.Trim())}&entity=musicArtist&limit=10&country=US", paced, ct);
        var candidates = (found?.Results ?? []).Where(x => x.ArtistId is > 0 && N(x.Artist) == N(artist))
            .Select(x => x.ArtistId!.Value).Distinct().Take(3).ToArray();
        var deezerTitles = deezerAlbums.Select(a => TitleKey(a.Title)).Where(t => t.Length > 0).ToHashSet();
        (long Id, int Matches, List<AppleAlbum> Albums) best = (0, 0, []);
        int second = 0;
        foreach (var id in candidates)
        {
            var albums = await ArtistCollectionsAsync(id, artist, paced, ct);
            var matches = albums.Select(a => TitleKey(a.Title)).Distinct().Count(deezerTitles.Contains);
            if (matches > best.Matches) { second = best.Matches; best = (id, matches, albums); }
            else second = Math.Max(second, matches);
        }
        // 한 장만 겹치면 흔한 제목일 수 있으니, Deezer 에 앨범이 거의 없을 때만 받아들인다. 동점이면 정하지 않는다.
        int needed = Math.Min(2, deezerTitles.Count);
        return best.Matches >= Math.Max(1, needed) && best.Matches > second ? (best.Id, best.Albums) : (0, []);
    }

    private async Task<List<AppleAlbum>> ArtistCollectionsAsync(long appleArtistId, string artist, bool paced, CancellationToken ct)
    {
        var r = await GetAsync($"https://itunes.apple.com/lookup?id={appleArtistId}&entity=album&limit=200&country=US", paced, ct);
        // 아티스트 조회에는 피처링으로 참여한 남의 싱글도 섞여 온다 → 앨범 아티스트가 그 사람인 것만
        var name = ListeningAnalyzer.NormalizeArtist(artist);
        return (r?.Results ?? []).Where(x => x.WrapperType == "collection" && x.CollectionId is > 0 && !string.IsNullOrWhiteSpace(x.Album)
                && ListeningAnalyzer.NormalizeArtist(x.Artist ?? "") == name)
            .Select(x =>
            {
                var (title, type) = SplitKind(x.Album!);
                var url = x.Url is { } u ? System.Text.RegularExpressions.Regex.Replace(u.Split('?')[0], @"music\.apple\.com/[a-z]{2}/",
                    $"music.apple.com/{country.ToLowerInvariant()}/") : null;
                return new AppleAlbum(x.CollectionId!.Value, title, type, x.Released is { Length: >= 10 } d ? d[..10] : null,
                    x.Artwork?.Replace("100x100bb", "600x600bb"), url, x.TrackCount,
                    string.Equals(x.CollectionType, "Compilation", StringComparison.OrdinalIgnoreCase));
            })
            // 같은 앨범의 클린·익스플리싯 판은 하나만
            .DistinctBy(a => (TitleKey(a.Title), a.ReleaseDate)).ToList();
    }

    /// <summary>"Title - Single" / "Title - EP" → (Title, single/ep). 그 밖은 album.</summary>
    public static (string Title, string RecordType) SplitKind(string name)
    {
        if (name.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase)) return (name[..^9].TrimEnd(), "single");
        if (name.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase)) return (name[..^5].TrimEnd(), "ep");
        return (name, "album");
    }

    private static string TitleKey(string title) => ListeningAnalyzer.NormalizeArtist(title);

    private sealed record ItResults([property: JsonPropertyName("results")] List<ItItem> Results);

    private sealed record ItItem(
        [property: JsonPropertyName("artworkUrl100")] string? Artwork,
        [property: JsonPropertyName("collectionViewUrl")] string? Url = null,
        [property: JsonPropertyName("artistName")] string? Artist = null,
        [property: JsonPropertyName("collectionName")] string? Album = null,
        [property: JsonPropertyName("wrapperType")] string? WrapperType = null,
        [property: JsonPropertyName("kind")] string? Kind = null,
        [property: JsonPropertyName("artistId")] long? ArtistId = null,
        [property: JsonPropertyName("collectionId")] long? CollectionId = null,
        [property: JsonPropertyName("collectionType")] string? CollectionType = null,
        [property: JsonPropertyName("releaseDate")] string? Released = null,
        [property: JsonPropertyName("trackCount")] int? TrackCount = null,
        [property: JsonPropertyName("trackId")] long? TrackId = null,
        [property: JsonPropertyName("trackName")] string? Track = null,
        [property: JsonPropertyName("trackViewUrl")] string? TrackUrl = null,
        [property: JsonPropertyName("previewUrl")] string? Preview = null,
        [property: JsonPropertyName("trackTimeMillis")] long? Millis = null,
        [property: JsonPropertyName("discNumber")] int? Disc = null,
        [property: JsonPropertyName("trackNumber")] int? TrackNumber = null);
}

/// <summary>
/// 추천 카드의 Deezer 앨범 ID 자리에 Apple 앨범을 넣을 때는 음수(-collectionId)로 둔다.
/// 저장 결과·좋아요·차단이 모두 이 ID 를 열쇠로 쓰므로 Deezer ID 와 겹치지 않게 하고, 음수면 Deezer·소리 서비스에 묻지 않는다.
/// </summary>
public static class AppleAlbumId
{
    public static long ToCard(long collectionId) => -collectionId;
    public static bool Is(long cardId) => cardId < 0;
    public static long Collection(long cardId) => -cardId;
}

/// <summary>Apple 카탈로그의 발매작. Id 는 Apple collectionId (Deezer ID 와 섞지 않는다).</summary>
public sealed record AppleAlbum(long Id, string Title, string RecordType, string? ReleaseDate, string? Cover, string? Link,
    int? TrackCount, bool Compilation = false)
{
    [JsonIgnore]
    public DateOnly? Released => DateOnly.TryParse(ReleaseDate, out var d) ? d : null;
}
