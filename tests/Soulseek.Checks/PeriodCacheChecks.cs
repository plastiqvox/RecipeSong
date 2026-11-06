using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class PeriodCacheChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var later = new DateTime(2035, 12, 31);
        for (var year = 2012; year <= 2030; year++)
        {
            var annual = Periods.Create(PeriodKind.Year, new(year, 1, 1));
            var weeks = Periods.IntersectingWeeks(annual, later);
            Check(weeks[0].Contains(annual.Start) && weeks[^1].Contains(annual.End.AddDays(-1)), "Year boundary dates were not covered.");
            Check(weeks.All(w => w.Start.DayOfWeek == DayOfWeek.Monday && w.End - w.Start == TimeSpan.FromDays(7)), "A boundary week was truncated.");
            Check(weeks.Zip(weeks.Skip(1)).All(pair => pair.First.End == pair.Second.Start), "Year has a missing or duplicated week.");
            var saved = new JsonCache<List<ReleaseRecommendation>>("period-weeks-" + year, TimeSpan.FromDays(30));
            foreach (var week in weeks) saved.Set(week.Label, []);
            saved.Save();
            var reopened = new JsonCache<List<ReleaseRecommendation>>("period-weeks-" + year, TimeSpan.FromDays(30));
            for (var month = 1; month <= 12; month++)
            {
                var monthly = Periods.Create(PeriodKind.Month, new(year, month, 1));
                var parts = Periods.IntersectingWeeks(monthly, later);
                Check(parts[0].Contains(monthly.Start) && parts[^1].Contains(monthly.End.AddDays(-1)), "Month boundary dates were not covered.");
                Check(parts.All(p => reopened.TryGetAt(p.Label, out var rows, out _) && rows.Count == 0),
                    "Opening a month after a year calculation requires a different or missing week cache.");
            }
        }
        var current = new DateTime(2026, 11, 2); // 월요일, 월초는 일요일 · 연초는 목요일
        foreach (var kind in new[] { PeriodKind.Year, PeriodKind.Month })
        {
            var parts = Periods.IntersectingWeeks(Periods.Create(kind, current), current);
            Check(parts[^1] == Periods.Create(PeriodKind.Week, current), "Current week was skipped by an unaligned month/year start.");
            Check(parts.All(w => w.Start <= current), "Future weeks must not be analysed.");
        }
        var december = Periods.IntersectingWeeks(Periods.Create(PeriodKind.Month, new(2025, 12, 1)), later)[^1];
        var january = Periods.IntersectingWeeks(Periods.Create(PeriodKind.Month, new(2026, 1, 1)), later)[0];
        Check(december == january && january.Start.Year == 2025 && january.End.Year == 2026, "Adjacent years do not share the same whole boundary week.");

        using var http = new HttpClient(new NoNetwork());
        var recommender = new ReleaseRecommender(new DeezerClient(http));
        var period = Periods.Create(PeriodKind.Week, current);
        var empty = await recommender.RecommendAsync(new CandidatePool([], current), new OwnedLibrary([]), period);
        Check(empty.CanCache && !empty.SoundScored && empty.Releases.Count == 0, "A completed empty week cannot be reused.");
        var pending = await recommender.RecommendAsync(new CandidatePool([], current, MinePending: 1), new OwnedLibrary([]), period);
        Check(!pending.CanCache, "A week with pending library candidates was incorrectly marked complete.");
        Check(!new RecommendationResult(period, [], false).CanCache, "An incomplete empty result must remain retryable.");
        try
        {
            await recommender.RecommendAsync(new CandidatePool([], current), new OwnedLibrary([]), period, ct: new CancellationToken(true));
            throw new Exception("Cancelled analysis returned a cacheable result.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("월·연 경계 주·현재 주·연간→월간 빈 주 캐시·미완료 및 취소 구분 검사 통과");
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Exception("Period cache checks must not access the network.");
    }
}
