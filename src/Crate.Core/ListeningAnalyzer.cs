namespace Crate.Core;

public static class ListeningAnalyzer
{
    /// <summary>
    /// 기간 안에 활동(마지막 재생 또는 추가)이 있는 곡을 아티스트 → 앨범으로 묶는다.
    /// iTunes는 날짜별 재생 이력이 없으므로 PlayCount는 해당 곡들의 누적 재생 수다.
    /// </summary>
    public static ListeningSummary Summarize(IEnumerable<TrackRecord> tracks, Period period, int? maxArtists = null)
    {
        var inPeriod = tracks.Where(t => t.ActivityDate is { } d && period.Contains(d));

        var artists = inPeriod
            .GroupBy(t => t.GroupArtist, StringComparer.OrdinalIgnoreCase)
            .Select(ag =>
            {
                var albums = ag
                    .GroupBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
                    .Select(alb =>
                    {
                        var sample = alb.Where(t => t.Location != null && File.Exists(t.Location))
                                        .OrderByDescending(t => t.PlayCount).FirstOrDefault();
                        return new AlbumSummary(
                            ag.Key, alb.Key, alb.Max(t => t.Year),
                            alb.Sum(t => Math.Max(t.PlayCount, 1)), alb.Count(),
                            sample?.Location, sample?.Title);
                    })
                    .OrderByDescending(a => a.PlayCount)
                    .ToList();
                return new ArtistSummary(ag.Key, albums.Sum(a => a.PlayCount), albums);
            })
            .OrderByDescending(a => a.PlayCount);

        return new ListeningSummary(period, (maxArtists is int n ? artists.Take(n) : artists).ToList());
    }

    /// <summary>달력 히트맵용: 날짜별 활동 곡 수.</summary>
    public static Dictionary<DateOnly, int> DailyActivity(IEnumerable<TrackRecord> tracks, DateTime start, DateTime end) =>
        tracks.Where(t => t.ActivityDate is { } d && d >= start && d < end)
              .GroupBy(t => DateOnly.FromDateTime(t.ActivityDate!.Value))
              .ToDictionary(g => g.Key, g => g.Count());

    public static HashSet<string> KnownArtists(IEnumerable<TrackRecord> tracks) =>
        tracks.SelectMany(t => new[] { t.Artist, t.AlbumArtist })
              .Where(a => !string.IsNullOrWhiteSpace(a))
              .Select(a => NormalizeArtist(a!))
              .ToHashSet();

    public static string NormalizeArtist(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
