using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RecipeSong.Core.Providers;

/// <summary>
/// Deezer 공개 API (인증 불필요). 비슷한 아티스트, 디스코그래피(발매일 포함), 앨범 아트, 30초 미리듣기 MP3.
/// 제한: 5초당 50요청 → 요청 간격을 110ms 이상으로 유지한다.
/// 미리듣기 URL은 몇 시간 뒤 만료되므로 캐시하지 말고 필요할 때 받는다.
/// </summary>
public sealed class DeezerClient(HttpClient http)
{
    private const string Base = "https://api.deezer.com";
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(110);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public async Task<DzArtist?> FindArtistAsync(string name, CancellationToken ct = default)
    {
        var r = await GetAsync<DzList<DzArtist>>($"/search/artist?q={Uri.EscapeDataString(name)}&limit=5", ct);
        var norm = ListeningAnalyzer.NormalizeArtist(name);
        return r?.Data?.FirstOrDefault(a => ListeningAnalyzer.NormalizeArtist(a.Name) == norm) ?? r?.Data?.FirstOrDefault();
    }

    /// <summary>앨범 검색 (필드 지정 없는 일반 검색 — 표기가 조금 달라도 걸린다). 결과에는 발매일이 없다.</summary>
    public async Task<IReadOnlyList<DzAlbum>> SearchAlbumsAsync(string query, int limit = 10, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzAlbum>>($"/search/album?q={Uri.EscapeDataString(query)}&limit={limit}", ct))?.Data ?? [];

    public async Task<IReadOnlyList<DzArtist>> SearchArtistsAsync(string name, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzArtist>>($"/search/artist?q={Uri.EscapeDataString(name)}&limit=5", ct))?.Data ?? [];

    public async Task<IReadOnlyList<DzArtist>> RelatedArtistsAsync(long artistId, int limit = 20, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzArtist>>($"/artist/{artistId}/related?limit={limit}", ct))?.Data ?? [];

    /// <summary>아티스트의 앨범/EP/싱글 목록 (발매일 포함).</summary>
    public async Task<IReadOnlyList<DzAlbum>> ArtistAlbumsAsync(long artistId, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzAlbum>>($"/artist/{artistId}/albums?limit=200", ct))?.Data ?? [];

    /// <summary>과거 앨범 탐색용 전체 페이지. 실패한 페이지를 빈 디스코그래피로 취급하지 않는다.</summary>
    public async Task<IReadOnlyList<DzAlbum>> ArtistAlbumsAllAsync(long artistId, CancellationToken ct = default)
    {
        var albums = new Dictionary<long, DzAlbum>();
        string? path = $"/artist/{artistId}/albums?limit=100";
        var pages = new HashSet<string>();
        while (path != null)
        {
            ct.ThrowIfCancellationRequested();
            if (!pages.Add(path)) throw new HttpRequestException(L.T("디스코그래피를 불러오지 못했어요. 다시 시도해 주세요."));
            var page = await GetAsync<DzList<DzAlbum>>(path, ct);
            if (page?.Data == null) throw new HttpRequestException(L.T("디스코그래피를 불러오지 못했어요. 다시 시도해 주세요."));
            foreach (var album in page.Data) albums[album.Id] = album;
            path = null;
            if (!string.IsNullOrWhiteSpace(page.Next))
            {
                if (!Uri.TryCreate(page.Next, UriKind.Absolute, out var next) || next.Scheme != "https"
                    || next.Host != "api.deezer.com" || next.AbsolutePath != $"/artist/{artistId}/albums")
                    throw new HttpRequestException(L.T("디스코그래피를 불러오지 못했어요. 다시 시도해 주세요."));
                path = next.PathAndQuery;
            }
        }
        return [.. albums.Values];
    }

    public async Task<IReadOnlyList<DzTrack>> AlbumTracksAsync(long albumId, int limit = 3, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzTrack>>($"/album/{albumId}/tracks?limit={limit}", ct))?.Data ?? [];

    public async Task<IReadOnlyList<DzTrack>> AlbumTracksAllAsync(long albumId, CancellationToken ct = default)
    {
        var tracks = new List<DzTrack>();
        string? path = $"/album/{albumId}/tracks?limit=100";
        var pages = new HashSet<string>();
        while (path != null)
        {
            ct.ThrowIfCancellationRequested();
            if (!pages.Add(path)) throw new HttpRequestException(L.T("수록곡 전체를 불러오지 못했어요. 다시 선택해 주세요."));
            var page = await GetAsync<DzList<DzTrack>>(path, ct);
            if (page?.Data == null) throw new HttpRequestException(L.T("수록곡 전체를 불러오지 못했어요. 다시 선택해 주세요."));
            tracks.AddRange(page.Data);
            path = null;
            if (!string.IsNullOrWhiteSpace(page.Next))
            {
                if (!Uri.TryCreate(page.Next, UriKind.Absolute, out var next) || next.Scheme != "https"
                    || next.Host != "api.deezer.com" || next.AbsolutePath != $"/album/{albumId}/tracks")
                    throw new HttpRequestException(L.T("수록곡 전체를 불러오지 못했어요. 다시 선택해 주세요."));
                path = next.PathAndQuery;
            }
        }
        return tracks;
    }

