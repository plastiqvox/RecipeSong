namespace Crate.Core.Library;

/// <summary>플레이리스트 학습에 쓸 만한 라이브러리인지 점검한다.</summary>
public static class LibraryDiagnostics
{
    public const int MinTrainingSize = 15;

    public sealed record PlaylistInfo(
        string Name, string? Folder, int Tracks, double FileRatio, string? TopArtist, string? TopGenre);

    public sealed record GenreCount(string Genre, int Tracks);

    public sealed record OverlapPair(string A, string B, double Jaccard);

    public sealed record Report(
        int Tracks, int WithFile, int Played, int Loved, int Artists,
        IReadOnlyList<GenreCount> TopGenres,
        IReadOnlyDictionary<PlaylistKind, int> PlaylistsByKind,
        int UserPlaylistsTrainable, int UserMedianSize, int UserMaxSize,
        int TracksInUserPlaylists, double AvgPlaylistsPerTrack,
        IReadOnlyList<PlaylistInfo> UserPlaylists,
        IReadOnlyList<PlaylistInfo> SmartPlaylists,
        int HighOverlapPairs, IReadOnlyList<OverlapPair> OverlapExamples);

    public static Report Analyze(IReadOnlyList<TrackRecord> tracks, IReadOnlyList<Playlist> playlists)
    {
        var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool HasFile(TrackRecord t) =>
            t.Location != null && (exists.TryGetValue(t.Location, out var e) ? e : exists[t.Location] = File.Exists(t.Location));

        var folders = playlists.Where(p => p.Kind == PlaylistKind.Folder && p.PersistentId != null)
                               .ToDictionary(p => p.PersistentId!, p => p.Name);
        PlaylistInfo Info(Playlist p) => new(
            p.Name,
            p.ParentPersistentId != null ? folders.GetValueOrDefault(p.ParentPersistentId) : null,
            p.Tracks.Count,
            p.Tracks.Count == 0 ? 0 : Math.Round((double)p.Tracks.Count(HasFile) / p.Tracks.Count, 2),
            p.Tracks.GroupBy(t => t.GroupArtist).MaxBy(g => g.Count())?.Key,
            p.Tracks.Where(t => t.Genre != null).GroupBy(t => t.Genre!).MaxBy(g => g.Count())?.Key);

        var user = playlists.Where(p => p.Kind == PlaylistKind.User && p.Tracks.Count > 0)
                            .OrderByDescending(p => p.Tracks.Count).ToList();
        var sizes = user.Select(p => p.Tracks.Count).OrderBy(n => n).ToList();
        var membership = user.SelectMany(p => p.Tracks).GroupBy(t => t).ToList();

        // 거의 같은 플레이리스트(복사본, 하위 버전)는 학습에서 합치거나 하나만 쓴다
        var sets = user.Where(p => p.Tracks.Count >= MinTrainingSize)
                       .Select(p => (p.Name, Set: p.Tracks.ToHashSet())).ToList();
        var overlaps = new List<OverlapPair>();
        for (int i = 0; i < sets.Count; i++)
            for (int j = i + 1; j < sets.Count; j++)
            {
                int inter = sets[i].Set.Count(sets[j].Set.Contains);
                if (inter == 0) continue;
                double jac = (double)inter / (sets[i].Set.Count + sets[j].Set.Count - inter);
                if (jac >= 0.6) overlaps.Add(new OverlapPair(sets[i].Name, sets[j].Name, Math.Round(jac, 2)));
            }

        return new Report(
            tracks.Count, tracks.Count(HasFile), tracks.Count(t => t.PlayCount > 0), tracks.Count(t => t.Loved),
            tracks.Select(t => t.GroupArtist).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            [.. tracks.Where(t => !string.IsNullOrWhiteSpace(t.Genre)).GroupBy(t => t.Genre!, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()).Take(15).Select(g => new GenreCount(g.Key, g.Count()))],
            playlists.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count()),
            user.Count(p => p.Tracks.Count >= MinTrainingSize),
            sizes.Count == 0 ? 0 : sizes[sizes.Count / 2],
            sizes.Count == 0 ? 0 : sizes[^1],
            membership.Count,
            membership.Count == 0 ? 0 : Math.Round(membership.Average(g => g.Count()), 2),
            [.. user.Select(Info)],
            [.. playlists.Where(p => p.Kind == PlaylistKind.Smart).OrderByDescending(p => p.Tracks.Count).Take(30).Select(Info)],
            overlaps.Count,
            [.. overlaps.OrderByDescending(o => o.Jaccard).Take(15)]);
    }
}
