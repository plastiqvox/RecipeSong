using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Crate.Core.Providers;

/// <summary>
/// Apple iTunes Search API (인증 불필요). Deezer에 없는 앨범의 아트 보조용.
/// 제한이 엄격하다(분당 약 20회).
/// </summary>
public sealed class ITunesSearchClient(HttpClient http, string country = "KR")
{
    public async Task<AlbumArt?> FindAlbumAsync(string artist, string album, CancellationToken ct = default)
    {
        var term = Uri.EscapeDataString($"{artist} {album}".Trim());
        try
        {
            var r = await http.GetFromJsonAsync<ItResults>(
                $"https://itunes.apple.com/search?term={term}&entity=album&limit=1&country={country}", ct);
            // 100x100 썸네일 URL을 600x600으로 키운다
            var art = r?.Results.FirstOrDefault()?.Artwork?.Replace("100x100bb", "600x600bb");
            return art == null ? null : new AlbumArt(art);
        }
        catch (HttpRequestException) { return null; }
    }

    private sealed record ItResults([property: JsonPropertyName("results")] List<ItItem> Results);

    private sealed record ItItem([property: JsonPropertyName("artworkUrl100")] string? Artwork);
}