    /// <summary>아티스트+곡명으로 곡을 찾는다 (내 파일이 없는 '비슷한 내 곡'을 미리듣기로 들을 때)</summary>
    public async Task<DzTrack?> FindTrackAsync(string artist, string title, CancellationToken ct = default)
    {
        var q = $"artist:\"{artist}\" track:\"{title}\"";
        var hit = (await GetAsync<DzList<DzTrack>>($"/search/track?q={Uri.EscapeDataString(q)}&limit=5", ct))?.Data?
            .FirstOrDefault(t => !string.IsNullOrEmpty(t.Preview));
        if (hit != null) return hit;
        // 따옴표 검색은 자주 비어서 온다 → 그냥 이어 붙여 한 번 더. 이때는 다른 아티스트 곡이 걸릴 수 있어 아티스트가 맞는 것만
        return (await GetAsync<DzList<DzTrack>>($"/search/track?q={Uri.EscapeDataString($"{artist} {title}")}&limit=10", ct))?.Data?
            .FirstOrDefault(t => !string.IsNullOrEmpty(t.Preview) && t.Artist?.Name is { } name
                && (name.Contains(artist, StringComparison.OrdinalIgnoreCase) || artist.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>곡 하나 (앨범 목록에 미리듣기가 비어 있을 때 다시 물어본다. 막힌 곡은 alternative 에 같은 곡의 다른 판이 온다)</summary>
    public Task<DzTrack?> TrackAsync(long trackId, CancellationToken ct = default) =>
        GetAsync<DzTrack>($"/track/{trackId}", ct);

    /// <summary>앨범 상세: 레이블, 발매일, 수록곡(미리듣기 포함).</summary>
    public Task<DzAlbum?> AlbumAsync(long albumId, CancellationToken ct = default) =>
        GetAsync<DzAlbum>($"/album/{albumId}", ct);

    /// <summary>아티스트+앨범명으로 앨범을 찾는다. 발매일은 /album/{id} 에만 있어 한 번 더 호출한다.</summary>
    public async Task<DzAlbum?> FindAlbumAsync(string artist, string album, CancellationToken ct = default)
    {
        var q = $"artist:\"{artist}\" album:\"{album}\"";
        var r = await GetAsync<DzList<DzAlbum>>($"/search/album?q={Uri.EscapeDataString(q)}&limit=1", ct);
        var hit = r?.Data?.FirstOrDefault();
        return hit == null ? null : await GetAsync<DzAlbum>($"/album/{hit.Id}", ct) ?? hit;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _next = DateTime.UtcNow + MinInterval;
            }
            finally { _gate.Release(); }

            try
            {
                using var resp = await http.GetAsync(Base + path, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync(ct);
                // 할당량 초과(code 4)는 200 + {"error":...}로 온다
                if (body.StartsWith("{\"error\"", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                    continue;
                }
                return System.Text.Json.JsonSerializer.Deserialize<T>(body);
            }
            catch (HttpRequestException) { return null; }
            // HttpClient 시간 초과는 취소가 아니라 실패 — 그대로 올리면 부른 쪽이 '사용자가 멈춤'으로 알고 계산을 조용히 끝낸다
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { continue; }
        }
        return null;
    }
}

public sealed record DzList<T>([property: JsonPropertyName("data")] List<T>? Data,
    [property: JsonPropertyName("next")] string? Next = null);

public sealed record DzArtist(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("picture_big")] string? Picture,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("nb_fan")] int Fans = 0);

public sealed record DzAlbum(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("cover_big")] string? Cover,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("record_type")] string? RecordType,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("genre_id")] int? GenreId = null,
    [property: JsonPropertyName("artist")] DzArtist? Artist = null,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("tracks")] DzList<DzTrack>? Tracks = null,
    [property: JsonPropertyName("genres")] DzList<DzGenre>? Genres = null,
    [property: JsonPropertyName("nb_tracks")] int? TrackCount = null)
{
    [JsonIgnore]
    public DateOnly? Released => DateOnly.TryParse(ReleaseDate, out var d) ? d : null;
}

public sealed record DzGenre(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record DzTrack(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("preview")] string? Preview,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("duration")] int Duration = 0,
    [property: JsonPropertyName("artist")] DzArtist? Artist = null,
    [property: JsonPropertyName("rank")] int Rank = 0,
    [property: JsonPropertyName("id")] long Id = 0,
    [property: JsonPropertyName("alternative")] DzTrack? Alternative = null);
