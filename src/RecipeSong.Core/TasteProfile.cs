namespace RecipeSong.Core;

/// <summary>
/// 추천 후보를 모을 시드 아티스트와 "내 라이브러리 뮤지션"(신보를 항상 보여 주는 아티스트).
/// 플레이리스트와 상관없이 라이브러리 전체에서 곡 가중치 합(많이 듣고 하트·별점을 준 곡)으로 고른다.
/// </summary>
public static class TasteProfile
{
    private static readonly HashSet<string> Excluded = ["unknownartist", "variousartists", "va"];

    /// <summary>라이브러리 뮤지션 추천의 FromPlaylist 자리에 넣는 값 (데이터 키 — 화면에서만 번역)</summary>
    public const string LibraryTag = "내 라이브러리";

    private static string Who(TrackRecord t) =>
        Excluded.Contains(ListeningAnalyzer.NormalizeArtist(t.GroupArtist)) ? t.Artist : t.GroupArtist;

    private static IEnumerable<(string Artist, List<TrackRecord> Tracks, double Score)> Ranked(IEnumerable<TrackRecord> tracks) =>
        tracks.GroupBy(t => Who(t).Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !Excluded.Contains(ListeningAnalyzer.NormalizeArtist(g.Key)))
            .Select(g => (g.Key, g.ToList(), Score: g.Sum(t => t.Weight)))
            .OrderByDescending(x => x.Score);

    /// <summary>시드: 곡 가중치 합 상위 아티스트 (가중치 0.5~1.0)</summary>
    public static IReadOnlyList<TasteArtist> FromLibrary(IEnumerable<TrackRecord> tracks, int top = 80)
    {
        var best = Ranked(tracks).Take(top).ToList();
        if (best.Count == 0) return [];
        double max = best[0].Score;
        return [.. best.Select(x => new TasteArtist(x.Artist, Math.Round(0.5 + 0.5 * x.Score / max, 3), x.Tracks.Sum(t => t.PlayCount), LibraryTag))];
    }

    /// <summary>
    /// 내 라이브러리 뮤지션: 곡 가중치 합 상위 top 명 (곡이 2곡 이상이거나 하트를 준 곡이 있는 사람만).
    /// 신보는 점수와 상관없이 앞에 나온다. Titles 는 Deezer 동명이인 확인용 (내 곡·앨범 제목).
    /// </summary>
    public static IReadOnlyList<PlaylistArtist> LibraryArtists(IEnumerable<TrackRecord> tracks, int top = 500) =>
        [.. Ranked(tracks).Where(x => x.Tracks.Count >= 2 || x.Tracks.Any(t => t.Loved)).Take(top)
            .Select(x => new PlaylistArtist(x.Artist, LibraryTag, null, "", x.Tracks.Count,
                x.Tracks.SelectMany(t => new[] { TitleKey(t.Title), TitleKey(t.Album) }).ToHashSet()))];

    private static readonly System.Text.RegularExpressions.Regex Decorations =
        new(@"\s*(\(.*?\)|\[.*?\]|\s-\s(single|ep)$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>제목 비교용: 괄호·대괄호(Remaster, feat. 등)와 " - Single"을 떼고 정규화.</summary>
    public static string TitleKey(string? title) =>
        ListeningAnalyzer.NormalizeArtist(Decorations.Replace(title ?? "", ""));
}
