using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Crate.Core.Providers;

/// <summary>
/// 아티스트 지역 판정. Deezer/Spotify에는 아티스트 국적이 없어서
/// 1) 이름의 문자(한글·가나·태국 문자 등)로 먼저 판정하고
/// 2) 모르면 MusicBrainz(초당 1회 제한)의 artist.country를 조회해 영구 캐시한다.
/// </summary>
public sealed class RegionResolver
{
    private static readonly HashSet<string> SoutheastAsia = ["TH", "VN", "ID", "PH", "MY", "SG", "MM", "KH", "LA", "BN", "TL"];
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _http;
    private readonly JsonCache<Region> _cache = new("artist-regions", TimeSpan.FromDays(3650));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public RegionResolver(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Crate/0.1 (personal music discovery desktop app)");
    }

    public bool TryGetCached(string artist, out Region region)
    {
        region = FromScript(artist);
        if (region != Region.Unknown) return true;
        if (_cache.TryGet(Key(artist), out region)) return true;
        // 캐시에 없을 때 TryGet은 default(Region) = Korea를 넣는다 → 반드시 Unknown으로
        region = Region.Unknown;
        return false;
    }

    public async Task<Region> ResolveAsync(string artist, CancellationToken ct = default)
    {
        if (TryGetCached(artist, out var r)) return r;
        // 네트워크 실패(null)는 캐시하지 않고 다음에 다시 시도
        if (await LookupAsync(artist, ct) is not Region found) return Region.Unknown;
        _cache.Set(Key(artist), found);
        return found;
    }

    public void Save() => _cache.Save();

    public static Region FromScript(string name)
    {
        foreach (var ch in name)
        {
            if (ch is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') return Region.Korea;
            if (ch is >= '぀' and <= 'ヿ') return Region.Japan;
            // 태국·라오·미얀마·크메르 문자
            if (ch is >= '฀' and <= '໿' or >= 'က' and <= '႟' or >= 'ក' and <= '៿') return Region.SoutheastAsia;
            // 베트남어 전용 글자
            if ("ơưăđƠƯĂĐ".Contains(ch) || ch is >= 'Ạ' and <= 'ỹ') return Region.SoutheastAsia;
        }
        return Region.Unknown;
    }

    private async Task<Region?> LookupAsync(string artist, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _next = DateTime.UtcNow + MinInterval;

            var q = Uri.EscapeDataString($"artist:\"{artist.Replace("\"", "")}\"");
            var r = await _http.GetFromJsonAsync<MbResult>($"https://musicbrainz.org/ws/2/artist?query={q}&limit=1&fmt=json", ct);
            var hit = r?.Artists?.FirstOrDefault();
            if (hit == null || hit.Score < 90) return Region.Unknown;
            var cc = hit.Country ?? hit.Area?.Codes?.FirstOrDefault()?[..2];
            return cc switch
            {
                null => Region.Unknown,
                "KR" => Region.Korea,
                "JP" => Region.Japan,
                _ when SoutheastAsia.Contains(cc) => Region.SoutheastAsia,
                _ => Region.International
            };
        }
        catch (HttpRequestException) { return null; }
        finally { _gate.Release(); }
    }

    private static string Key(string artist) => ListeningAnalyzer.NormalizeArtist(artist);

    private sealed record MbResult([property: JsonPropertyName("artists")] List<MbArtist>? Artists);

    private sealed record MbArtist(
        [property: JsonPropertyName("score")] int Score,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("area")] MbArea? Area);

    private sealed record MbArea([property: JsonPropertyName("iso-3166-1-codes")] List<string>? Codes);
}
