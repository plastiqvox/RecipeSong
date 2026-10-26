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

    public async Task<IReadOnlyList<DzTrack>> AlbumTracksAsync(long albumId, int limit = 3, CancellationToken ct = default) =>
        (await GetAsync<DzList<DzTrack>>($"/album/{albumId}/tracks?limit={limit}", ct))?.Data ?? [];

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
        }
        return null;
    }
}

public sealed record DzList<T>([property: JsonPropertyName("data")] List<T>? Data);

public sealed record DzArtist(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("picture_big")] string? Picture,
    [property: JsonPropertyName("link")] string? Link);

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
    [property: JsonPropertyName("genres")] DzList<DzGenre>? Genres = null)
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
    [property: JsonPropertyName("rank")] int Rank = 0);
