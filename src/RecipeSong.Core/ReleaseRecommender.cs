using RecipeSong.Core.Ai;
using RecipeSong.Core.Providers;
using RecipeSong.Core.Sound;

namespace RecipeSong.Core;

public sealed record RecommendOptions
{
    /// <summary>디스코그래피를 확인할 후보 아티스트 수</summary>
    public int MaxArtists { get; init; } = 1000;
    /// <summary>소리 분석까지 할 발매작 수 (아티스트 연결 점수 상위)</summary>
    public int MaxScored { get; init; } = 150;
    /// <summary>플레이리스트 뮤지션 발매작 중 소리 분석까지 할 수 (나머지도 목록에는 나온다)</summary>
    public int MaxMineScored { get; init; } = 300;
    /// <summary>2단계 확장의 출발점이 되는 1단계 아티스트 수</summary>
    public int Hop2Seeds { get; init; } = 120;
    /// <summary>점수에서 아티스트 연결 강도의 비중 (1 = 익숙한 쪽, 0 = 소리만 보고 새로운 쪽)</summary>
    public double GraphWeight { get; init; } = 0.5;
    /// <summary>점수에서 "저장함 앨범과 닮은 정도"의 비중</summary>
    public double FavoriteWeight { get; init; } = 1.0;
    /// <summary>점수에서 "별로 표시한 앨범과 닮은 정도"를 빼는 비중</summary>
    public double DislikeWeight { get; init; } = 1.0;
    /// <summary>좋아하는 아티스트 (정규화 이름): 수록곡에 참여했으면 가산점 (피처링·참여 연결)</summary>
    public IReadOnlySet<string> LikedArtists { get; init; } = new HashSet<string>();
    public double WithLikedWeight { get; init; } = 0.6;
    /// <summary>형식 취향: album/ep/single → 가산점 (내 라이브러리에서 그 형식을 얼마나 듣는지)</summary>
    public IReadOnlyDictionary<string, double> TypeBonus { get; init; } = new Dictionary<string, double>();
    /// <summary>작은 아티스트 우대 (Deezer 팬 수) 비중. 디깅 앱이라 기본으로 켠다.</summary>
    public double SmallArtistWeight { get; init; } = 1.0;

    /// <summary>차단한 아티스트(정규화 이름)·레이블(LabelAffinity 키): 어디서도 추천하지 않는다</summary>
    public IReadOnlySet<string> BlockedArtists { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> BlockedLabels { get; init; } = new HashSet<string>();
    /// <summary>동명이인으로 막은 Deezer 아티스트 id (이름이 같은 다른 사람은 그대로)</summary>
    public IReadOnlySet<long> BlockedArtistIds { get; init; } = new HashSet<long>();
    /// <summary>원래 앨범이 있으면 인스트루멘털 버전은 뺀다</summary>
    public bool ExcludeInstrumental { get; init; } = true;

    /// <summary>좋아하는 레이블 (LabelAffinity.Normalize 이름 → 0~1). 같은 레이블 신보에 가산점</summary>
    public IReadOnlyDictionary<string, double> LabelAffinity { get; init; } = new Dictionary<string, double>();
    public double LabelWeight { get; init; } = 1.0;
    /// <summary>별로 표시한 앨범의 아티스트 (정규화 이름): 추가 제안에서 뺀다 (내 플리 뮤지션은 그대로)</summary>
    public IReadOnlySet<string> DislikedArtists { get; init; } = new HashSet<string>();
    /// <summary>추가 제안에서 내 라이브러리에 있는 아티스트는 뺀다 (처음 듣는 사람만)</summary>
    public bool ExcludeKnown { get; init; }

    /// <summary>
    /// 기간 길이에 맞춘 한도: 주 기준으로 월은 4배, 연은 12배 (연을 주 52개만큼 하면 너무 오래 걸려 상한).
    /// 한 달을 봐도 주 단위로 네 번 본 것과 비슷하게 촘촘하게.
    /// </summary>
    public RecommendOptions ForPeriod(PeriodKind kind)
    {
        int f = kind switch { PeriodKind.Month => 4, PeriodKind.Year => 12, _ => 1 };
        return this with { MaxScored = MaxScored * f, MaxMineScored = MaxMineScored * f };
    }

    /// <summary>추천 양: 0 기본 / 1 많이 / 2 최대</summary>
    public static RecommendOptions Create(int amount, int adventure, bool excludeKnown) => new()
    {
        MaxArtists = amount switch { 1 => 2000, 2 => 3000, _ => 1000 },
        Hop2Seeds = amount switch { 1 => 200, 2 => 300, _ => 120 },
        MaxScored = amount switch { 1 => 300, 2 => 500, _ => 150 },
        MaxMineScored = amount switch { 1 => 500, 2 => 800, _ => 300 },
        GraphWeight = adventure switch { 0 => 1.0, 2 => 0.0, _ => 0.5 },
        ExcludeKnown = excludeKnown,
    };
    /// <summary>추천 중에 지역을 MusicBrainz에 새로 물어볼 최대 횟수 (초당 1회 제한, 나머지는 백그라운드로)</summary>
    public int MaxRegionLookups { get; init; } = 0;
}

/// <summary>후보 아티스트와 디스코그래피. 기간과 상관없으므로 한 번 모아 두고 여러 기간에 쓴다.</summary>
/// <param name="MinePending">플레이리스트 뮤지션 중 아직 Deezer에서 받지 않았거나 디스코그래피가 오래된 수 (RefreshMineAsync로 채운다)</param>
public sealed record CandidatePool(IReadOnlyList<PoolArtist> Artists, DateTime BuiltAt, int MinePending = 0);

/// <param name="Mine">플레이리스트 뮤지션이면 그 정보 (신보를 항상 보여준다)</param>
public sealed record PoolArtist(long Id, string Name, double Score, IReadOnlyList<string> Because, IReadOnlyList<DzAlbum> Albums,
    PlaylistArtist? Mine = null, int Fans = 0);

/// <summary>
/// 발매일 기준 추천: 선택한 기간(달력의 주/월/연)에 발매된 앨범·EP·싱글 중 내 크레이트 취향에 맞는 것.
/// 1) 시드: 크레이트 플레이리스트마다 많이 들은 아티스트 → Deezer 비슷한 아티스트 2단계 확장
/// 2) 후보 아티스트 디스코그래피에서 기간 내 발매작 (이미 가진 앨범 제외)
/// 3) 소리 분석 서비스로 미리듣기를 들어 크레이트·지역·취향 유사도를 매긴다
/// 4) 점수 = 취향 유사도(z) + 2 × 크레이트 적합도 + 0.5 × 아티스트 연결(z)
/// </summary>
public sealed class ReleaseRecommender(DeezerClient deezer, RegionResolver regions, SoundService? sound = null)
{
    private readonly JsonCache<long?> _artistIds = new("deezer-artist-ids", TimeSpan.FromDays(90));
    // 플레이리스트 뮤지션: 이름 정확히 일치 + 라이브러리 제목 확인을 거친 Deezer ID (없으면 null)
    private readonly JsonCache<long?> _mineIds = new("deezer-playlist-artist-ids", TimeSpan.FromDays(90));
    private readonly JsonCache<List<DzArtist>> _related = new("deezer-related", TimeSpan.FromDays(14));
    private readonly JsonCache<List<DzAlbum>> _discography = new("deezer-discography", TimeSpan.FromDays(3));
    private readonly JsonCache<AlbumInfo> _albumInfo = new("deezer-album-info", TimeSpan.FromDays(365));

