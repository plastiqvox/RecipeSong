namespace RecipeSong.Core;

/// <summary>
/// 추천 후보를 모을 시드 아티스트.
/// 크레이트로 쓰는 플레이리스트마다 많이 들은 아티스트를 고르게 뽑아서, 큰 플레이리스트(Overseas 등)가
/// 시드를 독차지하지 않게 한다.
/// </summary>
public static class TasteProfile
{
    private static readonly HashSet<string> Excluded = ["unknownartist", "variousartists", "va"];

    /// <param name="focusRegion">crates.json의 지역 이름("해외" 등). 지정하면 그 지역 플레이리스트에서만 시드를 많이 뽑는다.</param>
    public static IReadOnlyList<TasteArtist> FromCrates(
        IReadOnlyList<Playlist> playlists, CrateConfig config, int perPlaylist = 10, string? focusRegion = null)
    {
        if (focusRegion != null) perPlaylist = Math.Max(perPlaylist, 40);
        var best = new Dictionary<string, TasteArtist>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in playlists.Where(p => p.Kind == PlaylistKind.User && config.Uses(p.Name)))
        {
            if (focusRegion != null && config.Playlists[p.Name].Region != focusRegion) continue;
            var top = p.Tracks
                .GroupBy(t => t.GroupArtist, StringComparer.OrdinalIgnoreCase)
                .Where(g => !Excluded.Contains(ListeningAnalyzer.NormalizeArtist(g.Key)))
                // 곡 가중치 합: 많이 듣고, 하트·별점을 준 곡이 많은 아티스트가 먼저
                .Select(g => (Artist: g.Key, Score: g.Sum(t => t.Weight), Plays: g.Sum(t => t.PlayCount)))
                .OrderByDescending(x => x.Score)
                .Take(perPlaylist)
                .ToList();
            if (top.Count == 0) continue;
            double max = top[0].Score;
            foreach (var (artist, score, plays) in top)
            {
                var w = Math.Round(0.5 + 0.5 * score / max, 3);
                if (!best.TryGetValue(artist, out var cur) || cur.Weight < w)
                    best[artist] = new TasteArtist(artist, w, plays, p.Name);
            }
        }
        return [.. best.Values.OrderByDescending(a => a.Weight)];
    }

    /// <summary>크레이트 플레이리스트의 모든 뮤지션 (컴필레이션 곡은 곡 아티스트로).</summary>
    public static IReadOnlyList<PlaylistArtist> PlaylistArtists(
        IReadOnlyList<Playlist> playlists, IEnumerable<TrackRecord> library, CrateConfig config)
    {
        static string Who(TrackRecord t) =>
            Excluded.Contains(ListeningAnalyzer.NormalizeArtist(t.GroupArtist)) ? t.Artist : t.GroupArtist;

        // 뮤지션 → 플레이리스트별 곡 수
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in playlists.Where(p => p.Kind == PlaylistKind.User && config.Uses(p.Name)))
            foreach (var t in p.Tracks)
            {
                var a = Who(t).Trim();
                if (a.Length == 0 || Excluded.Contains(ListeningAnalyzer.NormalizeArtist(a))) continue;
                if (!counts.TryGetValue(a, out var per)) counts[a] = per = [];
                per[p.Name] = per.GetValueOrDefault(p.Name) + 1;
            }

        var titles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in library)
        {
            var a = Who(t).Trim();
            if (!counts.ContainsKey(a)) continue;
            if (!titles.TryGetValue(a, out var set)) titles[a] = set = [];
            set.Add(TitleKey(t.Title));
            set.Add(TitleKey(t.Album));
        }

        return [.. counts.Select(kv =>
        {
            var (home, n) = kv.Value.MaxBy(x => x.Value);
            var entry = config.Playlists[home];
            IReadOnlySet<string> set = titles.TryGetValue(kv.Key, out var s) ? s : new HashSet<string>();
            return new PlaylistArtist(kv.Key, home, entry.Style, entry.Region, kv.Value.Values.Sum(), set);
        }).OrderByDescending(a => a.Tracks)];
    }

    private static readonly System.Text.RegularExpressions.Regex Decorations =
        new(@"\s*(\(.*?\)|\[.*?\]|\s-\s(single|ep)$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>제목 비교용: 괄호·대괄호(Remaster, feat. 등)와 " - Single"을 떼고 정규화.</summary>
    public static string TitleKey(string? title) =>
        ListeningAnalyzer.NormalizeArtist(Decorations.Replace(title ?? "", ""));

    /// <summary>플레이리스트가 없을 때: 라이브러리 전체에서 재생 수 기준.</summary>
    public static IReadOnlyList<TasteArtist> Build(IEnumerable<TrackRecord> tracks, int top = 15)
    {
        var raw = tracks
            .GroupBy(t => t.GroupArtist, StringComparer.OrdinalIgnoreCase)
            .Where(g => !Excluded.Contains(ListeningAnalyzer.NormalizeArtist(g.Key)))
            .Select(g => (Artist: g.Key, Plays: g.Sum(t => Math.Max(t.PlayCount, 1))))
            .OrderByDescending(x => x.Plays)
            .Take(top)
            .ToList();
        if (raw.Count == 0) return [];
        double max = Math.Log(1 + raw[0].Plays);
        return [.. raw.Select(x => new TasteArtist(x.Artist, Math.Round(Math.Log(1 + x.Plays) / max, 3), x.Plays))];
    }
}
