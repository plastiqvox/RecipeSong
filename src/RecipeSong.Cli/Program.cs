// 엔진 점검용 콘솔:  dotnet run --project src/RecipeSong.Cli -- recommend 2026-W43 [crate] [top]
//                   dotnet run --project src/RecipeSong.Cli -- status
using System.Text;
using RecipeSong.Core;
using RecipeSong.Core.Library;
using RecipeSong.Core.Providers;
using RecipeSong.Core.Sound;

Console.OutputEncoding = Encoding.UTF8;
var settings = AppSettings.Load();
var library = new LibraryService(settings);
await using var sound = SoundService.TryCreate(settings);
var clock = System.Diagnostics.Stopwatch.StartNew();
var progress = new Progress<string>(m => Console.Error.WriteLine($"  [{clock.Elapsed.TotalSeconds,6:F1}s] {m}"));

var cmd = args.FirstOrDefault() ?? "status";

// 웹진 RSS에서 뽑은 "아티스트 – 앨범" 확인용 (Deezer 확인·점수 없이)
if (cmd == "webzines")
{
    var feeds = new WebzineFeeds(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    foreach (var f in WebzineFeeds.Defaults)
    {
        try
        {
            var picks = await feeds.ReadAsync(f);
            Console.WriteLine($"== {f.Name} ({f.Region}) {picks.Count}장");
            foreach (var p in picks.Take(6)) Console.WriteLine($"   {p.Artist} – {p.Album}  ({p.ReleaseDate})");
        }
        catch (Exception ex) { Console.WriteLine($"== {f.Name}: 실패 {ex.Message}"); }
    }
    return;
}
// 소리 점수를 기다리다 저장되지 못한 AI 제안을 ai-debug.log 의 '제출' 줄에서 되살린다 (API 비용 없이 Deezer 만 다시 확인).
//   ai-recover "2026-10-27 22:02"   ← 그 시각의 '제출' 줄. 앱이 켜져 있으면 앱이 캐시를 덮어쓰므로 닫고 돌린다.
// 날짜는 AI가 말한 날이 아니라 Deezer 발매일이 속한 주에 저장하고, 소리 점수는 앱에서 그 주를 열 때 매긴다.
if (cmd == "ai-recover")
{
    if (System.Diagnostics.Process.GetProcessesByName("RecipeSong.Wpf").Length > 0)
    {
        Console.WriteLine("RecipeSong 앱을 닫고 다시 실행하세요 (앱이 AI 결과 캐시를 덮어씁니다).");
        return;
    }
    if (settings.CacheDirectory is { Length: > 0 } cacheDir) CacheLocation.Directory = cacheDir;
    var at = args.ElementAtOrDefault(1) ?? "";
    var log = Path.Combine(CacheLocation.Directory, "ai-debug.log");
    var line = File.ReadLines(log).LastOrDefault(l => l.StartsWith(at) && l.Contains("] 제출 "));
    if (line == null) { Console.WriteLine($"'{at}' 의 제출 줄을 찾지 못했어요."); return; }
    // 같은 시각의 '확인' 줄에서 모델 이름 (예: Claude Sonnet 5.5)
    var by = File.ReadLines(log).Where(l => l.StartsWith(at[..Math.Min(at.Length, 16)]) && l.Contains("] 확인 "))
        .Select(l => l[(l.IndexOf('[') + 1)..l.IndexOf(']')]).FirstOrDefault() ?? "Claude";
    var picks = line[(line.IndexOf("장: ") + 3)..].Split(" | ", StringSplitOptions.RemoveEmptyEntries).Select(s =>
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.+?) – (.+) \((\d{4}-\d{2}-\d{2})\)$");
        return m.Success ? new AiPick(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, null, null,
            "다시 불러온 AI 제안 (추천 이유는 남아 있지 않아요)", null, by) : null;
    }).OfType<AiPick>().ToList();
    Console.WriteLine($"{by} 제출 {picks.Count}장 다시 확인");
    var recov = new ReleaseRecommender(new DeezerClient(new HttpClient()), new RegionResolver(new HttpClient()));
    List<ReleaseRecommendation> bare = [];
    var (_, _, olderWeeks) = await recov.ScoreAiPicksAsync(picks, new OwnedLibrary(await library.GetTracksAsync()), null, matched: b => bare = [.. b]);
    bare = [.. bare, .. olderWeeks];
    var cache = new JsonCache<List<ReleaseRecommendation>>("ai-picks", TimeSpan.FromDays(3650));
    foreach (var g in bare.GroupBy(r => Periods.Create(PeriodKind.Week, r.ReleaseDate.ToDateTime(TimeOnly.MinValue)).Label))
    {
        var list = cache.TryGet(g.Key, out var have) ? have : [];
        var add = g.Where(r => list.All(o => o.DeezerAlbumId != r.DeezerAlbumId)).ToList();
        cache.Set(g.Key, [.. list, .. add]);
        foreach (var r in g) Console.WriteLine($"  {g.Key} ← {r.Artist} – {r.Album} ({r.ReleaseDate:M.d}){(add.Contains(r) ? "" : " (이미 있음)")}");
    }
    cache.Save();
    return;
}
if (cmd == "status")
{
    if (sound == null) { Console.WriteLine("소리 분석 서비스를 찾지 못했습니다."); return; }
    var s = await sound.StatusAsync();
    Console.WriteLine($"{s.Device} {s.Gpu} · 학습 {s.TrainedAt:yyyy-MM-dd HH:mm} · {s.Tracks}곡 · 새 곡 {s.NewTracks}");
    Console.WriteLine($"레시피 {string.Join(", ", s.Styles)} ({s.StyleAccuracy:P0}) · 지역 {string.Join(", ", s.Regions)} ({s.RegionAccuracy:P0})");
    return;
}