    private sealed record AlbumInfo(string? Label, string? Genre, IReadOnlyList<string>? With = null);

    private static readonly Dictionary<string, Region> SoundRegions = new()
    {
        ["일본"] = Region.Japan, ["한국"] = Region.Korea, ["해외"] = Region.International, ["동남아"] = Region.SoutheastAsia
    };

    public SoundService? Sound => sound;

    /// <summary>한국 플레이리스트(K-indie) 뮤지션의 크레이트 이름</summary>
    public const string KoreanCrate = "한국음악";

    public const string OverseasIndie = "해외 인디·얼터", OverseasElectronic = "해외 일렉트로닉", OverseasSoul = "해외 소울·힙합";

    /// <summary>해외 크레이트 (일본 크레이트 다음, 한국음악 앞)</summary>
    public static readonly string[] OverseasCrates = [OverseasIndie, OverseasElectronic, OverseasSoul];

    // Deezer 장르 → 해외 크레이트. Jazz는 기존 Jazz 크레이트로. Pop·장르 없음은 소리로 정한다.
    private static readonly Dictionary<string, string> OverseasByGenre = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alternative"] = OverseasIndie, ["Rock"] = OverseasIndie, ["Folk"] = OverseasIndie, ["Metal"] = OverseasIndie,
        ["Singer & Songwriter"] = OverseasIndie, ["Indie Pop"] = OverseasIndie, ["Indie Rock"] = OverseasIndie,
        ["Electro"] = OverseasElectronic, ["Dance"] = OverseasElectronic, ["Disco"] = OverseasElectronic,
        ["Techno/House"] = OverseasElectronic, ["House"] = OverseasElectronic,
        ["R&B"] = OverseasSoul, ["Rap/Hip Hop"] = OverseasSoul, ["Soul & Funk"] = OverseasSoul, ["Hip Hop"] = OverseasSoul,
        ["Jazz"] = "Jazz",
    };

    private static readonly Dictionary<int, string> GenreNames = new()
    {
        [132] = "Pop", [116] = "Rap/Hip Hop", [152] = "Rock", [113] = "Dance", [165] = "R&B", [85] = "Alternative",
        [106] = "Electro", [466] = "Folk", [129] = "Jazz", [464] = "Metal", [169] = "Soul & Funk",
    };

    /// <summary>해외 발매작의 크레이트: Deezer 장르, 장르가 Pop이거나 없으면 소리 모델 (Club 결 → 일렉트로닉, 그 외 → 인디·얼터).</summary>
    public static string OverseasCrate(string? genre, int? genreId, string? soundCrate)
    {
        genre ??= genreId is { } id ? GenreNames.GetValueOrDefault(id) : null;
        if (genre != null && OverseasByGenre.TryGetValue(genre, out var crate)) return crate;
        return soundCrate switch
        {
            "Club" => OverseasElectronic,
            "Jazz" => "Jazz",
            _ => OverseasIndie
        };
    }

