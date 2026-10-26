using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

/// <summary>
/// 선택한 기간에 '발매된' 내 라이브러리 앨범.
/// 연 단위는 태그의 연도로 충분하고, 월·주 단위는 Deezer에서 정확한 발매일을 찾아 캐시한다.
/// </summary>
public sealed class LibraryReleases(DeezerClient deezer)
{
    private readonly JsonCache<string?> _dates = new("library-release-dates", TimeSpan.FromDays(3650));

    public async Task<ListeningSummary> InPeriodAsync(
        IEnumerable<TrackRecord> tracks, Period period, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 기간과 연도가 겹치는 앨범만 후보 (발매일 조회 횟수를 줄인다)
        var all = ListeningAnalyzer.Summarize(tracks.Where(t => t.Year is int y && period.OverlapsYear(y)), AllTime);
        var result = new List<ArtistSummary>();
        int done = 0, total = all.AlbumCount;

        foreach (var artist in all.Artists)
        {
            var albums = new List<AlbumSummary>();
            foreach (var album in artist.Albums)
            {
                progress?.Report($"발매일 확인 중… {++done}/{total}");
                DateOnly? released = null;
                if (period.Kind != PeriodKind.Year && !string.IsNullOrWhiteSpace(album.Album))
                {
                    var key = $"{album.Artist}\u001f{album.Album}".ToLowerInvariant();
                    var s = await _dates.GetOrAddAsync(key, async () => (await deezer.FindAlbumAsync(album.Artist, album.Album, ct))?.ReleaseDate);
                    released = DateOnly.TryParse(s, out var d) ? d : null;
                    // 발매일을 못 찾으면 월·주 단위에는 넣지 않는다
                    if (released is not { } r || !period.Contains(r)) continue;
                }
                albums.Add(album with { ReleaseDate = released });
            }
            if (albums.Count > 0)
                result.Add(artist with { Albums = albums, PlayCount = albums.Sum(a => a.PlayCount) });
        }
        _dates.Save();
        return new ListeningSummary(period, [.. result.OrderByDescending(a => a.PlayCount)]);
    }

    private static readonly Period AllTime = new(PeriodKind.Year, DateTime.MinValue, DateTime.MaxValue, "전체");
}