if (cmd == "recommend")
{
    // 옵션: --crate 일본인디  --region 해외  --type album  --top 5
    string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    // 기본: 가장 최근 금요일(오늘 포함)이 있는 주
    var period = args.ElementAtOrDefault(1) is { } p && !p.StartsWith("--") ? Periods.Parse(p)
        : Periods.Create(PeriodKind.Week, DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7)));
    var crate = Opt("--crate");
    var focus = Opt("--region");
    var type = Opt("--type");
    int top = int.TryParse(Opt("--top"), out var t) ? t : 5;
    Region? regionFilter = focus switch { "해외" => Region.International, "일본" => Region.Japan, "한국" => Region.Korea, _ => null };

    var tracks = await library.GetTracksAsync();
    var playlists = await library.GetPlaylistsAsync();
    var ml = settings.MlDirectory ?? SoundService.FindMlDirectory()!;
    var seeds = TasteProfile.FromCrates(playlists, CrateConfig.Load(ml), focusRegion: focus);
    Console.WriteLine($"{period.Label} · 시드 {seeds.Count}명 · 소리 분석 {(sound != null ? "켜짐" : "꺼짐")}");

    var http = new HttpClient();
    var regionHttp = new HttpClient();
    var rec = new ReleaseRecommender(new DeezerClient(http), new RegionResolver(regionHttp), sound);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mine = TasteProfile.PlaylistArtists(playlists, tracks, CrateConfig.Load(ml));
    var pool = await rec.BuildPoolAsync(seeds, mine, progress: progress);
    if (pool.MinePending > 0)
    {
        Console.WriteLine($"플리 뮤지션 {pool.MinePending}명 Deezer에서 받는 중…");
        await rec.RefreshMineAsync(mine, progress);
        pool = await rec.BuildPoolAsync(seeds, mine, progress: progress);
    }
    Console.WriteLine($"후보 풀: 아티스트 {pool.Artists.Count}명 (플리 뮤지션 {pool.Artists.Count(a => a.Mine != null)}), {sw.Elapsed.TotalSeconds:F1}초");
    var owned = new OwnedLibrary(tracks);
    // 같은 풀로 앞의 두 주를 더 돌려서 기간별 추천 속도를 확인
    foreach (var extra in Enumerable.Range(1, 2).Select(w => Periods.Create(PeriodKind.Week, period.Start.AddDays(-7 * w))))
    {
        sw.Restart();
        var x = await rec.RecommendAsync(pool, owned, extra);
        Console.WriteLine($"  {extra.Label}: {x.Releases.Count}장, {sw.Elapsed.TotalSeconds:F1}초");
    }
    sw.Restart();
    var r = await rec.RecommendAsync(pool, owned, period, progress: progress);
    Console.WriteLine($"{period.Label}: {r.Releases.Count}장, {sw.Elapsed.TotalSeconds:F1}초\n");

    var shown = r.Releases
        .Where(x => regionFilter == null || x.Region == regionFilter)
        .Where(x => type == null || x.RecordType == type);
    foreach (var g in shown.GroupBy(x => x.Crate ?? "(소리 분석 안 됨)")
                 .Where(g => crate == null || g.Key == crate)
                 .OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"▌{g.Key} · {g.Count()}장");
        foreach (var x in g.Take(top))
            Console.WriteLine($"  {x.Artist} — {x.Album} [{x.RecordType}] {x.ReleaseDate:MM.dd} · {x.Label} · {x.Region}" +
                              $" · 적합 {x.CrateFit:P0} · 취향 {x.Taste:F3}{(x.FromPlaylist != null ? $" · 내 플리 {x.FromPlaylist}" : x.IsKnownArtist ? " · 아는 아티스트" : "")}\n" +
                              $"     비슷한 내 곡: {string.Join(" / ", x.NearTracks)}");
        Console.WriteLine();
    }
}