    /// <summary>시드에서 후보 풀을 만들고 바로 추천한다 (한 번만 쓸 때).</summary>
    public async Task<RecommendationResult> RecommendAsync(
        IReadOnlyList<TasteArtist> seeds, OwnedLibrary owned, Period period,
        RecommendOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var pool = await BuildPoolAsync(seeds, null, options, progress, ct);
        return await RecommendAsync(pool, owned, period, options, progress, ct);
    }

    /// <summary>
    /// 후보 풀 = 플레이리스트 뮤지션(mine, 항상 표시) + 추가 제안(시드 → 비슷한 아티스트 2단계 → 디스코그래피).
    /// 플레이리스트 뮤지션은 캐시에 있는 것만 쓴다 (수천 명이라 처음엔 RefreshMineAsync로 뒤에서 모은다).
    /// </summary>
    public async Task<CandidatePool> BuildPoolAsync(
        IReadOnlyList<TasteArtist> seeds, IReadOnlyList<PlaylistArtist>? mine = null, RecommendOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new RecommendOptions();
        var suggestions = seeds.Count > 0 ? await SuggestionsAsync(seeds, options, progress, ct) : [];
        if (mine is not { Count: > 0 }) return new CandidatePool(suggestions, DateTime.Now);

        var byId = suggestions.ToDictionary(a => a.Id);
        int pending = 0;
        foreach (var m in mine)
        {
            if (!_mineIds.TryGetStale(ListeningAnalyzer.NormalizeArtist(m.Artist), out var id, out _)) { pending++; continue; }
            if (id is not { } artistId) continue;  // Deezer에 없음 / 동명이인
            if (!_discography.TryGetStale(artistId.ToString(), out var albums, out var fresh)) { pending++; continue; }
            if (!fresh) pending++;
            byId[artistId] = byId.TryGetValue(artistId, out var s)
                ? s with { Mine = m }
                : new PoolArtist(artistId, m.Artist, 0, [m.Playlist], albums, m);
        }
        return new CandidatePool([.. byId.Values], DateTime.Now, pending);
    }

