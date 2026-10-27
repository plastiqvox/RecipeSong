using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

/// <summary>
/// 좋아하는 레이블: 내 라이브러리에서 많이 듣고 좋아한(곡 가중치가 높은) 앨범들의 레이블을 Deezer에서 찾아,
/// 레이블마다 0~1 점수를 매긴다. 신보가 같은 레이블이면 가산점.
/// 대형 레이블·유통사는 취향 신호가 아니라서 뺀다.
/// </summary>
public sealed class LabelAffinity(DeezerClient deezer)
{
    // "아티스트|앨범" → 레이블 (못 찾으면 빈 문자열)
    private readonly JsonCache<string> _labels = new("library-album-labels", TimeSpan.FromDays(365));

    private static readonly HashSet<string> Generic =
    [
        "universal", "universalmusic", "universalmusicgroup", "sony", "sonymusic", "sonymusicentertainment", "warner",
        "warnermusic", "warnerrecords", "warnerbros", "columbia", "atlantic", "interscope", "capitol", "rca", "emi",
        "virgin", "polydor", "island", "republic", "def jam", "defjam", "epic", "parlophone", "decca", "avex", "avextrax",
        "independent", "selfreleased", "distrokid", "tunecore", "cdbaby", "awal", "believe", "orchard", "theorchard",
        "ingrooves", "unitedmasters", "kakaoentertainment", "stonemusic", "genie", "dreamus",
    ];

    /// <summary>레이블 비교용: 소문자·영숫자만, 흔한 꼬리말(Records, Music, Ltd...) 제거</summary>
    public static string Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var s = label.ToLowerInvariant();
        foreach (var tail in new[] { " records", " recordings", " music", " entertainment", " ltd", " llc", " inc", " co.", " label" })
            s = s.Replace(tail, "");
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// 레이블 표기를 비교용 키 여러 개로: "Atik Sounds / SPACE SHOWER MUSIC" → atiksounds, spaceshower.
    /// 대형 레이블·유통사(이름이 그걸로 시작하는 것 포함)는 뺀다.
    /// </summary>
    public static IEnumerable<string> Keys(string? label) =>
        (label ?? "").Split(['/', ',', '&', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(k => k.Length >= 2 && !Generic.Contains(k) && !Generic.Any(g => g.Length >= 5 && k.StartsWith(g)))
            .Distinct();

    /// <summary>신보 레이블의 점수: 키 중 가장 높은 것</summary>
    public static double Of(IReadOnlyDictionary<string, double> affinity, string? label) =>
        Keys(label).Select(k => affinity.GetValueOrDefault(k)).DefaultIfEmpty(0).Max();

    /// <summary>캐시에 있는 것만으로 바로 계산 (앱 시작 직후용)</summary>
    public IReadOnlyDictionary<string, double> FromCache(IEnumerable<TrackRecord> tracks, int maxAlbums = 300) =>
        Score(TopAlbums(tracks, maxAlbums), key => _labels.TryGet(key, out var l) ? l : null);

    /// <summary>못 찾은 앨범의 레이블을 Deezer에서 찾고 (처음 한 번 1분 남짓) 점수를 계산한다.</summary>
    public async Task<IReadOnlyDictionary<string, double>> BuildAsync(IEnumerable<TrackRecord> tracks, int maxAlbums = 300,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var albums = TopAlbums(tracks, maxAlbums);
        var todo = albums.Where(a => !_labels.TryGet(a.Key, out _)).ToList();
        int done = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (a, ct) =>
        {
            var found = await deezer.FindAlbumAsync(a.Artist, a.Album, ct);
            // 같은 아티스트의 앨범일 때만 믿는다
            var ok = found?.Artist is { } fa && ListeningAnalyzer.NormalizeArtist(fa.Name) == ListeningAnalyzer.NormalizeArtist(a.Artist);
            _labels.Set(a.Key, ok ? found!.Label ?? "" : "");
            if (Interlocked.Increment(ref done) % 25 == 0) progress?.Report(L.F("좋아하는 레이블 찾는 중 {0}/{1}", done, todo.Count));
        });
        _labels.Save();
        return Score(albums, key => _labels.TryGet(key, out var l) ? l : null);
    }

    private sealed record LikedAlbum(string Key, string Artist, string Album, double Score);

    /// <summary>앨범 점수 = 곡 가중치 합 (많이 듣고 좋아한 곡이 많은 앨범)</summary>
    private static List<LikedAlbum> TopAlbums(IEnumerable<TrackRecord> tracks, int max) =>
        [.. tracks.Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => (Artist: t.GroupArtist, t.Album))
            .Where(g => !ListeningAnalyzer.NormalizeArtist(g.Key.Artist).StartsWith("various"))
            .Select(g => new LikedAlbum($"{ListeningAnalyzer.NormalizeArtist(g.Key.Artist)}|{ListeningAnalyzer.NormalizeArtist(g.Key.Album)}",
                g.Key.Artist, g.Key.Album, g.Sum(t => t.Weight * t.Weight)))
            .OrderByDescending(a => a.Score)
            .Take(max)];

    /// <summary>레이블 점수: 그 레이블 앨범들의 점수 합, 가장 높은 레이블을 1로 (제곱근으로 완만하게)</summary>
    private static IReadOnlyDictionary<string, double> Score(List<LikedAlbum> albums, Func<string, string?> labelOf)
    {
        var sums = new Dictionary<string, double>();
        foreach (var a in albums)
        {
            foreach (var key in Keys(labelOf(a.Key)))
                sums[key] = sums.GetValueOrDefault(key) + a.Score;
        }
        if (sums.Count == 0) return new Dictionary<string, double>();
        var max = sums.Values.Max();
        return sums.ToDictionary(kv => kv.Key, kv => Math.Round(Math.Sqrt(kv.Value / max), 3));
    }
}
