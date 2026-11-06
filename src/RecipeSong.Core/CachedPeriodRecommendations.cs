namespace RecipeSong.Core;

public sealed record CachedPeriodRecommendations(IReadOnlyList<ReleaseRecommendation> Releases, int SavedWeeks, int TotalWeeks)
{
    /// <summary>없는 주를 분석하지 않고 저장된 주만 합친다. 같은 앨범은 한 번만 표시한다.</summary>
    public static CachedPeriodRecommendations Read(Period period, DateTime today,
        Func<Period, IReadOnlyList<ReleaseRecommendation>?> readWeek)
    {
        var weeks = Periods.IntersectingWeeks(period, today);
        var rows = new List<ReleaseRecommendation>();
        int saved = 0;
        foreach (var week in weeks)
            if (readWeek(week) is { } cached) { saved++; rows.AddRange(cached.Where(r => period.Contains(r.ReleaseDate))); }
        return new(rows.DistinctBy(r => r.DeezerAlbumId).OrderByDescending(r => r.ReleaseDate).ToArray(), saved, weeks.Count);
    }
}