    /// <summary>
    /// 플레이리스트 뮤지션을 Deezer에서 찾고 디스코그래피를 새로 받는다 (요청 제한 때문에 처음엔 10분 이상).
    /// 이름이 정확히 같고, 디스코그래피에 내 라이브러리 곡·앨범 제목이 하나라도 있어야 같은 뮤지션으로 본다.
    /// </summary>
    public async Task RefreshMineAsync(IReadOnlyList<PlaylistArtist> mine, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var todo = mine.Where(m =>
        {
            var key = ListeningAnalyzer.NormalizeArtist(m.Artist);
            if (!_mineIds.TryGetStale(key, out var id, out _)) return true;
            return id is { } i && !_discography.TryGet(i.ToString(), out _);
        }).ToList();
        int done = 0;
        // 동시에 8명씩: 요청이 한꺼번에 줄 서지 않아 진행률이 고르게 오르고, 중간에 꺼져도 받은 만큼 남는다
        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (m, ct) =>
            {
                var key = ListeningAnalyzer.NormalizeArtist(m.Artist);
                if (!_mineIds.TryGetStale(key, out var id, out _))
                {
                    // 이름이 정확히 같은 후보 중 디스코그래피가 내 라이브러리와 겹치는 아티스트
                    id = null;
                    foreach (var a in await deezer.SearchArtistsAsync(m.Artist, ct))
                    {
                        if (ListeningAnalyzer.NormalizeArtist(a.Name) != key) continue;
                        var albums = await deezer.ArtistAlbumsAsync(a.Id, ct);
                        _discography.Set(a.Id.ToString(), [.. albums]);
                        if (albums.Any(al => m.Titles.Contains(TasteProfile.TitleKey(al.Title)) && TasteProfile.TitleKey(al.Title).Length > 0))
                        {
                            id = a.Id;
                            break;
                        }
                    }
                    _mineIds.Set(key, id);
                }
                else if (id is { } i)
                    _discography.Set(i.ToString(), [.. await deezer.ArtistAlbumsAsync(i, ct)]);
    
                int d = Interlocked.Increment(ref done);
                if (d % 25 == 0) progress?.Report(L.F("플레이리스트 뮤지션 {0}/{1}", d, todo.Count));
                if (d % 250 == 0) SaveCaches();
            });
        }
        finally { SaveCaches(); }
    }

    private async Task<List<PoolArtist>> SuggestionsAsync(
        IReadOnlyList<TasteArtist> seeds, RecommendOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        // 1) 시드 → 비슷한 아티스트 2단계
        progress?.Report(L.T("취향 아티스트 찾는 중…"));
        var seedIds = (await Task.WhenAll(seeds.Select(async s => (s, id: await ArtistIdAsync(s.Artist, ct)))))
            .Where(x => x.id != null).Select(x => (x.s, id: x.id!.Value)).ToList();

        var cands = new Dictionary<long, Candidate>();
        foreach (var (s, id) in seedIds)
            Add(cands, id, s.Artist, s.Weight, s.Artist);

        progress?.Report(L.T("비슷한 아티스트 모으는 중…"));
        var hop1 = new List<(DzArtist a, double score, string seed)>();
        foreach (var (s, rel) in await Task.WhenAll(seedIds.Select(async x => (x.s, rel: await RelatedAsync(x.id, 25, ct)))))
            for (int i = 0; i < rel.Count; i++)
            {
                double sc = s.Weight * (1 - 0.7 * i / rel.Count);
                Add(cands, rel[i].Id, rel[i].Name, sc, s.Artist, rel[i].Fans);
                hop1.Add((rel[i], sc, s.Artist));
            }

        var hop2Seeds = hop1.OrderByDescending(h => h.score).DistinctBy(h => h.a.Id).Take(options.Hop2Seeds).ToList();
        int n2 = 0;
        foreach (var (h, rel) in await Task.WhenAll(hop2Seeds.Select(async h => (h, rel: await RelatedAsync(h.a.Id, 15, ct)))))
        {
            progress?.Report(L.F("비슷한 아티스트 넓히는 중… {0}/{1}", ++n2, hop2Seeds.Count));
            for (int i = 0; i < rel.Count; i++)
                Add(cands, rel[i].Id, rel[i].Name, 0.5 * h.score * (1 - 0.7 * i / rel.Count), h.seed, rel[i].Fans);
        }

        // 2) 기간 내 발매작
        var pool = cands.Values.OrderByDescending(c => c.Score).Take(options.MaxArtists).ToList();
        int done = 0;
        var discos = await Task.WhenAll(pool.Select(async c =>
        {
            var albums = await _discography.GetOrAddAsync(c.Id.ToString(), async () => [.. await deezer.ArtistAlbumsAsync(c.Id, ct)]);
            int d = Interlocked.Increment(ref done);
            if (d % 25 == 0) progress?.Report(L.F("디스코그래피 확인 중… {0}/{1}", d, pool.Count));
            return (c, albums);
        }));
        SaveCaches();
        return [.. discos.Select(x => new PoolArtist(x.c.Id, x.c.Name, x.c.Score, [.. x.c.Because.Distinct().Take(3)], x.albums, Fans: x.c.Fans))];
    }

    /// <summary>
    /// 2~4) 모아 둔 후보에서 기간 내 발매작을 골라 소리·지역·점수를 매긴다.
    /// 플레이리스트 뮤지션의 발매작은 모두 넣고 (앞쪽), 추가 제안은 아티스트 연결 점수 상위 MaxScored장.
    /// </summary>
    public async Task<RecommendationResult> RecommendAsync(
        CandidatePool pool, OwnedLibrary owned, Period period,
        RecommendOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new RecommendOptions();
        var inPeriod = pool.Artists
            .SelectMany(c => c.Albums
                .Where(a => a.Released is { } d && period.Contains(d) && a.RecordType != "compile")
                .Where(a => !owned.HasAlbum(c.Name, a.Title))
                .Select(a => (c, a)))
            .DistinctBy(x => x.a.Id)
            .Where(x => !options.BlockedArtists.Contains(ListeningAnalyzer.NormalizeArtist(x.c.Name)) && !options.BlockedArtistIds.Contains(x.c.Id))
            .Where(x => !(options.ExcludeInstrumental && IsRedundantInstrumental(x.c, x.a, owned)))
            .ToList();
        var mineHits = inPeriod.Where(x => x.c.Mine != null)
            .OrderByDescending(x => x.c.Mine!.Tracks * TypeWeight(x.a.RecordType)).ToList();
        var others = inPeriod.Where(x => x.c.Mine == null && !(options.ExcludeKnown && owned.HasArtist(x.c.Name))
                && !options.DislikedArtists.Contains(ListeningAnalyzer.NormalizeArtist(x.c.Name)))
            .OrderByDescending(x => x.c.Score * TypeWeight(x.a.RecordType))
            .Take(options.MaxScored).ToList();
        var hits = mineHits.Concat(others).ToList();
        if (hits.Count == 0) return new RecommendationResult(period, [], false);

        // 연·월 단위면 플리 뮤지션 발매작이 수백 장이라 앞쪽만 소리를 듣는다
        var toScore = mineHits.Take(options.MaxMineScored).Concat(others).Select(h => h.a.Id).ToList();
        var (recs, heardAny) = await ScoreHitsAsync(hits, toScore, owned, options, progress, ct);
        // 차단 레이블은 레이블을 알게 된 뒤에 뺀다
        if (options.BlockedLabels.Count > 0)
            recs.RemoveAll(r => RecipeSong.Core.LabelAffinity.Keys(r.Label).Any(options.BlockedLabels.Contains));
        progress?.Report(L.F("추천 {0}장", recs.Count));
        return new RecommendationResult(period,
            [.. recs.OrderByDescending(r => r.FromPlaylist != null).ThenByDescending(r => r.Score)],
            heardAny);
    }

    /// <summary>
    /// AI 커넥터가 찾은 발매작: Deezer에서 실제로 있는지 확인하고 (없으면 버린다) 같은 방식으로 소리·지역·점수를 매긴다.
    /// </summary>
    public async Task<(IReadOnlyList<ReleaseRecommendation> Recs, int Unverified)> ScoreAiPicksAsync(
        IReadOnlyList<AiPick> picks, OwnedLibrary owned, Period? period,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(L.F("AI가 찾은 {0}장 Deezer에서 확인 중…", picks.Count));
        var found = await Task.WhenAll(picks.Select(async p => (p, m: await MatchAiPickAsync(p, period, ct))));
        foreach (var (p, m) in found)
            AiDebugLog.Write(p.By, $"{(m.Album != null ? "확인" : "제외")} {p.Artist} – {p.Album} ({p.ReleaseDate}): {m.Note}");
        var hits = new List<(PoolArtist c, DzAlbum a)>();
        // 여러 커넥터가 같은 앨범을 고르면 한 장으로 합치고 누가 골랐는지 모두 남긴다
        var byAlbum = new Dictionary<long, List<AiPick>>();
        int accepted = 0;
        foreach (var (p, m) in found)
        {
            if (m.Album is not { Artist: { } artist } a) continue;
            if (owned.HasAlbum(artist.Name, a.Title)) continue;
            accepted++;
            if (byAlbum.TryGetValue(a.Id, out var same)) { same.Add(p); continue; }
            hits.Add((new PoolArtist(artist.Id, artist.Name, 0, ["AI"], [a]), a));
            byAlbum[a.Id] = [p];
        }
        if (hits.Count == 0) return ([], picks.Count);
        // 지역은 AI가 더 잘 안다 (이름만으로는 모르는 아티스트가 많다) → 크레이트(해외 장르 등)도 그 지역 기준으로 정해진다
        var regionHints = byAlbum
            .Where(kv => SoundRegions.ContainsKey(kv.Value[0].Region ?? ""))
            .ToDictionary(kv => kv.Key, kv => SoundRegions[kv.Value[0].Region!]);
        var (recs, _) = await ScoreHitsAsync(hits, [.. hits.Select(h => h.a.Id)], owned, new RecommendOptions(), progress, ct, regionHints);
        return ([.. recs.Select(r =>
        {
            var ps = byAlbum[r.DeezerAlbumId].DistinctBy(p => p.By).ToList();
            return r with
            {
                AiReason = ps.Count == 1 ? ps[0].Reason : string.Join("  /  ", ps.Select(p => $"{p.By}: {p.Reason}")),
                AiBy = [.. ps.Select(p => p.By)],
                BecauseOf = ["AI 제안"],
                SourceUrl = ps[0].Source,
            };
        }).OrderByDescending(r => r.Score)], picks.Count - accepted);
    }

    /// <summary>
    /// AI가 말한 앨범을 Deezer에서 찾는다. 표기가 조금 달라도 (괄호 꼬리말, 원어/영문, EP·싱글 표기) 같은 앨범으로 본다.
    /// 1) 일반 검색 상위 10개 중 아티스트·제목이 맞는 것
    /// 2) 없으면 그 아티스트의 디스코그래피에서 제목이 맞는 것, 그래도 없으면 그 기간에 나온 그 아티스트의 발매작
    /// 발매일: Deezer 날짜가 기간 ±3일이거나, AI가 확인했다는 날짜가 기간 안이고 Deezer 날짜와 30일 이내 (선공개 싱글 날짜가 붙는 경우)
    /// </summary>
    private async Task<(DzAlbum? Album, string Note)> MatchAiPickAsync(AiPick p, Period? period, CancellationToken ct)
    {
        var artistKey = ListeningAnalyzer.NormalizeArtist(p.Artist);
        var titleKey = TasteProfile.TitleKey(p.Album);
        bool SameArtist(string? name) => name != null && ListeningAnalyzer.NormalizeArtist(name) is var n && n.Length > 0
            && (n == artistKey || n.Length >= 3 && artistKey.Length >= 3 && (n.Contains(artistKey) || artistKey.Contains(n)));
        bool SameTitle(string title) => TasteProfile.TitleKey(title) is var t && t.Length > 0 && titleKey.Length > 0
            && (t == titleKey || t.Length >= 3 && titleKey.Length >= 3 && (t.StartsWith(titleKey) || titleKey.StartsWith(t)));
        DateOnly? aiDate = DateOnly.TryParse(p.ReleaseDate, out var ad) ? ad : null;
        // 기간이 없으면 (웹진) 최근 1년 안 발매작이면 받는다
        bool InPeriod(DateOnly d) => period is not { } pr
            ? d >= DateOnly.FromDateTime(DateTime.Today.AddYears(-1))
            : Enumerable.Range(-3, 7).Any(k => pr.Contains(d.AddDays(k)))
              || aiDate is { } x && pr.Contains(x) && Math.Abs(d.DayNumber - x.DayNumber) <= 30;

        async Task<(DzAlbum?, string)> Check(long id, string how)
        {
            var full = await deezer.AlbumAsync(id, ct);
            if (full?.Released is not { } d) return (null, $"{how}: 발매일 없음");
            return InPeriod(d) ? (full, $"{how} (Deezer {d:yyyy-MM-dd})") : (null, $"{how}: 발매일 {d:yyyy-MM-dd}가 기간 밖");
        }

        // 1) 일반 검색
        var search = await deezer.SearchAlbumsAsync($"{p.Artist} {p.Album}", 10, ct);
        if (search.FirstOrDefault(a => SameArtist(a.Artist?.Name) && SameTitle(a.Title)) is { } hit)
            return await Check(hit.Id, "검색 일치");

        // 2) 아티스트 디스코그래피
        var artist = (await deezer.SearchArtistsAsync(p.Artist, ct)).FirstOrDefault(a => SameArtist(a.Name));
        if (artist == null) return (null, "Deezer에 아티스트 없음");
        var albums = await _discography.GetOrAddAsync(artist.Id.ToString(), async () => [.. await deezer.ArtistAlbumsAsync(artist.Id, ct)]);
        if (albums.FirstOrDefault(a => SameTitle(a.Title)) is { } byTitle)
            return await Check(byTitle.Id, "디스코그래피 제목 일치");
        if (period != null && albums.Where(a => a.Released is { } d && period.Value.Contains(d) && a.RecordType != "compile")
                .OrderByDescending(a => TypeWeight(a.RecordType)).FirstOrDefault() is { } inPeriod)
            return await Check(inPeriod.Id, $"제목은 다르지만 기간 내 발매작 '{inPeriod.Title}'");
        return (null, $"아티스트는 있지만 해당 앨범 없음 (Deezer 앨범 {albums.Count}장)");
    }

    /// <summary>3~4) 발매작 목록에 소리·레이블·지역·점수를 매긴다. toScore에 있는 것만 소리를 듣는다.</summary>
    /// <summary>
    /// 팔로우한 아티스트의 기간 내 발매작 (저장된 추천 결과에도 더할 수 있게 따로). 디스코그래피는 캐시, 없으면 Deezer에서.
    /// 점수는 플리 뮤지션과 같은 방식이라 항상 앞에 놓인다.
    /// </summary>
    public async Task<List<ReleaseRecommendation>> FollowedAsync(IReadOnlyList<PlaylistArtist> follows, OwnedLibrary owned, Period period,
        RecommendOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hits = new List<(PoolArtist c, DzAlbum a)>();
        foreach (var f in follows.Where(f => f.DeezerId != 0))
        {
            var albums = await FollowedDiscographyAsync(f.DeezerId, ct);
            var c = new PoolArtist(f.DeezerId, f.Artist, 0, [f.Playlist], albums, f);
            hits.AddRange(albums.Where(a => a.Released is { } d && period.Contains(d) && a.RecordType != "compile" && !owned.HasAlbum(f.Artist, a.Title)
                    && !(options.ExcludeInstrumental && IsRedundantInstrumental(c, a, owned)))
                .DistinctBy(a => a.Id).Select(a => (c, a)));
        }
        SaveCaches();
        if (hits.Count == 0) return [];
        var (recs, _) = await ScoreHitsAsync(hits, [.. hits.Select(h => h.a.Id)], owned, options, progress, ct);
        return recs;
    }

    public async Task<List<DzAlbum>> FollowedDiscographyAsync(long artistId, CancellationToken ct = default) =>
        _discography.TryGet(artistId.ToString(), out var cached) ? cached
            : await _discography.GetOrAddAsync(artistId.ToString(), async () => [.. await deezer.ArtistAlbumsAsync(artistId, ct)]);

    /// <summary>캐시에 있는 디스코그래피만 (달력 표시용, 요청 없음)</summary>
    public IReadOnlyList<DzAlbum> CachedDiscography(long artistId) =>
        _discography.TryGetStale(artistId.ToString(), out var albums, out _) ? albums : [];

    /// <summary>"앨범 (Instrumental)"인데 같은 아티스트의 원래 앨범이 디스코그래피나 내 라이브러리에 있다</summary>
    private static bool IsRedundantInstrumental(PoolArtist c, DzAlbum a, OwnedLibrary owned) =>
        AlbumVariants.InstrumentalBase(a.Title) is { } original
        && (c.Albums.Any(o => o.Id != a.Id && AlbumVariants.SameTitle(o.Title, original)) || owned.HasAlbum(c.Name, original));

    private async Task<(List<ReleaseRecommendation> Recs, bool HeardAny)> ScoreHitsAsync(
        List<(PoolArtist c, DzAlbum a)> hits, IReadOnlyList<long> toScore, OwnedLibrary owned,
        RecommendOptions options, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyDictionary<long, Region>? regionHints = null)
    {
        // 3) 소리 분석 (레이블·장르도 같이 온다)
        IReadOnlyDictionary<long, AlbumSound> sounds = new Dictionary<long, AlbumSound>();
        var detailed = toScore.ToHashSet();
        if (sound != null)
        {
            progress?.Report(L.F("미리듣기 소리 분석 준비 중… ({0}장)", toScore.Count));
            var coverOf = hits.DistinctBy(h => h.a.Id).ToDictionary(h => h.a.Id, h => h.a.Cover);
            try { sounds = await sound.ScoreAlbumsAsync(toScore, progress, ct, coverOf); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report(L.F("소리 분석을 건너뜀: {0}", ex.Message));
            }
        }
        // 소리 서비스 결과가 없으면 레이블·장르만 직접 조회 (캐시, 많을 때는 캐시에 있는 것만)
        var infos = await Task.WhenAll(hits.Select(async h =>
            sounds.TryGetValue(h.a.Id, out var s) ? new AlbumInfo(s.Label, s.Genre, s.With)
            : _albumInfo.TryGet(h.a.Id.ToString(), out var cached) ? cached
            : !detailed.Contains(h.a.Id) ? new AlbumInfo(null, null)
            : await _albumInfo.GetOrAddAsync(h.a.Id.ToString(), async () =>
            {
                var d = await deezer.AlbumAsync(h.a.Id, ct);
                return new AlbumInfo(d?.Label, d?.Genres?.Data?.FirstOrDefault()?.Name);
            })));
        _albumInfo.Save();

        // 지역: 플리 뮤지션은 플레이리스트 지역. 나머지는 이름 문자 → MusicBrainz 캐시 → (한도 안에서) MusicBrainz → 소리 추정 (확신할 때만)
        int lookups = 0;
        var regionOf = new Dictionary<long, Region>();
        foreach (var h in hits)
        {
            if (regionOf.ContainsKey(h.c.Id)) continue;
            if (h.c.Mine is { } m && SoundRegions.TryGetValue(m.Region, out var home)) { regionOf[h.c.Id] = home; continue; }
            if (regionHints != null && regionHints.TryGetValue(h.a.Id, out var hint)) { regionOf[h.c.Id] = hint; continue; }
            if (!regions.TryGetCached(h.c.Name, out var r) && lookups < options.MaxRegionLookups)
            {
                progress?.Report(L.F("아티스트 지역 확인 중… {0}", ++lookups));
                r = await regions.ResolveAsync(h.c.Name, ct);
            }
            if (r == Region.Unknown && sounds.TryGetValue(h.a.Id, out var snd) && snd.Region != null)
            {
                var (name, p) = snd.Region.MaxBy(kv => kv.Value);
                if (p >= 0.8 && SoundRegions.TryGetValue(name, out var guessed)) r = guessed;
            }
            regionOf[h.c.Id] = r;
        }
        if (lookups > 0) regions.Save();

        // 4) 점수 (추가 제안끼리 비교. 플리 뮤지션은 항상 앞)
        var graphZ = ZScores(hits.Select(h => Math.Log(1e-3 + h.c.Score)).ToArray());
        var heard = hits.Select(h => sounds.GetValueOrDefault(h.a.Id) is { Heard: true } s ? s : null).ToList();
        var tasteZ = ZScores(heard.Where(s => s != null).Select(s => s!.Taste!.Value).ToArray());
        // 저장함 앨범과 닮은 정도 (저장한 앨범이 있을 때만 온다)
        var favIdx = heard.Select((s, i) => (s, i)).Where(x => x.s?.Fav != null).Select(x => x.i).ToList();
        var favZ = ZScores([.. favIdx.Select(i => heard[i]!.Fav!.Value)]);
        var favZOf = favIdx.Select((i, j) => (i, z: favZ[j])).ToDictionary(x => x.i, x => x.z);
        // 별로 표시한 앨범과 닮은 정도 (닮을수록 뒤로)
        var disIdx = heard.Select((s, i) => (s, i)).Where(x => x.s?.Dis != null).Select(x => x.i).ToList();
        var disZ = ZScores([.. disIdx.Select(i => heard[i]!.Dis!.Value)]);
        var disZOf = disIdx.Select((i, j) => (i, z: disZ[j])).ToDictionary(x => x.i, x => x.z);
        int k = 0;
        var recs = new List<ReleaseRecommendation>();
        for (int i = 0; i < hits.Count; i++)
        {
            var (c, a) = hits[i];
            var snd = heard[i];
            // 플리 뮤지션: 일본 크레이트 플레이리스트면 그 크레이트, 한국 플레이리스트면 한국음악
            string? crate = c.Mine?.Style ?? (c.Mine?.Region == "한국" || c.Mine is null or { Region: "" } && regionOf[c.Id] == Region.Korea ? KoreanCrate : null);
            double? fit = null, taste = null;
            double score;
            if (snd != null)
            {
                var (best, p) = snd.Style!.MaxBy(kv => kv.Value);
                crate ??= best;
                fit = snd.Style!.TryGetValue(crate, out var pc) ? pc : null;
                taste = snd.Taste;
                score = tasteZ[k++] + 2 * (fit ?? p) + (c.Mine == null ? options.GraphWeight * graphZ[i] : 0)
                        + options.FavoriteWeight * favZOf.GetValueOrDefault(i)
                        - options.DislikeWeight * Math.Max(0, disZOf.GetValueOrDefault(i));
            }
            else score = -1 + (c.Mine == null ? options.GraphWeight * graphZ[i] : 0);  // 소리를 못 들은 앨범은 뒤로

            var info = infos[i];
            // 좋아하는 레이블이면 가산점
            var labelAff = RecipeSong.Core.LabelAffinity.Of(options.LabelAffinity, info.Label);
            score += options.LabelWeight * labelAff;
            // 피처링·참여: 수록곡에 좋아하는 아티스트가 있으면
            var withLiked = info.With?.FirstOrDefault(n => options.LikedArtists.Contains(ListeningAnalyzer.NormalizeArtist(n))
                && ListeningAnalyzer.NormalizeArtist(n) != ListeningAnalyzer.NormalizeArtist(c.Name));
            if (withLiked != null) score += options.WithLikedWeight;
            // 형식 취향
            score += options.TypeBonus.GetValueOrDefault(a.RecordType ?? "album");
            // 작은 아티스트 우대 (추가 제안만, 팬 수를 알 때)
            if (c.Mine == null && c.Fans > 0)
                score += options.SmallArtistWeight * (c.Fans < 5_000 ? 0.4 : c.Fans < 50_000 ? 0.2 : c.Fans > 1_000_000 ? -0.2 : 0);
            if (regionOf[c.Id] == Region.International && c.Mine?.Style == null)
            {
                // 해외는 Deezer 장르로 나눈다. 어울림 점은 일본 크레이트 기준이라 표시하지 않는다.
                var overseas = OverseasCrate(info.Genre, a.GenreId, crate);
                fit = overseas == "Jazz" && snd?.Style is { } st && st.TryGetValue("Jazz", out var pj) ? pj : null;
                crate = overseas;
            }
            recs.Add(new ReleaseRecommendation(
                c.Name, a.Title, a.RecordType ?? "album", a.Released!.Value, info.Label,
                a.Cover, a.Id, a.Link, info.Genre, regionOf[c.Id],
                crate, fit, taste, snd?.Near ?? [],
                c.Mine != null ? [c.Mine.Playlist] : c.Because,
                c.Mine != null || owned.HasArtist(c.Name), Math.Round(score, 3),
                c.Mine?.Playlist,
                ArtistId: c.Id,
                // 저장한 앨범과 특히 닮았을 때만 (상위 약 15%) 카드에 표시
                FavNear: favZOf.GetValueOrDefault(i) >= 1.0 ? snd?.FavNear : null,
                LikedLabel: labelAff >= 0.2 ? info.Label : null,
                WithLiked: withLiked,
                Fans: c.Fans));
        }
        return (recs, heard.Any(s => s != null));
    }

    /// <summary>
    /// 후보 아티스트의 지역을 미리 채운다 (MusicBrainz 초당 1회라 백그라운드로 천천히).
    /// 이미 판정된 아티스트는 건너뛰고, 결과는 영구 캐시된다.
    /// </summary>
    public async Task WarmRegionsAsync(CandidatePool pool, int maxArtists = 400,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var todo = pool.Artists.Where(a => a.Mine == null).OrderByDescending(a => a.Score).Take(maxArtists)
            .Where(a => !regions.TryGetCached(a.Name, out _)).ToList();
        for (int i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await regions.ResolveAsync(todo[i].Name, ct);
            if (i % 20 == 19) { regions.Save(); progress?.Report(L.F("아티스트 지역 확인 {0}/{1}", i + 1, todo.Count)); }
        }
        regions.Save();
    }

    /// <summary>앨범 전체 수록곡과 미리듣기 URL (재생 직전에 받는다). URL은 몇 시간 뒤 만료된다.</summary>
    public async Task<IReadOnlyList<AlbumTrack>> AlbumTracksAsync(long deezerAlbumId, CancellationToken ct = default) =>
        [.. (await deezer.AlbumTracksAsync(deezerAlbumId, 200, ct)).Select((t, i) =>
            new AlbumTrack(i + 1, t.Title, t.Artist?.Name, TimeSpan.FromSeconds(t.Duration), string.IsNullOrEmpty(t.Preview) ? null : t.Preview, t.Rank, t.Id))];

    private Task<List<DzArtist>> RelatedAsync(long id, int limit, CancellationToken ct) =>
        _related.GetOrAddAsync($"{id}:{limit}", async () => [.. await deezer.RelatedArtistsAsync(id, limit, ct)]);

    private async Task<long?> ArtistIdAsync(string name, CancellationToken ct) =>
        await _artistIds.GetOrAddAsync(ListeningAnalyzer.NormalizeArtist(name),
            async () => (await deezer.FindArtistAsync(name, ct))?.Id);

    private static double[] ZScores(double[] xs)
    {
        if (xs.Length == 0) return xs;
        double mean = xs.Average(), sd = Math.Sqrt(xs.Average(x => (x - mean) * (x - mean)));
        return [.. xs.Select(x => sd < 1e-9 ? 0 : (x - mean) / sd)];
    }

    private static double TypeWeight(string? type) => type switch
    {
        "album" => 1.0,
        "ep" => 0.85,
        "single" => 0.7,
        _ => 0.5
    };

    private void SaveCaches()
    {
        _artistIds.Save();
        _mineIds.Save();
        _related.Save();
        _discography.Save();
    }

    private static void Add(Dictionary<long, Candidate> map, long id, string name, double score, string because, int fans = 0)
    {
        if (!map.TryGetValue(id, out var c)) map[id] = c = new Candidate(id, name);
        c.Score += score;
        c.Because.Add(because);
        if (fans > 0) c.Fans = fans;
    }

    private sealed class Candidate(long id, string name)
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public double Score { get; set; }
        public int Fans { get; set; }
        public List<string> Because { get; } = [];
    }
}

/// <summary>이미 가진 아티스트·앨범 (추천에서 앨범은 제외, 아티스트는 '아는 아티스트'로 표시).</summary>
public sealed class OwnedLibrary
{
    private readonly HashSet<string> _artists;
    private readonly HashSet<string> _albums;

    public OwnedLibrary(IEnumerable<TrackRecord> tracks)
    {
        var list = tracks as IReadOnlyCollection<TrackRecord> ?? [.. tracks];
        _artists = ListeningAnalyzer.KnownArtists(list);
        _albums = [.. list.Select(t => Key(t.GroupArtist, t.Album))];
    }

    public bool HasArtist(string artist) => _artists.Contains(ListeningAnalyzer.NormalizeArtist(artist));
    public bool HasAlbum(string artist, string album) => _albums.Contains(Key(artist, album));

    private static string Key(string artist, string album) =>
        ListeningAnalyzer.NormalizeArtist(artist) + "|" + ListeningAnalyzer.NormalizeArtist(album);
}
