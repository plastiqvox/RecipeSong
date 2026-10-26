using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecipeSong.Core;
using RecipeSong.Core.Library;
using RecipeSong.Core.Providers;
using RecipeSong.Core.Ai;
using RecipeSong.Core.Sound;

namespace RecipeSong.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly (string Key, string Label, string? ConfigRegion, Region? Region)[] RegionKeys =
    [
        ("all", Labels.All, null, null),
        ("jp", "일본", "일본", Region.Japan),
        ("kr", "한국", "한국", Region.Korea),
        ("intl", "해외", "해외", Region.International),
        ("sea", "동남아", null, Region.SoutheastAsia),
    ];

    private readonly AppSettings _settings;
    private readonly LibraryService _library;
    private readonly DeezerClient _deezer;
    private readonly ReleaseRecommender _recommender;
    private readonly LibraryReleases _libraryReleases;
    private readonly ArtworkService _artwork;
    private readonly SoundService? _sound;
    private readonly CrateConfig _crates;
    private readonly Dictionary<string, CandidatePool> _pools = [];
    private readonly Dictionary<string, string> _poolFavVersion = [];
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private OwnedLibrary? _owned;
    private List<ReleaseItem> _all = [];
    private IReadOnlyList<string> _crateOrder = [];

    // ---- 추천 설정 (양 · 모험 정도 · 아는 아티스트 빼기) ----
    private readonly JsonCache<RecPrefs> _recPrefsStore = new("rec-prefs", TimeSpan.FromDays(36500));
    private RecPrefs _recPrefs = new();
    private RecommendOptions RecOptions => RecommendOptions.Create(_recPrefs.Amount, _recPrefs.Adventure, _recPrefs.ExcludeKnown);

    [RelayCommand]
    private async Task OpenRecommendSettingsAsync()
    {
        var win = new RecommendSettingsWindow(_recPrefs, _settings, _sound?.MlDirectory ?? _settings.MlDirectory ?? SoundService.FindMlDirectory())
            { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result == _recPrefs) return;
        bool poolChanged = win.Result.Amount != _recPrefs.Amount;
        _recPrefs = win.Result;
        _recPrefsStore.Set("prefs", _recPrefs);
        _recPrefsStore.Save();
        // 추천 양이 바뀌면 후보 아티스트부터 다시 모은다
        if (poolChanged)
        {
            await _poolLock.WaitAsync(_life.Token);
            try { _pools.Clear(); } finally { _poolLock.Release(); }
        }
        await LoadAsync();
    }

    // ---- 크레이트 순서 (칩 우클릭으로 바꾼다) ----
    private readonly JsonCache<List<string>> _prefs = new("prefs", TimeSpan.FromDays(36500));
    private List<string> _userOrder = [];

    /// <summary>내가 정한 순서 → 나머지는 기본 순서 (모델 크레이트 · 해외 · 한국음악)</summary>
    private IEnumerable<string> CrateOrder => _userOrder.Concat(_crateOrder).Distinct();

    /// <summary>드래그로 옮기기: key를 target 칩의 앞(after=false) 또는 뒤에 놓는다</summary>
    public void MoveCrateTo(string key, string target, bool after)
    {
        if (key == target) return;
        var order = CrateChips.Select(c => c.Key).Where(k => k != Labels.All && k != AiChip && k != Labels.Unsorted)
            .Concat(CrateOrder).Distinct().ToList();
        if (!order.Remove(key)) return;
        int t = order.IndexOf(target);
        if (t < 0) return;
        order.Insert(after ? t + 1 : t, key);
        _userOrder = order;
        _prefs.Set("crate-order", _userOrder);
        _prefs.Save();
        ApplyFilters();
        RebuildSaved();
    }

    /// <param name="where">first / left / right / last / reset</param>
    private void MoveCrate(string key, string where)
    {
        if (where == "reset") _userOrder = [];
        else
        {
            var order = CrateChips.Select(c => c.Key).Where(k => k != Labels.All && k != AiChip && k != Labels.Unsorted)
                .Concat(CrateOrder).Distinct().ToList();
            int i = order.IndexOf(key);
            if (i < 0) return;
            order.RemoveAt(i);
            int to = where switch { "first" => 0, "left" => Math.Max(0, i - 1), "right" => Math.Min(order.Count, i + 1), _ => order.Count };
            order.Insert(to, key);
            _userOrder = order;
        }
        _prefs.Set("crate-order", _userOrder);
        _prefs.Save();
        ApplyFilters();
        RebuildSaved();
    }
    private IReadOnlyList<PlaylistArtist>? _mine;
    private Task? _mineRefresh;
    [ObservableProperty] private string _syncText = "";
    private string _crate = Labels.All, _region = "all";
    /// <summary>고른 형식 (비어 있으면 전체). 기본: 앨범 + EP</summary>
    private readonly HashSet<string> _types = ["album", "ep"];
    private readonly SavedReleases _saved = new();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _library = new LibraryService(settings);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _deezer = new DeezerClient(http);
        var itunes = new ITunesSearchClient(new HttpClient(), settings.Country);
        _sound = SoundService.TryCreate(settings);
        _recommender = new ReleaseRecommender(_deezer, new RegionResolver(new HttpClient()), _sound);
        _libraryReleases = new LibraryReleases(_deezer);
        _artwork = new ArtworkService(_deezer, itunes);
        _crates = CrateConfig.Load(_sound?.MlDirectory ?? settings.MlDirectory ?? SoundService.FindMlDirectory() ?? ".");

        TypeChips = [.. new[] { ("all", Labels.All), ("album", "앨범"), ("ep", "EP"), ("single", "싱글") }
            .Select(t => new FilterChip(t.Item1, t.Item2, SelectType))];
        RegionChips = [.. RegionKeys.Select(r => new FilterChip(r.Key, r.Label, SelectRegion))];
        _selectedDate = LatestFriday(DateTime.Today);  // 신보는 금요일에 몰린다 → 가장 최근 금요일이 있는 주
        if (_prefs.TryGet("crate-order", out var savedOrder)) _userOrder = savedOrder;
        if (_recPrefsStore.TryGet("prefs", out var rp)) _recPrefs = rp;
        _navMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
        BuildCalendar();
        SyncChips();
        RebuildSaved();
        RefreshAiStatus();
        // AI를 쓸 때마다 사이드바 사용액을 갱신 (기록은 백그라운드 스레드에서 올 수 있다)
        _aiLedger.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshAiStatus);
        _ = CheckKeysAsync();
        Player.OpenNowRequested = () => _ = GoToNowPlayingAsync();
    }

    /// <summary>오늘 포함 가장 최근 금요일</summary>
    private static DateTime LatestFriday(DateTime today) =>
        today.AddDays(-(((int)today.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7));

    public PlayerViewModel Player { get; } = new();

    // ---- 기간 / 달력 --------------------------------------------------------

    [ObservableProperty] private PeriodKind _mode = PeriodKind.Week;
    [ObservableProperty] private DateTime _selectedDate;
    [ObservableProperty] private DateTime _navMonth;

    public ObservableCollection<DayCell> Days { get; } = [];
    public ObservableCollection<MonthCell> Months { get; } = [];
    public bool IsYearMode => Mode == PeriodKind.Year;
    public bool IsWeekMode { get => Mode == PeriodKind.Week; set { if (value) SetMode(PeriodKind.Week); } }
    public bool IsMonthMode { get => Mode == PeriodKind.Month; set { if (value) SetMode(PeriodKind.Month); } }
    public bool IsYearModeChecked { get => Mode == PeriodKind.Year; set { if (value) SetMode(PeriodKind.Year); } }
    public string NavLabel => IsYearMode ? $"{NavMonth.Year}년" : $"{NavMonth.Year}년 {NavMonth.Month}월";

    public Period CurrentPeriod => Periods.Create(Mode, SelectedDate);
    public string PeriodKicker => Mode switch
    {
        PeriodKind.Year => "YEAR",
        PeriodKind.Month => "MONTH",
        _ => $"WEEK {System.Globalization.ISOWeek.GetWeekOfYear(SelectedDate)}"
    };
    public string PeriodTitle => Mode switch
    {
        PeriodKind.Year => $"{SelectedDate.Year}년",
        PeriodKind.Month => $"{SelectedDate.Year}년 {SelectedDate.Month}월",
        _ => $"{System.Globalization.ISOWeek.GetYear(SelectedDate)}년 {System.Globalization.ISOWeek.GetWeekOfYear(SelectedDate)}주차"
    };
    public string PeriodRange
    {
        get
        {
            var p = CurrentPeriod;
            // 저장된 결과를 꺼낸 건지 방금 분석한 건지 탭과 상관없이 보이게
            var source = RecSavedNote.Length > 0 ? RecSavedNote : "내 레시피 기준으로 소리를 듣고 골랐어요";
            return $"{p.Start:yyyy.M.d} – {p.End.AddDays(-1):M.d} 발매 · {source}";
        }
    }

    private void SetMode(PeriodKind mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        OnPeriodChanged();
    }

    [RelayCommand]
    private void Prev() => Shift(-1);

    [RelayCommand]
    private void Next() => Shift(1);

    private void Shift(int dir)
    {
        if (Mode == PeriodKind.Year)
        {
            SelectedDate = SelectedDate.AddYears(dir);
            OnPeriodChanged();
        }
        else if (Mode == PeriodKind.Week)
        {
            SelectedDate = SelectedDate.AddDays(7 * dir);
            OnPeriodChanged();
        }
        else
        {
            SelectedDate = new DateTime(SelectedDate.Year, SelectedDate.Month, 1).AddMonths(dir);
            OnPeriodChanged();
        }
    }

    private void SelectDay(DateTime d)
    {
        SelectedDate = d;
        if (Mode == PeriodKind.Month) Mode = PeriodKind.Week;  // 월 보기에서 날짜를 누르면 그 주로
        OnPeriodChanged();
    }

    private void SelectMonth(int m)
    {
        SelectedDate = new DateTime(SelectedDate.Year, m, 1);
        Mode = PeriodKind.Month;
        OnPeriodChanged();
    }

    private void OnPeriodChanged()
    {
        NavMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);
        foreach (var n in new[] { nameof(CanPrecise), nameof(PreciseLabel), nameof(IsYearMode), nameof(IsWeekMode), nameof(IsMonthMode), nameof(IsYearModeChecked),
                                  nameof(NavLabel), nameof(PeriodKicker), nameof(PeriodTitle), nameof(PeriodRange) })
            OnPropertyChanged(n);
        BuildCalendar();
        _loadTask = LoadAsync();
    }

    /// <summary>마지막으로 시작한 불러오기 (재생 중 앨범으로 이동할 때 끝나기를 기다린다)</summary>
    private Task _loadTask = Task.CompletedTask;

    // ---- 재생 중인 앨범으로 이동: 그 주를 열고 수록곡을 펼쳐 그 카드로 스크롤 ----

    /// <summary>화면이 그 카드로 스크롤하도록 알린다 (MainWindow가 받는다)</summary>
    public event Action<ReleaseItem>? ScrollToRequested;

    private async Task GoToNowPlayingAsync()
    {
        if (Player.NowRelease is not { } playing) return;
        var rec = playing.Rec;
        int? trackNo = playing.Current?.Number;
        SelectedTab = 0;

        // 형식·지역 필터에 가려지지 않게
        if (_types.Count > 0 && !_types.Contains(rec.RecordType)) _types.Add(rec.RecordType);
        if (RegionKeys.First(r => r.Key == _region).Region is { } rg && rg != rec.Region) _region = "all";

        // 그 앨범이 나온 주로
        var date = rec.ReleaseDate.ToDateTime(TimeOnly.MinValue);
        if (Mode != PeriodKind.Week || !CurrentPeriod.Contains(date))
        {
            Mode = PeriodKind.Week;
            SelectedDate = date;
            OnPeriodChanged();
        }
        try { await _loadTask; } catch (OperationCanceledException) { }

        // 결과에 없으면 (저장함·라이브러리에서 틀었을 때 등) 카드를 하나 넣는다
        var target = _all.FirstOrDefault(i => i.Rec.DeezerAlbumId == rec.DeezerAlbumId);
        if (target == null)
        {
            target = MakeItem(rec);
            _all = [.. _all, target];
        }
        // 그 크레이트만 보이게 해서 6장 제한에 가려지지 않게
        _crate = target.CrateName;
        ApplyFilters();

        target.ShowTracks = true;
        await EnsureTracksAsync(target);
        // 재생 표시를 새 카드의 곡으로 옮긴다
        if (target.Tracks.FirstOrDefault(t => t.Number == trackNo) is { } track)
        {
            target.Current = track;
            Player.Rebind(track.SetPlaying, () => PlayNextTrack(target, track), target);
        }
        ScrollToRequested?.Invoke(target);
    }

    private void BuildCalendar()
    {
        var period = CurrentPeriod;
        Days.Clear();
        var first = NavMonth;
        int lead = ((int)first.DayOfWeek + 6) % 7;
        int total = (int)Math.Ceiling((lead + DateTime.DaysInMonth(first.Year, first.Month)) / 7.0) * 7;
        for (int i = 0; i < total; i++)
        {
            var d = first.AddDays(i - lead);
            Days.Add(new DayCell(d, d.Month == first.Month, SelectDay)
            {
                IsSelected = d == SelectedDate.Date,
                InRange = Mode != PeriodKind.Year && period.Contains(d),
            });
        }
        Months.Clear();
        for (int m = 1; m <= 12; m++)
            Months.Add(new MonthCell(m, SelectMonth) { IsSelected = m == SelectedDate.Month });
        UpdateHeat();
    }

    /// <summary>달력 색: 현재 결과에서 날짜별(연 보기는 월별) 추천 장수.</summary>
    private void UpdateHeat()
    {
        var byDay = _all.GroupBy(i => i.Rec.ReleaseDate).ToDictionary(g => g.Key, g => g.Count());
        int max = byDay.Count > 0 ? byDay.Values.Max() : 1;
        foreach (var d in Days)
            d.Heat = byDay.TryGetValue(DateOnly.FromDateTime(d.Date), out var n) ? Math.Clamp((int)Math.Ceiling(4.0 * n / max), 1, 4) : 0;
        var byMonth = _all.GroupBy(i => i.Rec.ReleaseDate.Month).ToDictionary(g => g.Key, g => g.Count());
        int maxM = byMonth.Count > 0 ? byMonth.Values.Max() : 1;
        foreach (var m in Months)
            m.Heat = byMonth.TryGetValue(m.Month, out var n) ? Math.Clamp((int)Math.Ceiling(4.0 * n / maxM), 1, 4) : 0;
        UpdateAiMarks();
    }

    /// <summary>
    /// 달력에 AI가 찾은 앨범 표시: 발매일(연 보기는 발매월)마다 모델별 점. 저장해 둔 모든 기간의 AI 결과를 본다.
    /// </summary>
    private void UpdateAiMarks()
    {
        // 같은 앨범이 여러 기간 키에 있으면 배지를 합친다
        var recs = _aiPicks.FreshValues.SelectMany(l => l).GroupBy(r => r.DeezerAlbumId)
            .Select(g => g.Aggregate(MergeAi)).ToList();
        (IReadOnlyList<AiBadge> Dots, string? Tip) Marks(IEnumerable<ReleaseRecommendation> rs)
        {
            var labels = rs.SelectMany(r => (r.AiBy ?? []).Select(b => (b, r))).ToList();
            // 모델 기록 전의 "Claude"는 그날 같은 커넥터의 모델 배지가 있으면 그쪽으로 합친다 (점 하나 = 모델 하나)
            labels = [.. labels.Select(x => !x.b.Contains(' ')
                && labels.FirstOrDefault(o => o.b.StartsWith(x.b + " ", StringComparison.Ordinal)).b is { } full ? (full, x.r) : x)];
            if (labels.Count == 0) return ([], null);
            var groups = labels.GroupBy(x => x.b).OrderBy(g => AiColors.Rank(g.Key)).ToList();
            return ([.. groups.Select(g => new AiBadge(g.Key))],
                "AI가 찾은 앨범\n" + string.Join("\n", groups.Select(g =>
                    $"{g.Key}: " + string.Join(", ", g.Select(x => $"{x.r.Artist} – {x.r.Album}").Distinct().Take(4)) + (g.Count() > 4 ? " …" : ""))));
        }
        var byDay = recs.ToLookup(r => r.ReleaseDate);
        var history = _recHistory.FreshValues.ToList();
        static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + Environment.NewLine + Environment.NewLine + b;
        foreach (var d in Days)
        {
            (d.AiDots, d.AiTip) = Marks(byDay[DateOnly.FromDateTime(d.Date)]);
            var h = history.Where(x => x.Kind == PeriodKind.Week && x.Start.Date <= d.Date && d.Date <= x.End.Date).MaxBy(x => x.At);
            d.Analyzed = h != null;
            d.Tip = Join(h is { } w ? $"이 주 분석함 · {w.At:M.d HH:mm} · 추천 {w.Count}장" : null, d.AiTip);
        }
        var byMonth = recs.Where(r => r.ReleaseDate.Year == SelectedDate.Year).ToLookup(r => r.ReleaseDate.Month);
        foreach (var m in Months)
        {
            (m.AiDots, m.AiTip) = Marks(byMonth[m.Month]);
            var h = history.Where(x => x.Kind == PeriodKind.Month && x.Start.Year == SelectedDate.Year && x.Start.Month == m.Month).MaxBy(x => x.At);
            m.Analyzed = h != null;
            m.Tip = Join(h is { } mo ? $"이 달 분석함 · {mo.At:M.d HH:mm} · 추천 {mo.Count}장" : null, m.AiTip);
        }
    }

    // ---- 필터 / 결과 --------------------------------------------------------

    public ObservableCollection<FilterChip> CrateChips { get; } = [];
    public ObservableCollection<FilterChip> TypeChips { get; }
    public ObservableCollection<FilterChip> RegionChips { get; }
    public ObservableCollection<CrateSection> Sections { get; } = [];
    public ObservableCollection<LibraryArtistGroup> LibraryGroups { get; } = [];

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private int _recCount;
    [ObservableProperty] private int _libraryCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _emptyText = "";

    public bool IsRecsTab { get => SelectedTab == 0; set { if (value) SelectedTab = 0; } }
    public bool IsLibraryTab { get => SelectedTab == 1; set { if (value) SelectedTab = 1; } }
    public bool IsSavedTab { get => SelectedTab == 2; set { if (value) SelectedTab = 2; } }

    // ---- AI 커넥터 -----------------------------------------------------------

    private readonly AiConnectorSettings _aiSettings = AiConnectorSettings.Load();
    // 돈 들여 찾은 결과라 만료 없이 보관 (기간별)
    private readonly JsonCache<List<ReleaseRecommendation>> _aiPicks = new("ai-picks", TimeSpan.FromDays(3650));
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private bool _aiReady;
    [ObservableProperty] private bool _aiBusy;
    [ObservableProperty] private string _aiText = "";

    /// <summary>AI 결과는 기간별로 모은다 (지역 칩은 화면 필터로만 쓴다)</summary>
    private static string AiKey(Period p) => p.Label;

    /// <summary>이 기간의 AI 결과. 예전 버전은 "기간|지역" 키로 저장했으니 그것도 합친다.</summary>
    private List<ReleaseRecommendation> AiPicksFor(Period p)
    {
        var all = new Dictionary<long, ReleaseRecommendation>();
        foreach (var key in RegionKeys.Select(r => $"{p.Label}|{r.Key}").Prepend(AiKey(p)))
            if (_aiPicks.TryGet(key, out var list))
                foreach (var r in list.Select(FixOldRegion))
                    all[r.DeezerAlbumId] = all.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
        return [.. all.Values];
    }

    /// <summary>
    /// 지역 버그(모르는 아티스트가 한국으로 저장됨) 때 저장한 AI 결과 바로잡기:
    /// 이름에 한글이 없는데 한국이면 해외로 보고 해외 크레이트(장르 기준)로 다시 나눈다.
    /// </summary>
    private static ReleaseRecommendation FixOldRegion(ReleaseRecommendation r)
    {
        if (r.Region != Region.Korea || RegionResolver.FromScript(r.Artist) == Region.Korea || r.FromPlaylist != null) return r;
        var crate = ReleaseRecommender.OverseasCrate(r.GenreName, null, r.Crate);
        return r with { Region = Region.International, Crate = crate, CrateFit = crate == "Jazz" ? r.CrateFit : null };
    }

    private readonly AiUsageLedger _aiLedger = new();

    private IReadOnlyList<IAiScout> Scouts => [new ClaudeScout(_aiSettings, _aiLedger), new GptScout(_aiSettings, _aiLedger)];

    /// <summary>
    /// 사이드바 카드: 커넥터마다 한 줄 — "Claude Sonnet 5.5 · 오늘 $0.12/$1 · 주 $0.40/$5 · 남은 약 $4.60"
    /// </summary>
    private void RefreshAiStatus()
    {
        var ready = Scouts.Where(s => s.IsReady).ToList();
        AiReady = ready.Count > 0;
        string Line(string name, string model)
        {
            if (ready.All(s => s.Name != name)) return $"{name} 키 필요";
            var b = _aiSettings.BudgetOf(name);
            string Of(double spent, double? limit) => AiUsageLedger.Money(spent) + (limit is { } l ? $"/{AiUsageLedger.Money(l)}" : "");
            var parts = new List<string>
            {
                model,
                $"오늘 {Of(_aiLedger.Spent(AiUsageLedger.TodayStart, name), b.Daily)}",
                $"주 {Of(_aiLedger.Spent(AiUsageLedger.WeekStart, name), b.Weekly)}",
            };
            if (b.Remaining(_aiLedger, name) is { } left) parts.Add($"남은 약 {AiUsageLedger.Money(Math.Max(0, left))}");
            if (b.CreditExpires is { } exp)
            {
                var days = (exp - DateTime.Today).Days;
                parts.Add(days < 0 ? "크레딧 만료됨?" : days <= 30 ? $"만료 {days}일 남음" : $"{exp:yy.M.d} 만료");
            }
            if (b.Blocked(_aiLedger, name) != null) parts.Add("한도 도달");
            // 키: 확인 결과(만료·삭제) > 곧 만료 경고 > 만료 예정일
            if (_keyProblems.TryGetValue(name, out var problem)) parts.Add(problem);
            else if (b.KeyExpiryWarning() is { } warn) parts.Add(warn);
            else if (b.KeyExpires is { } keyUntil) parts.Add($"키 {keyUntil:M.d}까지");
            return string.Join(" · ", parts);
        }
        var lines = new List<string>();
        if (_aiSettings.Enabled)
            lines.Add(Line("Claude", AiConnectorSettings.Models.FirstOrDefault(m => m.Id == _aiSettings.Model).Label?.Split(" (")[0] ?? _aiSettings.Model));
        if (_aiSettings.GptEnabled) lines.Add(Line("GPT", _aiSettings.GptModel));
        AiStatus = lines.Count == 0 ? "꺼짐" : string.Join("\n", lines);
    }

    private readonly Dictionary<string, string> _keyProblems = [];

    /// <summary>켜 둔 커넥터의 키가 아직 유효한지 확인 (모델 목록 조회라 요금 없음)</summary>
    private async Task CheckKeysAsync()
    {
        foreach (var s in Scouts.Where(s => s.IsReady))
        {
            var problem = await s.CheckKeyAsync(_life.Token);
            if (problem == null) _keyProblems.Remove(s.Name); else _keyProblems[s.Name] = problem;
        }
        RefreshAiStatus();
    }

    [RelayCommand]
    private void OpenConnector()
    {
        var win = new ConnectorWindow(_aiSettings, _aiLedger) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            RefreshAiStatus();
            _ = CheckKeysAsync();
        }
    }

    /// <summary>같은 앨범을 여러 커넥터가 고르면 배지와 이유를 합친다</summary>
    private static ReleaseRecommendation MergeAi(ReleaseRecommendation old, ReleaseRecommendation add)
    {
        var by = (old.AiBy ?? []).Concat(add.AiBy ?? []).Distinct().ToList();
        // 예전 기록의 "Claude"는 같은 앨범에 "Claude Sonnet 5.5" 같은 모델 배지가 있으면 뺀다
        by.RemoveAll(b => !b.Contains(' ') && by.Any(o => o.StartsWith(b + " ", StringComparison.Ordinal)));
        var reason = old.AiReason == null ? add.AiReason
            : add.AiReason == null || old.AiBy?.SequenceEqual(by) == true ? old.AiReason
            : $"{old.AiReason}  /  {add.AiReason}";
        return old with { AiBy = by, AiReason = reason };
    }

    /// <summary>
    /// 지금 보는 기간에서 켜 둔 AI 커넥터들이 동시에 웹으로 찾는다. 결과는 기간별로 저장해 두고 다음에 바로 꺼내 쓴다.
    /// mode: "add" = 저장된 결과에 더하기, "replace" = 저장된 결과를 지우고 새로 찾기
    /// </summary>
    [RelayCommand]
    private async Task FindWithAiAsync(string? mode)
    {
        if (AiBusy) return;
        bool replace = mode == "replace";
        if (!AiReady) { OpenConnector(); if (!AiReady) return; }
        var period = CurrentPeriod;
        var regionKey = _region;
        var focus = RegionKeys.First(r => r.Key == regionKey) is var rk && rk.Key != "all" ? rk.Label : null;
        // 일일·주간 한도나 충전액에 닿은 커넥터는 이번에 쉰다
        var blocked = Scouts.Where(s => s.IsReady)
            .Select(s => (s, why: _aiSettings.BudgetOf(s.Name).Blocked(_aiLedger, s.Name)))
            .Where(x => x.why != null).ToList();
        var scouts = Scouts.Where(s => s.IsReady && blocked.All(b => b.s.Name != s.Name)).ToList();
        if (scouts.Count == 0)
        {
            AiText = string.Join(" · ", blocked.Select(b => $"{b.s.Name}: {b.why}")) + " (설정에서 한도를 바꿀 수 있어요)";
            return;
        }
        var runStart = DateTime.Now;
        AiBusy = true;
        var status = new Dictionary<string, string>();
        void Report(string who, string m) { lock (status) status[who] = m; AiText = string.Join("  ·  ", status.Values); }
        var progress = new Progress<string>(m => AiText = m);
        try
        {
            var playlists = await _library.GetPlaylistsAsync(_life.Token);
            var tracks = await _library.GetTracksAsync(_life.Token);
            _mine ??= TasteProfile.PlaylistArtists(playlists, tracks, _crates);
            var taste = string.Join("\n", _mine.GroupBy(m => m.Playlist).Select(g =>
            {
                var e = _crates.Playlists[g.Key];
                var crate = e.Style ?? (e.Region == "한국" ? ReleaseRecommender.KoreanCrate : $"{e.Region} (장르는 Deezer 기준)");
                return $"- {g.Key} 플레이리스트 → 레시피 {crate}, 지역 {e.Region}: " +
                       string.Join(", ", g.OrderByDescending(m => m.Tracks).Take(10).Select(m => m.Artist));
            }));
            // 다시 찾기면 지난 AI 결과도 다시 나올 수 있게 제외 목록에서 뺀다
            var exclude = _all.Where(i => !(replace && i.IsAi)).Select(i => $"{i.Artist} – {i.Album}").ToList();

            // 커넥터마다 따로 돌리고, 하나가 실패해도 나머지 결과는 쓴다
            var runs = await Task.WhenAll(scouts.Select(async s =>
            {
                try
                {
                    var p = await s.FindAsync(period, taste, exclude, focus, new Progress<string>(m => Report(s.Name, m)), _life.Token);
                    Report(s.Name, $"{s.Name} {p.Count}장 찾음");
                    return (s.Name, picks: p, error: (string?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report(s.Name, $"{s.Name} 실패");
                    return (s.Name, picks: (IReadOnlyList<AiPick>)[], error: ex.Message);
                }
            }));
            var picks = runs.SelectMany(r => r.picks).ToList();
            _owned ??= new OwnedLibrary(tracks);
            var (recs, unverified) = picks.Count == 0 ? ([], 0)
                : await _recommender.ScoreAiPicksAsync(picks, _owned, period, progress, _life.Token);

            if (replace)
            {
                // 이 기간의 예전 결과(예전 "기간|지역" 키 포함)를 지운다
                foreach (var key in RegionKeys.Select(r => $"{period.Label}|{r.Key}")) _aiPicks.Set(key, []);
            }
            var merged = (replace ? [] : AiPicksFor(period)).ToDictionary(r => r.DeezerAlbumId);
            foreach (var r in recs) merged[r.DeezerAlbumId] = merged.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
            _aiPicks.Set(AiKey(period), [.. merged.Values]);
            _aiPicks.Save();
            UpdateAiMarks();

            // 그새 다른 기간으로 옮겼으면 저장만 하고 화면은 그대로
            if (CurrentPeriod.Label == period.Label && _region == regionKey)
            {
                if (replace)
                {
                    // 화면에서도 지난 AI 결과를 걷어 낸다: AI만 찾은 카드는 빼고, 다른 이유로 있던 카드는 배지만 뗀다
                    _all = [.. _all.Where(i => !(i.IsAi && i.Rec.BecauseOf.Contains("AI 제안")))
                        .Select(i => i.IsAi ? MakeItem(i.Rec with { AiBy = null, AiReason = null }) : i)];
                }
                var byId = recs.ToDictionary(r => r.DeezerAlbumId);
                // 이미 화면에 있는 앨범이면 배지만 더하고, 새 앨범은 추가
                _all = [.. _all.Select(i => byId.Remove(i.Rec.DeezerAlbumId, out var r) ? MakeItem(MergeAi(i.Rec, r)) : i),
                        .. byId.Values.Select(r => MakeItem(r))];
                ApplyFilters();
            }
            var summary = string.Join(" · ", runs.Select(r => r.error == null
                ? $"{r.Name} {recs.Count(x => x.AiBy?.Any(b => b.StartsWith(r.Name, StringComparison.Ordinal)) == true)}장"
                : $"{r.Name} 실패: {r.error}"));
            AiText = $"AI 제안 {summary}" + (unverified > 0 ? $" · Deezer에서 확인 못 한 {unverified}장은 뺐어요" : "")
                     + $" · 이번 검색 {AiUsageLedger.Money(_aiLedger.Spent(runStart))}"
                     + (blocked.Count > 0 ? " · " + string.Join(", ", blocked.Select(b => $"{b.s.Name} 쉼: {b.why}")) : "");
        }
        catch (OperationCanceledException) { AiText = ""; }
        catch (Exception ex) { AiText = $"AI로 찾지 못했어요: {ex.Message}"; }
        finally
        {
            AiBusy = false;
            UpdateAiSaved();
        }
    }

    // 이 기간에 저장된 AI 결과: "저장된 AI 제안 5장 · 10.27 06:36 검색"
    [ObservableProperty] private int _aiSavedCount;
    [ObservableProperty] private string _aiSavedNote = "";
    public bool HasAiSaved => AiSavedCount > 0;
    partial void OnAiSavedCountChanged(int value) => OnPropertyChanged(nameof(HasAiSaved));

    private void UpdateAiSaved()
    {
        var period = CurrentPeriod;
        var picks = AiPicksFor(period);
        AiSavedCount = picks.Count;
        DateTime? at = _aiPicks.TryGetAt(AiKey(period), out _, out var t) ? t : null;
        var by = picks.SelectMany(r => r.AiBy ?? []).Distinct().ToList();
        by.RemoveAll(b => !b.Contains(' ') && by.Any(o => o.StartsWith(b + " ", StringComparison.Ordinal)));  // 예전 "Claude" 표기
        AiSavedNote = picks.Count == 0 ? ""
            : $"저장된 AI 제안 {picks.Count}장" + (by.Count > 0 ? $" ({string.Join(", ", by)})" : "") + (at is { } a ? $" · {a:M.d HH:mm} 검색" : "");
    }

    // ---- 저장함 (나중에 디깅) -------------------------------------------------

    public ObservableCollection<CrateSection> SavedSections { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoSaved))] private int _savedCount;
    public bool NoSaved => SavedCount == 0;

    private ReleaseItem MakeItem(ReleaseRecommendation r, string? savedNote = null) =>
        new(r, PlayReleaseAsync, ToggleTracksAsync, ToggleSave, _saved.Contains(r.DeezerAlbumId)) { SavedNote = savedNote };

    private void ToggleSave(ReleaseItem item)
    {
        var id = item.Rec.DeezerAlbumId;
        bool save = !_saved.Contains(id);
        if (save) _saved.Add(item.Rec); else _saved.Remove(id);
        // 추천 탭과 저장함에 같은 앨범 카드가 따로 있으니 둘 다 맞춘다
        foreach (var i in _all.Where(i => i.Rec.DeezerAlbumId == id)) i.IsSaved = save;
        item.IsSaved = save;
        RebuildSaved();
        _ = SyncFavoritesAsync();
    }

    /// <summary>
    /// 즐겨찾기 학습: 저장함 앨범을 소리 분석 서비스에 알린다. 이후 신보는 "저장한 앨범과 닮은 정도"도 점수에 들어가고,
    /// 저장한 앨범의 아티스트는 비슷한 아티스트를 찾는 출발점(시드)이 된다 (다음 분석부터).
    /// </summary>
    private async Task SyncFavoritesAsync()
    {
        if (_sound == null) return;
        try
        {
            var favs = _saved.All.Select(s => (s.Rec.DeezerAlbumId, $"{s.Rec.Artist} – {s.Rec.Album}")).ToList();
            await _sound.SetFavoritesAsync(favs, _life.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }  // 서비스가 아직 안 떴으면 시작할 때 다시 보낸다
    }

    /// <summary>저장함이 바뀌면 추천 결과·후보 풀 저장 키가 바뀐다 (저장 앨범이 점수와 시드에 들어가니까)</summary>
    private string FavoritesVersion => _saved.All.Count == 0 ? "0"
        : $"{_saved.All.Count}-{_saved.All.Sum(s => s.Rec.DeezerAlbumId % 100_000):x}";

    private void RebuildSaved()
    {
        var all = _saved.All;
        SavedCount = all.Count;
        SavedSections.Clear();
        var items = all.Select(s => MakeItem(s.Rec, $"{s.SavedAt:M.d} 저장")).ToList();
        var order = CrateOrder.ToList();
        foreach (var g in items.GroupBy(i => i.CrateName)
                     .OrderBy(g => order.IndexOf(g.Key) is var k && k >= 0 ? k : 99))
        {
            var list = g.ToList();
            SavedSections.Add(new CrateSection(g.Key, list, list.Count, list.Count(i => i.IsMine), false, _ => { }));
        }
    }
    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsRecsTab));
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsSavedTab));
    }

    private void SelectCrate(string key)
    {
        _crate = key;
        ApplyFilters();
    }

    /// <summary>형식은 여러 개 고를 수 있다. '전체'를 누르거나 다 고르면/다 빼면 전체.</summary>
    private void SelectType(string key)
    {
        if (key == "all") _types.Clear();
        else if (!_types.Remove(key)) _types.Add(key);
        if (_types.Count == TypeChips.Count - 1) _types.Clear();
        ApplyFilters();
    }

    private void SelectRegion(string key)
    {
        if (_region == key) return;
        _region = key;
        ApplyFilters();   // 지금 결과를 바로 지역으로 거르고
        _ = LoadAsync();  // 그 지역 플레이리스트를 시드로 다시 찾는다
    }

    private void SyncChips()
    {
        foreach (var c in CrateChips) c.IsSelected = c.Key == _crate;
        foreach (var c in TypeChips) c.IsSelected = c.Key == "all" ? _types.Count == 0 : _types.Contains(c.Key);
        foreach (var c in RegionChips) c.IsSelected = c.Key == _region;
    }

    private IEnumerable<ReleaseItem> FilteredBase()
    {
        var region = RegionKeys.First(r => r.Key == _region).Region;
        // 지역을 고르면 그 지역 플레이리스트에서 출발한 후보만 오므로, 아직 지역을 모르는 아티스트도 함께 보여준다
        bool keepUnknown = _resultsRegion == _region;
        return _all.Where(i => (_types.Count == 0 || _types.Contains(i.Rec.RecordType))
            && (region == null || i.Rec.Region == region || keepUnknown && i.Rec.Region == Region.Unknown));
    }

    /// <summary>지금 _all이 어떤 지역 시드로 만든 결과인지</summary>
    private string _resultsRegion = "all";

    /// <summary>크레이트 칩 옆 "AI 제안" 칩: AI 커넥터가 찾은 것만 모아 본다</summary>
    private const string AiChip = "AI 제안";

    private void ApplyFilters()
    {
        var items = FilteredBase().ToList();
        int aiCount = items.Count(i => i.IsAi);

        // 크레이트 칩: 전체 · (AI 제안) · 모델의 크레이트 순서 · 미분류
        var names = CrateOrder.Concat(items.Select(i => i.CrateName)).Distinct().ToList();
        var keys = new List<string> { Labels.All };
        if (aiCount > 0) keys.Add(AiChip);
        keys.AddRange(names);
        if (!CrateChips.Select(c => c.Key).SequenceEqual(keys))
        {
            CrateChips.Clear();
            foreach (var k in keys)
                CrateChips.Add(new FilterChip(k, k, SelectCrate) { Mover = k == Labels.All || k == AiChip || k == Labels.Unsorted ? null : MoveCrate });
        }
        if (!keys.Contains(_crate)) _crate = Labels.All;
        foreach (var c in CrateChips)
            c.Count = c.Key == Labels.All ? items.Count : c.Key == AiChip ? aiCount : items.Count(i => i.CrateName == c.Key);
        SyncChips();

        bool aiOnly = _crate == AiChip;
        bool all = _crate == Labels.All;
        Sections.Clear();
        foreach (var g in items.Where(i => !aiOnly || i.IsAi).GroupBy(i => i.CrateName)
                     .OrderBy(g => g.Key == Labels.Unsorted ? 99 : names.IndexOf(g.Key)))
        {
            if (!all && !aiOnly && g.Key != _crate) continue;
            // 플리 뮤지션 → AI 제안 → 추가 제안 순
            var list = g.OrderBy(i => i.IsMine ? 0 : i.IsAi ? 1 : 2).ToList();
            int mine = list.Count(i => i.IsMine);
            var shown = list;
            if (all && list.Count > 6)
            {
                // 전체 보기: 6장 중 추가 제안(AI 포함)이 최소 2장은 보이게
                int sugg = Math.Min(2, list.Count - mine);
                shown = [.. list.Where(i => i.IsMine).Take(6 - sugg), .. list.Where(i => !i.IsMine)];
                shown = shown.Take(6).ToList();
            }
            Sections.Add(new CrateSection(g.Key, shown, list.Count, mine, all && list.Count > 6, SelectCrate));
        }
        RecCount = items.Count;
        EmptyText = IsBusy ? "" : items.Count == 0
            ? (_all.Count == 0 ? "이 기간에 찾은 발매작이 없어요." : "이 조건에 맞는 발매작이 없어요. 형식·지역 필터를 넓혀 보세요.")
            : "";
    }

    // ---- 불러오기 -----------------------------------------------------------

    public async Task InitializeAsync()
    {
        IsBusy = true;
        BusyText = "라이브러리 읽는 중…";
        var tracks = await _library.GetTracksAsync(_life.Token);
        _owned = new OwnedLibrary(tracks);
        WatchLibrary();
        _ = StartSoundAsync();
        await LoadAsync();
    }

    private async Task StartSoundAsync()
    {
        if (_sound == null)
        {
            ModelDevice = "꺼짐";
            ModelTitle = "소리 분석을 쓸 수 없음";
            ModelSub = "ml 폴더 · uv · 학습된 모델을 찾지 못했어요. 아티스트 연결로만 추천합니다.";
            return;
        }
        try
        {
            ModelSub = "소리 분석 서비스 시작 중…";
            await _sound.StartAsync(_life.Token);
            await SyncFavoritesAsync();
            await RefreshStatusAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ModelDevice = "오류";
            ModelSub = $"소리 분석 서비스를 시작하지 못했어요: {ex.Message}";
        }
    }

    private async Task<CandidatePool> PoolAsync(string? focus, IProgress<string> progress, CancellationToken ct)
    {
        var key = focus ?? "all";
        await _poolLock.WaitAsync(ct);
        try
        {
            if (_pools.TryGetValue(key, out var p) && DateTime.Now - p.BuiltAt < TimeSpan.FromHours(12)
                && _poolFavVersion.GetValueOrDefault(key) == FavoritesVersion) return p;
            var playlists = await _library.GetPlaylistsAsync(ct);
            var tracks = await _library.GetTracksAsync(ct);
            var seeds = TasteProfile.FromCrates(playlists, _crates, focusRegion: focus);
            // 저장함 아티스트도 출발점으로 (플레이리스트 시드와 겹치면 플레이리스트 쪽을 둔다)
            var savedSeeds = _saved.All.Select(s => s.Rec.Artist).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(a => seeds.All(x => !string.Equals(x.Artist, a, StringComparison.OrdinalIgnoreCase)))
                .Select(a => new TasteArtist(a, 0.9, 0, "저장함"));
            seeds = [.. seeds, .. savedSeeds];
            if (seeds.Count == 0) seeds = TasteProfile.Build(tracks);
            _mine ??= TasteProfile.PlaylistArtists(playlists, tracks, _crates);
            p = await _recommender.BuildPoolAsync(seeds, _mine, RecOptions, progress, ct);
            _pools[key] = p;
            _poolFavVersion[key] = FavoritesVersion;
            if (p.MinePending > 0 && _mineRefresh == null) _mineRefresh = RefreshMineAsync(_mine);
            // 지역은 MusicBrainz(초당 1회)라 뒤에서 천천히 채운다
            _ = Task.Run(() => _recommender.WarmRegionsAsync(p, ct: _life.Token));
            return p;
        }
        finally { _poolLock.Release(); }
    }

    /// <summary>플레이리스트 뮤지션 디스코그래피를 뒤에서 모으고, 끝나면 지금 보는 기간을 다시 불러온다.</summary>
    private async Task RefreshMineAsync(IReadOnlyList<PlaylistArtist> mine)
    {
        var progress = new Progress<string>(m => SyncText = $"내 플리 뮤지션 신보 모으는 중… {m.Replace("플레이리스트 뮤지션 ", "")}");
        try
        {
            SyncText = $"내 플리 뮤지션 {mine.Count:N0}명의 신보 모으는 중…";
            await Task.Run(() => _recommender.RefreshMineAsync(mine, progress, _life.Token));
            SyncText = "";
            await _poolLock.WaitAsync(_life.Token);
            try { _pools.Clear(); } finally { _poolLock.Release(); }
            if (!IsBusy) await LoadAsync(force: true);  // 새로 모은 플리 뮤지션 신보 반영
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SyncText = $"플리 뮤지션 신보를 모으지 못했어요: {ex.Message}"; }
        finally { _mineRefresh = null; }
    }

    // ---- 추천 결과 저장: 한 번 계산한 기간은 다음에 바로 꺼낸다 ----
    private readonly JsonCache<List<ReleaseRecommendation>> _recResults = new("rec-results", TimeSpan.FromDays(90));
    /// <summary>분석 이력 (달력 표시용): 기간마다 마지막 분석 시각과 추천 장수</summary>
    private readonly JsonCache<RecHistory> _recHistory = new("rec-history", TimeSpan.FromDays(3650));
    public sealed record RecHistory(PeriodKind Kind, DateTime Start, DateTime End, DateTime At, int Count);
    [ObservableProperty] private string _recSavedNote = "";
    public bool HasRecSaved => RecSavedNote.Length > 0;
    partial void OnRecSavedNoteChanged(string value)
    {
        OnPropertyChanged(nameof(HasRecSaved));
        OnPropertyChanged(nameof(PeriodRange));
    }

    /// <summary>기간 · 지역 · 추천 설정마다 따로 저장 (설정을 바꾸면 결과가 달라지니까)</summary>
    private string RecKey(Period period, string? focus) =>
        $"{period.Label}|{focus ?? "all"}|{_recPrefs.Amount}{_recPrefs.Adventure}{(_recPrefs.ExcludeKnown ? 1 : 0)}";

    /// <summary>
    /// 지난 기간은 저장 결과를 그대로 쓴다. 끝난 지 7일이 안 된 기간은 신보·디스코그래피가 계속 바뀌니 12시간만 쓴다.
    /// </summary>
    private bool TryGetSavedRecs(string key, Period period, out List<ReleaseRecommendation> recs, out DateTime at)
    {
        if (!_recResults.TryGetAt(key, out recs, out at)) return false;
        bool recent = period.End >= DateTime.Today.AddDays(-7);
        return !recent || DateTime.Now - at < TimeSpan.FromHours(12);
    }

    /// <summary>
    /// 주·월을 열 때 그 기간을 품은 월·연 결과가 저장돼 있으면 그 기간 발매작만 잘라 온다 (같은 지역·추천 설정일 때).
    /// 연·월 결과는 기간 전체에서 순위를 매긴 것이라, 그 주만 더 촘촘하게 보려면 "다시 분석".
    /// </summary>
    private bool TryGetWiderRecs(Period period, string? focus, out List<ReleaseRecommendation> recs, out DateTime at, out string label)
    {
        recs = [];
        at = default;
        label = "";
        var wider = period.Kind switch
        {
            PeriodKind.Week => new[] { Periods.Create(PeriodKind.Month, period.Start), Periods.Create(PeriodKind.Year, period.Start),
                                       Periods.Create(PeriodKind.Month, period.End), Periods.Create(PeriodKind.Year, period.End) },
            PeriodKind.Month => [Periods.Create(PeriodKind.Year, period.Start)],
            _ => [],
        };
        foreach (var w in wider.Distinct())
        {
            // 주가 두 달에 걸치면 한 달 결과로는 다 못 덮는다
            if (w.Start > period.Start || w.End < period.End) continue;
            if (!TryGetSavedRecs(RecKey(w, focus), w, out var all, out var t)) continue;
            recs = [.. all.Where(r => period.Contains(r.ReleaseDate))];
            at = t;
            label = w.Label;
            return true;
        }
        return false;
    }

    // ---- 정밀 분석: 연은 월 단위로, 월은 주 단위로 하나씩 분석해 저장 → 넓은 기간 보기는 그걸 합친다 ----
    public bool CanPrecise => Mode != PeriodKind.Week;
    public string PreciseLabel => Mode == PeriodKind.Year ? "연 정밀 분석" : "월 정밀 분석";
    [ObservableProperty] private bool _isPrecise;
    [ObservableProperty] private string _preciseText = "";
    private CancellationTokenSource? _preciseCts;

    /// <summary>정밀 분석의 조각: 연 → 1월~이번 달, 월 → 그 달에 걸친 주 (오늘 이후는 뺀다)</summary>
    private static List<Period> PartsOf(Period p)
    {
        if (p.Kind == PeriodKind.Year)
            return [.. Enumerable.Range(1, 12).Select(m => Periods.Create(PeriodKind.Month, new DateTime(p.Start.Year, m, 1)))
                .Where(x => x.Start <= DateTime.Today)];
        if (p.Kind == PeriodKind.Month)
        {
            var weeks = new List<Period>();
            for (var d = p.Start; d < p.End && d <= DateTime.Today; d = d.AddDays(7))
                weeks.Add(Periods.Create(PeriodKind.Week, d));
            var last = Periods.Create(PeriodKind.Week, p.End.AddDays(-1));
            if (last.Start <= DateTime.Today && weeks.All(w => w.Label != last.Label)) weeks.Add(last);
            return [.. weeks.DistinctBy(w => w.Label)];
        }
        return [];
    }

    /// <summary>모든 조각이 저장돼 있으면 합친 결과 (이 기간에 나온 것만)</summary>
    private bool TryGetPrecise(Period period, string? focus, out List<ReleaseRecommendation> recs, out DateTime latest)
    {
        recs = [];
        latest = default;
        var parts = PartsOf(period);
        if (parts.Count == 0) return false;
        var all = new List<ReleaseRecommendation>();
        foreach (var part in parts)
        {
            if (!TryGetSavedRecs(RecKey(part, focus), part, out var got, out var at)) return false;
            all.AddRange(got);
            if (at > latest) latest = at;
        }
        recs = [.. all.Where(r => period.Contains(r.ReleaseDate)).DistinctBy(r => r.DeezerAlbumId)
            .OrderByDescending(r => r.FromPlaylist != null).ThenByDescending(r => r.Score)];
        return true;
    }

    /// <summary>버튼을 다시 누르면 멈춘다 (끝낸 조각은 저장돼 있어 다음에 이어서)</summary>
    [RelayCommand]
    private async Task PreciseAsync()
    {
        if (IsPrecise) { _preciseCts?.Cancel(); return; }
        var period = CurrentPeriod;
        var regionKey = _region;
        var focus = RegionKeys.First(r => r.Key == regionKey).ConfigRegion;
        var parts = PartsOf(period);
        var unit = period.Kind == PeriodKind.Year ? "개월" : "주";
        var title = period.Kind == PeriodKind.Year ? "연 정밀 분석" : "월 정밀 분석";
        _preciseCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var ct = _preciseCts.Token;
        IsPrecise = true;
        int done = 0, skipped = 0;
        try
        {
            _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
            var pool = await PoolAsync(focus, new Progress<string>(m => PreciseText = $"{title} 준비 · {m}"), ct);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var key = RecKey(part, focus);
                if (TryGetSavedRecs(key, part, out _, out _)) { skipped++; continue; }
                var head = $"{title} {i + 1}/{parts.Count} · {part.Label}";
                PreciseText = head;
                var progress = new Progress<string>(msg => PreciseText = $"{head} · {msg}");
                var result = await _recommender.RecommendAsync(pool, _owned, part, RecOptions.ForPeriod(part.Kind), progress, ct);
                if (result.SoundScored && result.Releases.Count > 0)
                {
                    _recResults.Set(key, [.. result.Releases]);
                    _recHistory.Set(part.Label, new RecHistory(part.Kind, part.Start, part.End, DateTime.Now, result.Releases.Count));
                    _recResults.Save();
                    _recHistory.Save();
                }
                done++;
                UpdateAiMarks();
            }
            PreciseText = $"{title} 완료 · 새로 {done}{unit}" + (skipped > 0 ? $" (이미 있던 {skipped}{unit}은 건너뜀)" : "");
            // 지금 그 기간을 보고 있으면 합친 결과로 다시 그린다
            if (CurrentPeriod.Label == period.Label && _region == regionKey) await LoadAsync();
        }
        catch (OperationCanceledException) { PreciseText = $"{title} 멈춤 · {done}{unit} 저장됨 (다시 누르면 이어서)"; }
        catch (Exception ex) { PreciseText = $"{title} 실패: {ex.Message}"; }
        finally { IsPrecise = false; }
    }

    /// <summary>"다시 분석": 저장된 결과를 무시하고 지금 다시 계산</summary>
    [RelayCommand]
    private Task Reanalyze() => LoadAsync(force: true);

    private async Task LoadAsync(bool force = false)
    {
        _loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _loadCts = cts;
        var ct = cts.Token;
        var period = CurrentPeriod;
        var progress = new Progress<string>(m => { if (!ct.IsCancellationRequested) BusyText = m; });
        var regionKey = _region;
        var focus = RegionKeys.First(r => r.Key == regionKey).ConfigRegion;
        var key = RecKey(period, focus);
        var aiRecs = AiPicksFor(period);
        UpdateAiSaved();

        IsBusy = true;
        EmptyText = "";
        RecSavedNote = "";
        _ = LoadLibraryAsync(period, ct);

        // 저장된 AI 결과는 계산을 기다리지 않고 먼저 보여 준다
        if (aiRecs.Count > 0)
        {
            _all = [.. aiRecs.Select(r => MakeItem(r))];
            _resultsRegion = focus != null ? regionKey : "all";
            ApplyFilters();
        }
        try
        {
            List<ReleaseRecommendation> releases;
            bool soundScored = true;
            if (!force && period.Kind != PeriodKind.Week && TryGetPrecise(period, focus, out var precise, out var preciseAt))
            {
                releases = precise;
                RecSavedNote = $"{(period.Kind == PeriodKind.Year ? "월별" : "주별")} 정밀 분석 합침 · {preciseAt:M.d HH:mm}";
            }
            else if (!force && TryGetWiderRecs(period, focus, out var wider, out var widerAt, out var widerLabel)
                && !TryGetSavedRecs(key, period, out _, out _))
            {
                // 이 주(달)를 따로 분석한 적은 없지만 그걸 품은 달·연 결과가 저장돼 있다 → 잘라서 바로
                releases = wider;
                RecSavedNote = $"{widerLabel} 분석에서 가져옴 · {widerAt:M.d HH:mm}";
            }
            else if (!force && TryGetSavedRecs(key, period, out var saved, out var savedAt))
            {
                releases = saved;
                RecSavedNote = $"저장된 추천 · {savedAt:M.d HH:mm} 분석";
                // 이력 기능 전에 저장한 결과도 달력에 표시되게
                if (!_recHistory.TryGet(period.Label, out _))
                {
                    _recHistory.Set(period.Label, new RecHistory(period.Kind, period.Start, period.End, savedAt, saved.Count));
                    _recHistory.Save();
                }
            }
            else
            {
                _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
                var pool = await PoolAsync(focus, progress, ct);
                var result = await _recommender.RecommendAsync(pool, _owned, period, RecOptions.ForPeriod(period.Kind), progress, ct);
                if (ct.IsCancellationRequested) return;
                releases = [.. result.Releases];
                soundScored = result.SoundScored;
                // 소리 분석까지 된 결과만 저장 (소리 서비스가 늦게 뜬 경우는 다음에 다시 계산)
                if (soundScored && releases.Count > 0)
                {
                    _recResults.Set(key, releases);
                    _recHistory.Set(period.Label, new RecHistory(period.Kind, period.Start, period.End, DateTime.Now, releases.Count));
                    _recHistory.Save();
                    _recResults.Save();
                    RecSavedNote = $"방금 분석 · {DateTime.Now:M.d HH:mm}";
                }
            }
            if (ct.IsCancellationRequested) return;
            _all = [.. releases.Select(r => MakeItem(r))];
            // 이 기간에 AI가 찾아 둔 결과를 합친다 (다시 요청하지 않는다)
            if (aiRecs.Count > 0)
            {
                var byId = aiRecs.ToDictionary(r => r.DeezerAlbumId);
                _all = [.. _all.Select(i => byId.Remove(i.Rec.DeezerAlbumId, out var r) ? MakeItem(MergeAi(i.Rec, r)) : i),
                        .. byId.Values.Select(r => MakeItem(r))];
            }
            _resultsRegion = focus != null ? regionKey : "all";
            IsBusy = false;
            BusyText = soundScored ? "" : "소리 분석 없이 아티스트 연결로만 골랐어요";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _all = [];
            IsBusy = false;
            BusyText = $"추천을 불러오지 못했어요: {ex.Message}";
        }
        ApplyFilters();
        UpdateHeat();
    }

    private async Task LoadLibraryAsync(Period period, CancellationToken ct)
    {
        try
        {
            var tracks = await _library.GetTracksAsync(ct);
            var summary = await _libraryReleases.InPeriodAsync(tracks, period, ct: ct);
            if (ct.IsCancellationRequested) return;
            LibraryGroups.Clear();
            var items = new List<LibraryAlbumItem>();
            foreach (var a in summary.Artists.Take(60))
            {
                var albums = a.Albums.Select(al => new LibraryAlbumItem(al, PlayLibraryAsync)).ToList();
                items.AddRange(albums);
                LibraryGroups.Add(new LibraryArtistGroup(a.Artist, $"이 기간 발매 {albums.Count}장 · 재생 {a.PlayCount}회", albums));
            }
            LibraryCount = items.Count;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                item.ArtUrl = (await _artwork.GetAsync(item.Album, ct)).ArtworkUrl;
            }
            _artwork.Save();
        }
        catch (OperationCanceledException) { }
    }

    // ---- 재생 ---------------------------------------------------------------

    /// <summary>track이 null이면 커버 버튼: 이 앨범에서 듣던 곡을 이어서, 없으면 첫 곡부터.</summary>
    private async Task PlayReleaseAsync(ReleaseItem item, TrackItem? track)
    {
        var target = track ?? item.Current;
        if (target != null && Player.IsCurrent(target.SetPlaying))
        {
            Player.Toggle();
            return;
        }
        item.ShowTracks = true;
        if (!await EnsureTracksAsync(item)) return;
        target ??= item.Tracks.FirstOrDefault(t => t.HasPreview);
        if (target?.Preview is not { } url)
        {
            Player.Subtitle = $"{item.Album}: {(target == null ? "미리듣기가 없는 앨범이에요" : "이 곡은 미리듣기가 없어요")}";
            return;
        }
        item.Current = target;
        Player.Play(new Uri(url), $"{target.Title} — {target.Artist}",
            $"{item.Album} · {target.Number}/{item.Tracks.Count}번 곡 · Deezer 미리듣기 30초", item.CoverUrl, target.SetPlaying,
            ended: () => PlayNextTrack(item, target), release: item);
    }

    // 30초 미리듣기가 끝나면 같은 앨범의 다음 곡으로 (앨범 훑어보기)
    private void PlayNextTrack(ReleaseItem item, TrackItem done)
    {
        var next = item.Tracks.SkipWhile(t => t != done).Skip(1).FirstOrDefault(t => t.HasPreview);
        if (next != null) _ = PlayReleaseAsync(item, next);
    }

    private async Task ToggleTracksAsync(ReleaseItem item)
    {
        item.ShowTracks = !item.ShowTracks;
        if (item.ShowTracks) await EnsureTracksAsync(item);
    }

    /// <summary>수록곡을 받는다. 미리듣기 URL은 몇 시간 뒤 만료되므로 30분이 지났으면 새로 받는다.</summary>
    private async Task<bool> EnsureTracksAsync(ReleaseItem item)
    {
        if (item.Tracks.Count > 0 && DateTime.Now - item.TracksLoadedAt < TimeSpan.FromMinutes(30)) return true;
        bool first = item.Tracks.Count == 0;
        if (first) item.TracksStatus = "수록곡 불러오는 중…";
        try
        {
            var tracks = await _recommender.AlbumTracksAsync(item.Rec.DeezerAlbumId);
            item.SetTracks(tracks);
            item.TracksStatus = tracks.Count == 0 ? "수록곡 정보가 없어요" : null;
            return tracks.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            item.TracksStatus = "수록곡을 불러오지 못했어요";
            return !first;
        }
    }

    private Task PlayLibraryAsync(LibraryAlbumItem item)
    {
        if (item.Album.SampleTrackPath is not { } path) return Task.CompletedTask;
        if (Player.IsCurrent(item.SetPlaying)) { Player.Toggle(); return Task.CompletedTask; }
        Player.Play(new Uri(path), $"{item.Album.SampleTrackTitle} — {item.Album.Artist}", $"{item.Title} · 내 파일",
            item.ArtUrl, item.SetPlaying);
        return Task.CompletedTask;
    }

    // ---- 내 취향 모델 / 자동 재학습 -----------------------------------------

    [ObservableProperty] private string _modelTitle = "내 취향 모델 확인 중…";
    [ObservableProperty] private string _modelSub = "";
    [ObservableProperty] private string _modelDevice = "";
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _updateProgressText = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _autoUpdate = true;

    private async Task RefreshStatusAsync()
    {
        if (_sound == null) return;
        var s = await _sound.StatusAsync(_life.Token);
        _crateOrder = [.. s.Styles, .. ReleaseRecommender.OverseasCrates, ReleaseRecommender.KoreanCrate];
        RebuildSaved();
        ModelDevice = s.Gpu != null ? "GPU" : s.Device.ToUpperInvariant();
        ModelTitle = $"{s.Tracks:N0}곡 · 레시피 {s.Styles.Count}개";
        ModelSub = $"학습 {s.TrainedAt:yyyy.M.d} · 레시피 정확도 {s.StyleAccuracy:P0}";
        HasUpdate = s.NewTracks > 0;
        UpdateText = s.NewTracks > 0 ? $"iTunes에 새 곡 {s.NewTracks:N0}곡" : "";
        ApplyFilters();
        if (HasUpdate && AutoUpdate && !IsUpdating) await RunUpdateAsync();
    }

    [RelayCommand]
    private async Task RunUpdateAsync()
    {
        if (_sound == null || IsUpdating) return;
        IsUpdating = true;
        HasUpdate = false;
        var progress = new Progress<string>(m =>
        {
            UpdateProgressText = m;
            var mm = Regex.Match(m, @"(\d+)/(\d+)");
            UpdateProgress = mm.Success ? 100.0 * int.Parse(mm.Groups[1].Value) / Math.Max(1, int.Parse(mm.Groups[2].Value)) : 100;
        });
        try
        {
            var r = await _sound.UpdateAsync(progress, _life.Token);
            UpdateProgressText = r.Accepted
                ? $"새 곡 {r.Added}곡 반영 · 정확도 {r.OldStyleAccuracy:P0} → {r.NewStyleAccuracy:P0}"
                : $"새 모델이 더 나빠서({r.NewStyleAccuracy:P0}) 이전 모델 유지";
            _library.Invalidate();
            _owned = new OwnedLibrary(await _library.GetTracksAsync(_life.Token));
            _pools.Clear();  // 플레이리스트가 바뀌었을 수 있으니 시드부터 다시
            _mine = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateProgressText = $"업데이트 실패: {ex.Message}";
        }
        finally { IsUpdating = false; }
        await RefreshStatusAsync();
    }

    /// <summary>iTunes가 Library.xml을 다시 쓰면(곡 추가·플레이리스트 변경) 잠시 뒤 상태를 확인한다.</summary>
    private void WatchLibrary()
    {
        if (_settings.ITunesLibraryPath is not { } xml || !File.Exists(xml)) return;
        var debounce = new System.Timers.Timer(30_000) { AutoReset = false };
        debounce.Elapsed += (_, _) => Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            _library.Invalidate();
            await RefreshStatusAsync();
        });
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(xml)!, Path.GetFileName(xml))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => { debounce.Stop(); debounce.Start(); };
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        _watcher?.Dispose();
        if (_sound != null) await _sound.DisposeAsync();
    }
}
