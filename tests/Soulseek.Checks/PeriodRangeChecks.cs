using RecipeSong.Core;
using RecipeSong.Core.Providers;

static class PeriodRangeChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var range = Periods.WeekRange(new(2020, 12, 31), new(2021, 1, 10));
        Check(range.Start == new DateTime(2020, 12, 28) && range.End == new DateTime(2021, 1, 11), "Year crossing range lost a boundary week.");
        var weeks = Periods.IntersectingWeeks(range, new(2026, 11, 4));
        Check(weeks.Count == 2 && weeks[0].Label.Contains("53"), "ISO week 53 was omitted.");
        Check(Periods.WeekRange(new(2026, 11, 8), new(2026, 11, 2)).End - new DateTime(2026, 11, 2) == TimeSpan.FromDays(7), "The minimum range must be one complete week.");
        try { Periods.WeekRange(new(2026, 11, 9), new(2026, 11, 2)); throw new Exception("Reversed weeks were accepted."); }
        catch (ArgumentException) { }
        var boundary = new ReleaseRecommendation("Artist", "Album", "album", new(2021, 1, 10), null, null, 1, null, null, Region.International, null, null, null, [], [], false, 0);
        int reads = 0;
        var cached = CachedPeriodRecommendations.Read(range, new(2026, 11, 4), _ => { reads++; return [boundary, boundary with { DeezerAlbumId = 2, ReleaseDate = new(2021, 1, 11) }]; });
        Check(reads == 2 && cached.SavedWeeks == 2 && cached.Releases.Count == 1, "Cached aggregation duplicated albums or included the exclusive end date.");
        var partial = CachedPeriodRecommendations.Read(range, new(2026, 11, 4), p => p == weeks[0] ? [] : null);
        Check(partial.SavedWeeks == 1 && partial.TotalWeeks == 2 && partial.Releases.Count == 0, "Empty cached weeks and missing weeks were confused.");
        Check(Periods.IntersectingWeeks(Periods.WeekRange(new(2026, 11, 2), new(2026, 11, 30)), new(2026, 11, 4)).Count == 1, "Future weeks were included.");

        var dates = new JsonCache<string?>("library-release-dates", TimeSpan.FromDays(3650));
        dates.Set("artist\u001fknown", "2021-01-05"); dates.Save();
        using var http = new HttpClient(new NoNetwork());
        var library = await new LibraryReleases(new DeezerClient(http)).InPeriodAsync([
            new TrackRecord { Title = "Track", Artist = "Artist", Album = "Known", Year = 2021, DateAdded = new(2021, 1, 5) },
            new TrackRecord { Title = "Track", Artist = "Artist", Album = "Unknown", Year = 2021, DateAdded = new(2021, 1, 5) }
        ], range, cachedOnly: true);
        Check(library.AlbumCount == 1, "Cache-only library lookup must reuse known dates and skip unknown dates.");
        Console.WriteLine("Week range and cache-only period checks passed.");
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new Exception("Applying a cached range must not request release metadata.");
    }
}
