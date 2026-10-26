using System.ComponentModel;
using ModelContextProtocol.Server;
using RecipeSong.Core;
using RecipeSong.Core.Library;

namespace RecipeSong.Mcp;

[McpServerToolType]
public sealed class LibraryTools(LibraryService library, ReleaseRecommender recommender, LibraryReleases libraryReleases)
{
    private const string PeriodHelp =
        "기간. '2026'(연), '2026-10'(월), '2026-W43'(ISO 주), 또는 '2026-10-21' 같은 날짜(이 경우 kind로 단위 지정).";

    [McpServerTool(Name = "recommend_releases", ReadOnly = true, OpenWorld = true)]
    [Description("선택한 기간(연·월·주)에 '발매된' 앨범·EP·싱글 중 내 크레이트(iTunes 플레이리스트) 취향에 맞는 것을 추천합니다. " +
                 "라이브러리 밖 아티스트도 포함되며, 아는 아티스트의 신보는 isKnownArtist=true로 표시됩니다. " +
                 "소리 분석 서비스가 켜져 있으면 크레이트·적합도·비슷한 내 곡이 함께 나옵니다.")]
    public async Task<object> RecommendReleases(
        [Description(PeriodHelp)] string period,
        [Description("날짜를 넘길 때의 단위: Year, Month, Week")] PeriodKind? kind = null,
        [Description("형식 필터: album, ep, single 중 선택 (비우면 전부)")] string[]? recordTypes = null,
        [Description("지역 필터: Korea, Japan, International, SoutheastAsia (비우면 전부)")] Region[]? regions = null,
        [Description("추천 개수")] int count = 24,
        CancellationToken ct = default)
    {
        var tracks = await library.GetTracksAsync(ct);
        var p = Periods.Parse(period, kind);
        var r = await recommender.RecommendAsync(await SeedsAsync(ct), new OwnedLibrary(tracks), p, ct: ct);
        return new
        {
            period = p.Label,
            start = p.Start.ToString("yyyy-MM-dd"),
            end = p.End.AddDays(-1).ToString("yyyy-MM-dd"),
            releases = r.Releases
                .Where(x => recordTypes is not { Length: > 0 } || recordTypes.Contains(x.RecordType))
                .Where(x => regions is not { Length: > 0 } || regions.Contains(x.Region))
                .Take(count)
                .Select(x => new
                {
                    x.Artist, x.Album, type = x.RecordType, releaseDate = x.ReleaseDate.ToString("yyyy-MM-dd"), x.Label,
                    genre = x.GenreName, region = x.Region.ToString(), x.Crate, x.CrateFit, nearTracks = x.NearTracks,
                    x.CoverUrl, x.Link, becauseOf = x.BecauseOf, x.IsKnownArtist, x.Score
                }),
            warnings = library.Warnings
        };
    }

    [McpServerTool(Name = "get_taste_profile", ReadOnly = true)]
    [Description("추천 시드로 쓰는 취향 아티스트: 크레이트 플레이리스트마다 많이 들은 아티스트와 가중치.")]
    public async Task<object> GetTasteProfile(CancellationToken ct = default) => await SeedsAsync(ct);

    private async Task<IReadOnlyList<TasteArtist>> SeedsAsync(CancellationToken ct)
    {
        var tracks = await library.GetTracksAsync(ct);
        var playlists = await library.GetPlaylistsAsync(ct);
        var ml = library.Settings.MlDirectory ?? RecipeSong.Core.Sound.SoundService.FindMlDirectory();
        var seeds = ml == null ? [] : TasteProfile.FromCrates(playlists, CrateConfig.Load(ml));
        return seeds.Count > 0 ? seeds : TasteProfile.Build(tracks);
    }

    [McpServerTool(Name = "get_library_releases", ReadOnly = true, OpenWorld = true)]
    [Description("선택한 기간에 '발매된' 내 라이브러리 앨범을 아티스트 → 앨범 단위로 보여줍니다. " +
                 "연 단위는 태그 연도, 월·주 단위는 Deezer에서 찾은 정확한 발매일 기준입니다.")]
    public async Task<object> GetLibraryReleases(
        [Description(PeriodHelp)] string period,
        [Description("날짜를 넘길 때의 단위: Year, Month, Week")] PeriodKind? kind = null,
        CancellationToken ct = default)
    {
        var tracks = await library.GetTracksAsync(ct);
        var p = Periods.Parse(period, kind);
        var s = await libraryReleases.InPeriodAsync(tracks, p, ct: ct);
        return new
        {
            period = p.Label,
            artists = s.Artists.Select(a => new
            {
                a.Artist, a.PlayCount,
                albums = a.Albums.Select(al => new { al.Album, al.Year, releaseDate = al.ReleaseDate?.ToString("yyyy-MM-dd"), al.PlayCount, al.TrackCount })
            })
        };
    }

    [McpServerTool(Name = "get_listening_summary", ReadOnly = true)]
    [Description("특정 기간에 '들은'(마지막 재생/추가일 기준) 음악을 아티스트 → 앨범 단위로 요약합니다. " +
                 "iTunes는 곡별 마지막 재생일만 기록하므로 재생 수는 해당 곡들의 누적값입니다.")]
    public async Task<object> GetListeningSummary(
        [Description(PeriodHelp)] string period,
        [Description("날짜를 넘길 때의 단위: Year, Month, Week")] PeriodKind? kind = null,
        [Description("최대 아티스트 수")] int maxArtists = 20,
        CancellationToken ct = default)
    {
        var tracks = await library.GetTracksAsync(ct);
        var p = Periods.Parse(period, kind);
        var s = ListeningAnalyzer.Summarize(tracks, p, maxArtists);
        return new
        {
            period = p.Label,
            totalPlays = s.TotalPlays,
            artists = s.Artists.Select(a => new
            {
                a.Artist, a.PlayCount,
                albums = a.Albums.Select(al => new { al.Album, al.Year, al.PlayCount, al.TrackCount })
            }),
            warnings = library.Warnings
        };
    }

    [McpServerTool(Name = "get_library_diagnostics", ReadOnly = true)]
    [Description("라이브러리와 iTunes 플레이리스트가 취향 학습에 적합한지 점검합니다: 곡 수, 음원 파일 보유율, " +
                 "플레이리스트 종류·크기 분포, 학습 가능한(15곡 이상) 직접 만든 플레이리스트, 거의 겹치는 플레이리스트 쌍.")]
    public async Task<LibraryDiagnostics.Report> GetLibraryDiagnostics(CancellationToken ct = default)
    {
        var tracks = await library.GetTracksAsync(ct);
        var playlists = await library.GetPlaylistsAsync(ct);
        return await Task.Run(() => LibraryDiagnostics.Analyze(tracks, playlists), ct);
    }

    [McpServerTool(Name = "search_library", ReadOnly = true)]
    [Description("내 라이브러리에서 아티스트/곡/앨범 이름으로 검색합니다. 이미 가진 음악인지 확인할 때 사용하세요.")]
    public async Task<object> SearchLibrary(
        [Description("검색어")] string query,
        [Description("최대 결과 수")] int limit = 30,
        CancellationToken ct = default)
    {
        var tracks = await library.GetTracksAsync(ct);
        return tracks
            .Where(t => t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.PlayCount)
            .Take(limit)
            .Select(t => new { t.Artist, t.Title, t.Album, t.Year, t.PlayCount, lastPlayed = t.LastPlayed?.ToString("yyyy-MM-dd"), source = t.Source.ToString() });
    }
}
