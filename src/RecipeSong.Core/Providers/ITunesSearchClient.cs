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

    private sealed record ItResults([property: JsonPropertyName("results")] List<ItItem> Results);

    private sealed record ItItem(
        [property: JsonPropertyName("artworkUrl100")] string? Artwork,
        [property: JsonPropertyName("collectionViewUrl")] string? Url = null,
        [property: JsonPropertyName("artistName")] string? Artist = null,
        [property: JsonPropertyName("collectionName")] string? Album = null);
}
