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
    /// <summary>예전(지역 칩이 있던 때) AI 결과 저장 키의 지역 꼬리 — "기간|jp" 같은 키도 읽어 합친다</summary>
    private static readonly string[] OldRegionKeys = ["all", "jp", "kr", "intl", "sea"];

    private readonly AppSettings _settings;
    private readonly LibraryService _library;
    private readonly DeezerClient _deezer;
    private readonly ReleaseRecommender _recommender;
    private readonly LibraryReleases _libraryReleases;
    private readonly ArtworkService _artwork;
    private readonly SoundService? _sound;
    private readonly GenreSources _genres;

    /// <summary>장르 정보 키 (설정 창). 저장하면 다음 추천 계산부터 쓴다</summary>
    public GenreSourceSettings GenreSettings => _genres.Settings;
    public void SaveGenreSettings() => _genres.Settings.Save();
    private readonly Dictionary<string, CandidatePool> _pools = [];
    private readonly Dictionary<string, string> _poolFavVersion = [];
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private OwnedLibrary? _owned;
    private List<ReleaseItem> _all = [];

    // ---- 추천 설정 (양 · 모험 정도 · 아는 아티스트 빼기) ----
    private readonly JsonCache<RecPrefs> _recPrefsStore = new("rec-prefs", TimeSpan.FromDays(36500));
    private RecPrefs _recPrefs = new();
    private RecommendOptions RecOptions => RecommendOptions.Create(_recPrefs.Amount, _recPrefs.Adventure, _recPrefs.ExcludeKnown)
        with
        {
            DislikedArtists = DislikedArtists, LabelAffinity = _labelAffinity,
            BlockedArtists = _blocked.ArtistKeys, BlockedLabels = _blocked.LabelKeys, BlockedArtistIds = _blocked.ArtistIds,
            ExcludeInstrumental = _recPrefs.ExcludeInstrumental, HitsOnly = _recPrefs.Tracks == 1,
            LikedArtists = _likedArtists, TypeBonus = _typeBonus,
        };

    // ---- 좋아하는 아티스트 (피처링·참여 가산점) · 형식 취향 ----
    private HashSet<string> _likedArtists = [];
    private Dictionary<string, double> _typeBonus = [];

    /// <summary>
    /// 라이브러리에서: 곡 가중치 합 상위 300명 + 내 라이브러리 뮤지션 = 좋아하는 아티스트.
    /// 형식 취향: 앨범(7곡 이상)·EP(4~6곡)·싱글(1~3곡)로 묶어 가중치 몫을 보고, 많이 듣는 형식에 최대 ±0.3.
    /// </summary>
    private void BuildTasteExtras(IReadOnlyList<TrackRecord> tracks)
    {
        _likedArtists = [.. tracks.GroupBy(t => t.GroupArtist).OrderByDescending(g => g.Sum(t => t.Weight)).Take(300)
            .Select(g => ListeningAnalyzer.NormalizeArtist(g.Key))];
        if (_mine != null) _likedArtists.UnionWith(_mine.Select(m => ListeningAnalyzer.NormalizeArtist(m.Artist)));
        _likedArtists.UnionWith(_follows.All.Select(f => ListeningAnalyzer.NormalizeArtist(f.Name)));
        var share = tracks.Where(t => t.Album.Length > 0).GroupBy(t => (t.GroupArtist, t.Album))
            .Select(g => (Type: g.Count() >= 7 ? "album" : g.Count() >= 4 ? "ep" : "single", W: g.Sum(t => t.Weight)))
            .GroupBy(x => x.Type).ToDictionary(g => g.Key, g => g.Sum(x => x.W));
        double total = share.Values.Sum();
        if (total <= 0) return;
        _typeBonus = new[] { "album", "ep", "single" }
            .ToDictionary(t => t, t => Math.Round(Math.Clamp(0.9 * (share.GetValueOrDefault(t) / total - 1.0 / 3), -0.3, 0.3), 3));
    }

    // ---- 앱에서 들은 행동: 끝까지 들은 앨범은 은근한 좋아요, 몇 초 만에 넘긴 앨범은 은근한 별로 ----
    private readonly JsonCache<ListenStat> _listens = new("listen-log", TimeSpan.FromDays(36500));
    public sealed record ListenStat(int Completed, int Skipped, string Name);

    private void OnListened(ReleaseItem item, double seconds, bool completed)
    {
        var key = item.Rec.DeezerAlbumId.ToString();
        var s = _listens.TryGet(key, out var old) ? old : new ListenStat(0, 0, $"{item.Artist} – {item.Album}");
        if (completed) s = s with { Completed = s.Completed + 1 };
        else if (seconds < 8) s = s with { Skipped = s.Skipped + 1 };
        else return;  // 중간까지 들은 건 신호로 보지 않는다
        _listens.Set(key, s);
        _listens.Save();
        _ = SyncFavoritesAsync();
    }

    /// <summary>은근한 좋아요: 미리듣기를 끝까지 2곡 이상, 넘긴 것보다 훨씬 많이 / 은근한 별로: 2번 이상 바로 넘기고 끝까지 들은 적 없음</summary>
    private IEnumerable<(long Id, string Name)> ImplicitLikes() =>
        _listens.FreshValuesWithKeys().Where(x => x.Value.Completed >= 2 && x.Value.Completed >= 2 * x.Value.Skipped)
            .Select(x => (long.Parse(x.Key), x.Value.Name));

    private IEnumerable<long> ImplicitSkips() =>
        _listens.FreshValuesWithKeys().Where(x => x.Value.Skipped >= 2 && x.Value.Completed == 0).Select(x => long.Parse(x.Key));

    // ---- 팔로우: 신보는 라이브러리 뮤지션처럼 항상 앞에, 비슷한 아티스트 찾기의 출발점 (Deezer 아티스트 id로) ----
    private readonly FollowList _follows = new();
    public FollowList Follows => _follows;

    private bool IsFollowedRec(ReleaseRecommendation r) => r.ArtistId != 0
        ? _follows.Contains(r.ArtistId)
        : _follows.All.Any(f => ListeningAnalyzer.NormalizeArtist(f.Name) == ListeningAnalyzer.NormalizeArtist(r.Artist));

    private async Task ToggleFollowAsync(ReleaseItem item)
    {
        var id = item.Rec.ArtistId;
        // 예전에 저장한 추천에는 아티스트 id가 없다 → 앨범에서 찾는다
        if (id == 0)
        {
            try { id = (await _deezer.AlbumAsync(item.Rec.DeezerAlbumId, _life.Token))?.Artist?.Id ?? 0; }
            catch (HttpRequestException) { }
        }
        if (id == 0) { SyncText = L.F("{0}을(를) Deezer에서 찾지 못해 팔로우하지 못했어요", item.Artist); return; }
        bool follow = !_follows.Contains(id);
        if (follow)
        {
            _follows.Follow(id, item.Artist, "");
            _likedArtists.Add(ListeningAnalyzer.NormalizeArtist(item.Artist));
        }
        else _follows.Unfollow(id);
        await RefreshFollowsAsync();
    }

    /// <summary>팔로우가 바뀐 뒤 (카드 ☆ 또는 추천 설정에서 해제): 후보 풀을 새로 모으고, 지금 기간에 팔로우 신보를 더하거나 뺀다</summary>
    public async Task RefreshFollowsAsync()
    {
        await _poolLock.WaitAsync(_life.Token);
        try { _pools.Clear(); } finally { _poolLock.Release(); }
        // 해제한 아티스트: 팔로우 때문에만 들어온 카드는 뺀다
        _all = [.. _all.Where(i => i.Rec.FromPlaylist != FollowList.Tag || IsFollowedRec(i.Rec))];
        // 저장함·관심 없음 탭 카드와 오른쪽 창도 (따로 만든 카드라 _all 만 바꾸면 별이 그대로였다)
        foreach (var i in _all.Concat(SavedSections.SelectMany(s => s.Items)).Concat(DislikedItems).Append(Detail).OfType<ReleaseItem>())
            i.IsFollowed = IsFollowedRec(i.Rec);
        await MergeFollowedAsync(CurrentPeriod, _loadCts?.Token ?? _life.Token);
        ApplyFilters();
        MarkSeen();
        UpdateAiMarks();
    }

    /// <summary>팔로우한 아티스트의 기간 내 발매작을 결과에 더한다 (저장된 추천에도). 이미 있는 앨범은 팔로우 표시만 붙인다.</summary>
    private async Task MergeFollowedAsync(Period period, CancellationToken ct)
    {
        if (_follows.All.Count == 0) return;
        try
        {
            _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
            var recs = await Task.Run(() => _recommender.FollowedAsync(_follows.AsPlaylistArtists(), _owned, period, RecOptions.ForPeriod(period.Kind), ct: ct), ct);
            if (ct.IsCancellationRequested || CurrentPeriod.Label != period.Label) return;
            var byId = recs.ToDictionary(r => r.DeezerAlbumId);
            _all = [.. _all.Select(i => byId.Remove(i.Rec.DeezerAlbumId) ? MakeItem(i.Rec with { FromPlaylist = i.Rec.FromPlaylist ?? FollowList.Tag }) : i),
                    .. byId.Values.Select(r => MakeItem(r))];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { }
    }

    // ---- 차단: 카드 우클릭으로 아티스트·레이블 전체를 뺀다 (추천 설정에서 해제) ----
    private readonly BlockList _blocked = new();
    public BlockList Blocked => _blocked;

    /// <summary>카드 우클릭 → 차단 사유 창 (여러 개). '취향이 아니에요'가 있으면 그 앨범을 '별로'로 학습한다.</summary>
    public void Block(ReleaseItem item, bool label)
    {
        if (label && item.Rec.Label is not { Length: > 0 }) return;
        var win = new BlockWindow(label ? item.Rec.Label! : item.Artist, $"{item.Artist} – {item.Album}", label)
            { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        var reason = win.Reasons;
        var before = _blocked.Snapshot();
        KeepPlace(item, () =>
        {
            if (label) _blocked.BlockLabel(item.Rec, reason);
            else _blocked.BlockArtist(item.Rec, reason);
            ApplyFilters();
            RebuildSaved();
        });
        _ = SyncFavoritesAsync();
        if (reason.Learns()) _ = OfferSimilarAsync(item);
        OfferUndo(L.F("'{0}' 차단했어요", label ? item.Rec.Label! : item.Artist), () =>
        {
            _blocked.Restore(before);
            DropSimilarOfferFrom(item);
            RefreshBlocked();
        });
    }

    // ---- 되돌리기: 별로·차단 직후 잠깐 (카드가 바로 사라져서 잘못 눌렀을 때 찾아가기 어렵다) ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUndo))] private string _undoText = "";
    public bool HasUndo => UndoText.Length > 0;
    private Action? _undo;
    private int _undoVersion;

    private async void OfferUndo(string text, Action undo)
    {
        _undo = undo;
        UndoText = text;
        int v = ++_undoVersion;
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (v != _undoVersion) return;  // 그 사이 다른 되돌리기가 생겼다
        _undo = null;
        UndoText = "";
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        _undo = null;
        UndoText = "";
        _undoVersion++;
        undo?.Invoke();
    }

    /// <summary>되돌린 앨범 때문에 띄운 '닮은 추천 숨기기' 배너는 거둔다</summary>
    private void DropSimilarOfferFrom(ReleaseItem item)
    {
        if (_similarFrom != item.Rec.DeezerAlbumId) return;
        _similarFrom = null;
        _similarOffer = [];
        SimilarOfferText = "";
    }

    /// <summary>추천 설정 창에서 웹진을 켜고 끈 뒤</summary>
    public void SetDisabledWebzines(IEnumerable<string> names)
    {
        DisabledWebzines = [.. names];
        _prefs.Set("webzines-off", [.. DisabledWebzines]);
        _prefs.Save();
    }

    /// <summary>추천 설정 창에서 해제한 뒤 화면을 다시 거른다</summary>
    public void RefreshBlocked() { ApplyFilters(); RebuildSaved(); _ = SyncFavoritesAsync(); }

    /// <summary>좋아하는 레이블 (많이 듣고 좋아한 앨범들의 레이블). 시작할 때 캐시로 바로, 빠진 건 뒤에서 Deezer로 채운다.</summary>
    private IReadOnlyDictionary<string, double> _labelAffinity = new Dictionary<string, double>();

    private async Task BuildLabelAffinityAsync(IReadOnlyList<TrackRecord> tracks)
    {
        var affinity = new LabelAffinity(_deezer);
        _labelAffinity = affinity.FromCache(tracks);
        try
        {
            // 화면 문구는 언어를 따르니 문자열 비교 대신 누가 썼는지로 판단한다
            string? lastLabelText = null;
            var progress = new Progress<string>(m => { if (!_mineSyncText) SyncText = lastLabelText = m; });
            _labelAffinity = await Task.Run(() => affinity.BuildAsync(tracks, progress: progress, ct: _life.Token));
            if (lastLabelText != null && SyncText == lastLabelText) SyncText = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { SyncText = ""; }
    }

    /// <summary>톱니 버튼: 언어 · iTunes 라이브러리 · 저장 위치와 용량</summary>
    [RelayCommand]
    private void OpenSettings() =>
        new SettingsWindow(_settings, _sound?.MlDirectory ?? _settings.MlDirectory ?? SoundService.FindMlDirectory(), this)
            { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private async Task OpenRecommendSettingsAsync()
    {
        var win = new RecommendSettingsWindow(_recPrefs, _blocked, this)
            { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result == _recPrefs) return;
        RefreshBlocked();  // 창에서 차단을 해제했을 수 있다
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

    /// <summary>내가 정한 순서 → 나머지는 기본 순서 (장르 칸)</summary>
    private IEnumerable<string> CrateOrder => _userOrder.Concat(GenreGroups.All).Distinct();

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
    private string _crate = Labels.All;
    /// <summary>고른 형식 (비어 있으면 전체). 기본: 앨범 + EP</summary>
    private readonly HashSet<string> _types = ["album", "ep"];
    private readonly SavedReleases _saved = new();
    /// <summary>좋아요: 앨범 하트(liked-albums.json)와 곡 하트(liked-tracks.json). 취향 학습에 쓴다.</summary>
    private readonly SavedReleases _likedAlbums = new(Path.Combine(JsonCache<object>.Dir, "liked-albums.json"));
    private readonly LikedTracks _likedTracks = new();
    /// <summary>별로 표시한 앨범: 추천에서 숨기고, 닮은 소리는 점수를 깎고, 그 아티스트는 추가 제안에서 뺀다</summary>
    private readonly SavedReleases _disliked = new(Path.Combine(JsonCache<object>.Dir, "disliked.json"));

    /// <summary>별로 표시한 앨범의 아티스트 (내 라이브러리 뮤지션은 빼지 않는다)</summary>
    private HashSet<string> DislikedArtists =>
        [.. _disliked.All.Where(d => d.Rec.FromPlaylist == null).Select(d => ListeningAnalyzer.NormalizeArtist(d.Rec.Artist))];

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        AutoUpdate = settings.AutoRetrain;
        _library = new LibraryService(settings);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _deezer = new DeezerClient(http);
        var itunes = new ITunesSearchClient(new HttpClient(), settings.Country);
        _itunes = itunes;
        _sound = SoundService.TryCreate(settings);
        _genres = new GenreSources(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, GenreSourceSettings.Load());
        _recommender = new ReleaseRecommender(_deezer, _genres, _sound);
        _libraryReleases = new LibraryReleases(_deezer);
        _artwork = new ArtworkService(_deezer, itunes);

        TypeChips = [.. new[] { ("all", L.T(Labels.All)), ("album", L.T("앨범")), ("ep", "EP"), ("single", L.T("싱글")) }
            .Select(t => new FilterChip(t.Item1, t.Item2, SelectType))];
        _selectedDate = LatestFriday(DateTime.Today);  // 신보는 금요일에 몰린다 → 가장 최근 금요일이 있는 주
        // 레시피 칸을 쓰던 때 저장한 순서는 장르 칸 이름만 남긴다 (없는 칸이 빈 칩으로 남지 않게)
        if (_prefs.TryGet("crate-order", out var savedOrder)) _userOrder = [.. savedOrder.Where(GenreGroups.All.Contains)];
        if (_recPrefsStore.TryGet("prefs", out var rp)) _recPrefs = rp;
        if (_prefs.TryGet("webzines-off", out var off)) DisabledWebzines = [.. off];
        _navMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
        BuildCalendar();
        SyncChips();
        RebuildSaved();
        RefreshAiStatus();
        // AI를 쓸 때마다 사이드바 사용액을 갱신 (기록은 백그라운드 스레드에서 올 수 있다)
        _aiLedger.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshAiStatus);
        _ = CheckKeysAsync();
        Player.OpenNowRequested = () => _ = GoToNowPlayingAsync();
        Player.Listened += OnListened;
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
    public string NavLabel => IsYearMode ? L.F("{0}년", NavMonth.Year) : L.F("{0}년 {1}월", NavMonth.Year, NavMonth.Month, NavMonth);

    public Period CurrentPeriod => Periods.Create(Mode, SelectedDate);
    public string PeriodTitle => Mode switch
    {
        PeriodKind.Year => L.F("{0}년", SelectedDate.Year),
        PeriodKind.Month => L.F("{0}년 {1}월", SelectedDate.Year, SelectedDate.Month, SelectedDate),
        _ => L.F("{0}년 {1}주차", System.Globalization.ISOWeek.GetYear(SelectedDate), System.Globalization.ISOWeek.GetWeekOfYear(SelectedDate))
    };
    public string PeriodRange
    {
        get
        {
            var p = CurrentPeriod;
            // 저장된 결과를 꺼낸 건지 방금 분석한 건지 탭과 상관없이 보이게
            var source = RecSavedNote.Length > 0 ? RecSavedNote
                : IsTrained ? L.T("내 라이브러리와 소리가 닮은 것을 골랐어요") : L.T("아직 소리 분석 전이라 아티스트 연결로만 골랐어요");
            return L.F("{0:yyyy.M.d} – {1:M.d} 발매 · {2}", p.Start, p.End.AddDays(-1), source);
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
        _pageSize = PageStep;
        ScrollTopRequested?.Invoke();
        NavMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);
        foreach (var n in new[] { nameof(CanPrecise), nameof(PreciseLabel), nameof(IsYearMode), nameof(IsWeekMode), nameof(IsMonthMode), nameof(IsYearModeChecked), nameof(CanAiWeekly),
                                  nameof(NavLabel), nameof(PeriodTitle), nameof(PeriodRange) })
            OnPropertyChanged(n);
        BuildCalendar();
        _loadTask = LoadAsync();
    }

    /// <summary>마지막으로 시작한 불러오기 (재생 중 앨범으로 이동할 때 끝나기를 기다린다)</summary>
    private Task _loadTask = Task.CompletedTask;

    // ---- 재생 중인 앨범으로 이동: 그 주를 열고 수록곡을 펼쳐 그 카드로 스크롤 ----

    /// <summary>화면이 그 카드로 스크롤하도록 알린다 (MainWindow가 받는다)</summary>
    public event Action<ReleaseItem>? ScrollToRequested;
    /// <summary>기간·탭·레시피를 바꾸면 목록을 맨 위로 (헤더가 고정이라 앞 화면의 스크롤 위치가 남으면 바뀐 줄 모른다)</summary>
    public event Action? ScrollTopRequested;

    private async Task GoToNowPlayingAsync()
    {
        if (Player.NowRelease is not { } playing) return;
        var rec = playing.Rec;
        int? trackNo = playing.Current?.Number;
        SelectedTab = 0;

        // 형식 필터에 가려지지 않게
        if (_types.Count > 0 && !_types.Contains(rec.RecordType)) _types.Add(rec.RecordType);

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
        // 그 크레이트만 보이게 해서 6장 제한에 가려지지 않게 (Detail 을 먼저 정해 페이지에서도 잘리지 않게)
        _crate = target.CrateName;
        Detail = target;
        ApplyFilters();
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
        var recs = _aiPicks.FreshValues.SelectMany(l => l).Concat(AllWebzinePicks()).GroupBy(r => r.DeezerAlbumId)
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
                L.T("AI가 찾은 앨범") + "\n" + string.Join("\n", groups.Select(g =>
                    $"{g.Key}: " + string.Join(", ", g.Select(x => $"{x.r.Artist} – {x.r.Album}").Distinct().Take(4)) + (g.Count() > 4 ? " …" : ""))));
        }
        var byDay = recs.ToLookup(r => r.ReleaseDate);
        var history = _recHistory.FreshValues.ToList();
        static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + Environment.NewLine + Environment.NewLine + b;
        // 처음 보는 앨범: AI·웹진·정밀 분석이 화면에 나오기 전에 찾아 둔 것
        var seen = Seen;
        var unseen = recs.Concat(_recResults.FreshValues.SelectMany(l => l)).Where(r => !seen.Contains(r.DeezerAlbumId))
            .DistinctBy(r => r.DeezerAlbumId).ToList();
        var newByDay = unseen.ToLookup(r => r.ReleaseDate);
        static string? NewTip(int n) => n > 0 ? L.F("처음 보는 앨범 {0}장", n) : null;
        var followed = _follows.All.SelectMany(f => _recommender.CachedDiscography(f.Id)
                .Where(a => a.Released != null && a.RecordType != "compile").Select(a => (f.Name, a.Title, Date: a.Released!.Value)))
            .Distinct().ToList();
        var followByDay = followed.ToLookup(x => x.Date);
        static string? FollowTip(IEnumerable<(string Name, string Title, DateOnly Date)> xs) =>
            xs.Select(x => $"{x.Name} – {x.Title}").ToList() is { Count: > 0 } l
                ? L.T("팔로우한 아티스트 발매작") + "\n" + string.Join("\n", l.Take(4)) + (l.Count > 4 ? " …" : "") : null;
        foreach (var d in Days)
        {
            (d.AiDots, d.AiTip) = Marks(byDay[DateOnly.FromDateTime(d.Date)]);
            var h = history.Where(x => x.Kind == PeriodKind.Week && x.Start.Date <= d.Date && d.Date <= x.End.Date).MaxBy(x => x.At);
            d.Analyzed = h != null;
            d.NewCount = newByDay[DateOnly.FromDateTime(d.Date)].Count();
            var fol = followByDay[DateOnly.FromDateTime(d.Date)].ToList();
            d.FollowCount = fol.Count;
            d.Tip = Join(Join(h is { } w ? L.F("이 주 분석함 · {0:M.d HH:mm} · 추천 {1}장", w.At, w.Count) : null, NewTip(d.NewCount)),
                Join(FollowTip(fol), d.AiTip));
        }
        var byMonth = recs.Where(r => r.ReleaseDate.Year == SelectedDate.Year).ToLookup(r => r.ReleaseDate.Month);
        foreach (var m in Months)
        {
            (m.AiDots, m.AiTip) = Marks(byMonth[m.Month]);
            var h = history.Where(x => x.Kind == PeriodKind.Month && x.Start.Year == SelectedDate.Year && x.Start.Month == m.Month).MaxBy(x => x.At);
            m.Analyzed = h != null;
            m.NewCount = unseen.Count(r => r.ReleaseDate.Year == SelectedDate.Year && r.ReleaseDate.Month == m.Month);
            var folM = followed.Where(x => x.Date.Year == SelectedDate.Year && x.Date.Month == m.Month).ToList();
            m.FollowCount = folM.Count;
            m.Tip = Join(Join(h is { } mo ? L.F("이 달 분석함 · {0:M.d HH:mm} · 추천 {1}장", mo.At, mo.Count) : null, NewTip(m.NewCount)),
                Join(FollowTip(folM), m.AiTip));
        }
    }

    // ---- 필터 / 결과 --------------------------------------------------------

    public ObservableCollection<FilterChip> CrateChips { get; } = [];
    public ObservableCollection<FilterChip> TypeChips { get; }
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
    /// <summary>별로 표시한 앨범 탭 (저장함은 즐겨찾기만 — 별로가 섞여 있으면 위시리스트를 보기 어렵다)</summary>
    public bool IsDislikedTab { get => SelectedTab == 3; set { if (value) SelectedTab = 3; } }

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
    /// <summary>점수 없이 먼저 저장된 AI 추천(소리 서비스가 바빴을 때)에 점수를 매겨 다시 저장한다</summary>
    private async Task RescoreAiAsync(Period period)
    {
        try
        {
            var all = AiPicksFor(period);
            var bare = all.Where(r => r.Taste == null && r.Crate == null).ToList();
            if (bare.Count == 0) return;
            _owned ??= new OwnedLibrary(await _library.GetTracksAsync(_life.Token));
            var scored = await _recommender.RescoreAsync(bare, _owned, ct: _life.Token);
            var byId = scored.ToDictionary(r => r.DeezerAlbumId);
            _aiPicks.Set(AiKey(period), [.. all.Select(r => byId.GetValueOrDefault(r.DeezerAlbumId, r))]);
            _aiPicks.Save();
            if (CurrentPeriod.Label != period.Label) return;
            _all = [.. _all.Select(i => byId.TryGetValue(i.Rec.DeezerAlbumId, out var r) && !i.ShowTracks && !i.IsPlaying ? MakeItem(r, i.SavedNote) : i)];
            ApplyFilters();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    /// <summary>
    /// AI 에게 보내는 취향 요약 (영어 머리글): 라이브러리에서 많이 듣고 좋아한 뮤지션 + 앱에서 남긴 신호.
    /// 신호를 안 보내면 AI 는 차단한 아티스트도 찾아오고(앱에서 걸러질 뿐) 좋아요 방향을 모른다.
    /// </summary>
    private string AiTaste()
    {
        static string Line(string head, IEnumerable<string> items) =>
            items.Where(x => x.Length > 0).Distinct().ToList() is { Count: > 0 } l ? $"- {head}: {string.Join("; ", l)}" : "";
        var artists = string.Join(", ", _mine!.Take(60).Select(m => m.Artist));
        var signals = new[]
        {
            Line("Liked (more like these)", _likedAlbums.All.Take(15).Select(s => $"{s.Rec.Artist} – {s.Rec.Album}")
                .Concat(_likedTracks.All.TakeLast(10).Select(t => $"{t.Artist} – {t.Title}"))),
            Line("Following (their scenes matter)", _follows.All.Select(f => f.Name)),
            Line("Favorite labels", _labelAffinity.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key)),
            Line("Not for me (avoid similar sounds)", _disliked.All.Take(15).Select(d => $"{d.Rec.Artist} – {d.Rec.Album}")),
            Line("Blocked artists (never suggest)", _blocked.Artists),
            Line("Blocked labels (never suggest)", _blocked.Labels),
        }.Where(x => x.Length > 0);
        return "Artists I listen to most (whole library, most loved first):\n" + artists
               + (signals.Any() ? "\n\nSignals from my listening in this app:\n" + string.Join("\n", signals) : "");
    }

    private List<ReleaseRecommendation> AiPicksFor(Period p)
    {
        var all = new Dictionary<long, ReleaseRecommendation>();
        foreach (var key in OldRegionKeys.Select(r => $"{p.Label}|{r}").Prepend(AiKey(p)))
            if (_aiPicks.TryGet(key, out var list))
                foreach (var r in list)
                    all[r.DeezerAlbumId] = all.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
        // 월·연: 주별로 찾은 결과(다른 키)도 그 기간에 발매됐으면 합친다
        if (p.Kind != PeriodKind.Week)
            foreach (var r in _aiPicks.FreshValues.SelectMany(l => l).Where(r => p.Contains(r.ReleaseDate)))
                all[r.DeezerAlbumId] = all.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
        return [.. all.Values];
    }

    /// <summary>
    /// 레시피·지역으로 나누던 때 저장한 결과를 장르 칸으로 옮긴다 (지난 기간은 다시 계산하지 않고 저장 결과를 쓴다).
    /// Deezer 장르 → 소리 장르 태그(EffNet) → 예전 칸 이름 순. 점수 전(칸·취향 없음) AI 결과는 그대로 둔다 (다시 점수를 매길 표시).
    /// 예전 플레이리스트 뮤지션 표시도 '내 라이브러리'로 바꾼다
    /// </summary>
    private static ReleaseRecommendation Regroup(ReleaseRecommendation r)
    {
        // 예전 플레이리스트 뮤지션 배지(플레이리스트 이름)는 '내 라이브러리'로
        if (r.FromPlaylist is { } from && from != FollowList.Tag && from != TasteProfile.LibraryTag)
            r = r with { FromPlaylist = TasteProfile.LibraryTag, BecauseOf = [TasteProfile.LibraryTag] };
        if (r.Crate == null || GenreGroups.All.Contains(r.Crate)) return r;
        var g = ReleaseRecommender.DeezerGroup(r.GenreName, null) ?? GenreGroups.Of(r.Genres) ?? r.Crate switch
        {
            "Club" or "해외 일렉트로닉" => GenreGroups.Electronic,
            "해외 소울·힙합" => GenreGroups.HipHop,
            "Jazz" => GenreGroups.Jazz,
            _ => GenreGroups.Of(r.Crate) ?? GenreGroups.Indie,
        };
        return r with { Crate = g, CrateFit = null, Region = Region.Unknown };
    }

    private readonly AiUsageLedger _aiLedger = new();

    private IReadOnlyList<IAiScout> Scouts => [new ClaudeScout(_aiSettings, _aiLedger), new GptScout(_aiSettings, _aiLedger)];

    /// <summary>
    /// 사이드바 카드: 커넥터마다 모델 한 줄 + 금액 한 줄 — "Claude Sonnet 5.5" / "오늘 $0.12/$1 · 주 $0.40/$5 · 남은 약 $4.60"
    /// (한 줄로 이으면 카드 폭에서 금액 중간이 끊겨 어색하게 줄바꿈됐다)
    /// </summary>
    private void RefreshAiStatus()
    {
        var ready = Scouts.Where(s => s.IsReady).ToList();
        AiReady = ready.Count > 0;
        string Line(string name, string model)
        {
            if (ready.All(s => s.Name != name)) return L.F("{0} 키 필요", name);
            var b = _aiSettings.BudgetOf(name);
            string Of(double spent, double? limit) => AiUsageLedger.Money(spent) + (limit is { } l ? $"/{AiUsageLedger.Money(l)}" : "");
            var parts = new List<string>
            {
                model,
                L.F("오늘 {0}", Of(_aiLedger.Spent(AiUsageLedger.TodayStart, name), b.Daily)),
                L.F("주 {0}", Of(_aiLedger.Spent(AiUsageLedger.WeekStart, name), b.Weekly)),
            };
            if (b.Remaining(_aiLedger, name) is { } left) parts.Add(L.F("남은 약 {0}", AiUsageLedger.Money(Math.Max(0, left))));
            if (b.CreditExpires is { } exp)
            {
                var days = (exp - DateTime.Today).Days;
                parts.Add(days < 0 ? L.T("크레딧 만료됨?") : days <= 30 ? L.F("만료 {0}일 남음", days) : L.F("{0:yy.M.d} 만료", exp));
            }
            if (b.Blocked(_aiLedger, name) != null) parts.Add(L.T("한도 도달"));
            // 키: 확인 결과(만료·삭제) > 곧 만료 경고 > 만료 예정일
            if (_keyProblems.TryGetValue(name, out var problem)) parts.Add(problem);
            else if (b.KeyExpiryWarning() is { } warn) parts.Add(warn);
            else if (b.KeyExpires is { } keyUntil) parts.Add(L.F("키 {0:M.d}까지", keyUntil));
            return parts[0] + "\n" + string.Join(" · ", parts.Skip(1));
        }
        var lines = new List<string>();
        if (_aiSettings.Enabled)
            lines.Add(Line("Claude", AiConnectorSettings.Models.FirstOrDefault(m => m.Id == _aiSettings.Model).Label?.Split(" (")[0] ?? _aiSettings.Model));
        if (_aiSettings.GptEnabled) lines.Add(Line("GPT", _aiSettings.GptModel));
        AiStatus = lines.Count == 0 ? L.T("꺼짐") :string.Join("\n", lines);
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
    /// <summary>AI 찾기 옵션: 주별로(월에서만) · 저장된 AI 결과 지우고 다시 (한 번 돌리면 풀린다 — 실수로 계속 지우지 않게)</summary>
    [ObservableProperty] private bool _aiWeekly;
    /// <summary>주별로 찾기는 주를 여럿 담은 월·연에서만 (연은 52번 검색이라 시작 전에 예상 비용을 묻는다)</summary>
    public bool CanAiWeekly => !IsWeekMode;
    [ObservableProperty] private bool _aiReplace;

    [RelayCommand]
    private async Task FindWithAiAsync(string? mode)
    {
        if (AiBusy) return;
        if (!AiReady) { OpenConnector(); if (!AiReady) return; }
        bool replace = mode == "replace" || AiReplace && HasAiSaved;
        bool weekly = mode == "weekly" || AiWeekly && CanAiWeekly;
        AiReplace = false;
        if (weekly) await FindWithAiWeeklyAsync(replace);
        else await AiSearchAsync(CurrentPeriod, replace);
    }

    /// <summary>
    /// AI 주별로 찾기 (월·연): 한 달(1년)을 한 번에 찾으면 검색 8회로 한 달을 훑어 주마다 몇 장씩만 건진다 → 주마다 따로 찾는다.
    /// 결과는 주 키로 저장되고, 월 화면은 그 주들의 결과를 합쳐 보여 준다 (AiPicksFor). 비용은 주 수만큼.
    /// </summary>
    private async Task FindWithAiWeeklyAsync(bool replace)
    {
        var month = CurrentPeriod;
        var weeks = new List<Period>();
        for (var d = month.Start; d <= month.End; d = d.AddDays(7))
            if (Periods.Create(PeriodKind.Week, d) is var w && weeks.All(x => x.Label != w.Label)) weeks.Add(w);
        if (Periods.Create(PeriodKind.Week, month.End) is var last && weeks.All(x => x.Label != last.Label)) weeks.Add(last);
        // 예상 비용: 최근 검색 한 번 비용 × 주 수 (기록이 없으면 Sonnet 기준 $0.15)
        double per = _aiLedger.Recent(5) is { Count: > 0 } r ? r.Average() : 0.15;
        if (!ConfirmWindow.Ask(Application.Current.MainWindow, L.T("AI 주별로 찾기"),
                L.F("{0}을(를) 주 {1}번으로 나눠 AI로 찾을까요?\n한 번에 찾을 때보다 촘촘하지만 비용이 주 수만큼 들어요 (예상 약 ${2:0.00}).", month.Display, weeks.Count, per * weeks.Count),
                L.F("약 ${0:0.00} 쓰고 찾기", per * weeks.Count))) return;
        var start = DateTime.Now;
        for (int i = 0; i < weeks.Count; i++)
        {
            if (i > 0 && !AiWeekly) break;  // 진행 중에 '주별로' 체크를 풀면 그 주까지만 하고 멈춘다 (연은 52번이라 오래 걸린다)
            if (!await AiSearchAsync(weeks[i], replace, L.F("주별로 찾는 중 {0}/{1} · {2} · 멈추려면 '주별로' 체크를 풀어요", i + 1, weeks.Count, weeks[i].Display))) break;  // 한도에 닿으면 멈춘다
        }
        await LoadAsync();  // 월 화면에 주별 결과를 합쳐 다시 보여 준다
        AiText = L.F("주별로 찾기 끝 · 이번 검색 {0}", AiUsageLedger.Money(_aiLedger.Spent(start)));
        UpdateAiMarks();
    }

    /// <returns>계속해도 되면 true (한도·키 문제로 아무 커넥터도 못 돌리면 false)</returns>
    private async Task<bool> AiSearchAsync(Period period, bool replace, string? stepText = null)
    {
        // 일일·주간 한도나 충전액에 닿은 커넥터는 이번에 쉰다
        var blocked = Scouts.Where(s => s.IsReady)
            .Select(s => (s, why: _aiSettings.BudgetOf(s.Name).Blocked(_aiLedger, s.Name)))
            .Where(x => x.why != null).ToList();
        var scouts = Scouts.Where(s => s.IsReady && blocked.All(b => b.s.Name != s.Name)).ToList();
        if (scouts.Count == 0)
        {
            AiText = string.Join(" · ", blocked.Select(b => $"{b.s.Name}: {b.why}")) + " " + L.T("(설정에서 한도를 바꿀 수 있어요)");
            return false;
        }
        var runStart = DateTime.Now;
        AiBusy = true;
        var status = new Dictionary<string, string>();
        void Report(string who, string m) { lock (status) status[who] = m; AiText = (stepText != null ? stepText + "  ·  " : "") + string.Join("  ·  ", status.Values); }
        var progress = new Progress<string>(m => AiText = m);
        try
        {
            var tracks = await _library.GetTracksAsync(_life.Token);
            _mine ??= TasteProfile.LibraryArtists(tracks);
            var taste = AiTaste();
            // 다시 찾기면 지난 AI 결과도 다시 나올 수 있게 제외 목록에서 뺀다
            var exclude = _all.Where(i => !(replace && i.IsAi)).Select(i => $"{i.Artist} – {i.Album}").ToList();

            // 커넥터마다 따로 돌리고, 하나가 실패해도 나머지 결과는 쓴다
            var runs = await Task.WhenAll(scouts.Select(async s =>
            {
                try
                {
                    var p = await s.FindAsync(period, taste, exclude, null, new Progress<string>(m => Report(s.Name, m)), _life.Token);
                    Report(s.Name, L.F("{0} {1}장 찾음", s.Name, p.Count));
                    return (s.Name, picks: p, error: (string?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report(s.Name, L.F("{0} 실패", s.Name));
                    return (s.Name, picks: (IReadOnlyList<AiPick>)[], error: ex.Message);
                }
            }));
            var picks = runs.SelectMany(r => r.picks).ToList();
            _owned ??= new OwnedLibrary(tracks);
            // Deezer 확인이 끝나면 점수 전에 먼저 저장한다: 소리 서비스가 다른 기간 분석으로 바쁘면 점수가 한참 걸리고,
            // 그사이 앱을 끄면 AI 비용만 쓰고 결과가 사라졌다. 점수 없는 추천은 기간을 열 때 다시 점수를 매긴다
            void SaveMatched(IReadOnlyList<ReleaseRecommendation> bare)
            {
                var have = AiPicksFor(period).ToDictionary(r => r.DeezerAlbumId);
                foreach (var r in bare) have[r.DeezerAlbumId] = have.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
                _aiPicks.Set(AiKey(period), [.. have.Values]);
                _aiPicks.Save();
                if (_sound?.IsBusy == true)
                    AiText = L.F("{0}장 확인해 저장했어요 · 소리 분석이 다른 작업 중이라 끝나면 점수를 매겨요", bare.Count);
            }
            var (recs, unverified, otherWeeks) = picks.Count == 0 ? ([], 0, [])
                : await _recommender.ScoreAiPicksAsync(picks, _owned, period, progress, _life.Token, SaveMatched);
            // AI가 날짜를 틀린 다른 주 발매: 버리지 않고 실제 발매 주에 저장 (점수는 그 주를 열 때)
            foreach (var g in otherWeeks.GroupBy(r => Periods.Create(PeriodKind.Week, r.ReleaseDate.ToDateTime(TimeOnly.MinValue))))
            {
                var have = AiPicksFor(g.Key).ToDictionary(r => r.DeezerAlbumId);
                foreach (var r in g) have[r.DeezerAlbumId] = have.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
                _aiPicks.Set(AiKey(g.Key), [.. have.Values]);
            }

            if (replace)
            {
                // 이 기간의 예전 결과(예전 "기간|지역" 키 포함)를 지운다
                foreach (var key in OldRegionKeys.Select(r => $"{period.Label}|{r}")) _aiPicks.Set(key, []);
            }
            var merged = (replace ? [] : AiPicksFor(period)).ToDictionary(r => r.DeezerAlbumId);
            foreach (var r in recs) merged[r.DeezerAlbumId] = merged.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r;
            _aiPicks.Set(AiKey(period), [.. merged.Values]);
            _aiPicks.Save();
            UpdateAiMarks();

            // 그새 다른 기간으로 옮겼으면 저장만 하고 화면은 그대로
            if (CurrentPeriod.Label == period.Label)
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
                MarkSeen();
            }
            var summary = string.Join(" · ", runs.Select(r => r.error == null
                ? L.F("{0} {1}장", r.Name, recs.Count(x => x.AiBy?.Any(b => b.StartsWith(r.Name, StringComparison.Ordinal)) == true))
                : L.F("{0} 실패: {1}", r.Name, r.error)));
            AiText = L.F("AI 제안 {0}", summary) + (unverified > 0 ? " · " + L.F("Deezer에서 확인 못 한 {0}장은 뺐어요", unverified) : "")
                     + (otherWeeks.Count > 0 ? " · " + L.F("다른 주 발매 {0}장은 그 주에 넣었어요", otherWeeks.Count) : "")
                     + " · " + L.F("이번 검색 {0}", AiUsageLedger.Money(_aiLedger.Spent(runStart)))
                     + (blocked.Count > 0 ? " · " + string.Join(", ", blocked.Select(b => L.F("{0} 쉼: {1}", b.s.Name, b.why))) : "");
        }
        catch (OperationCanceledException) { AiText = ""; return false; }
        catch (Exception ex) { AiText = L.F("AI로 찾지 못했어요: {0}", ex.Message); }
        finally
        {
            AiBusy = false;
            UpdateAiSaved();
        }
        return true;
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
            : L.F("저장된 AI 제안 {0}장", picks.Count) + (by.Count > 0 ? $" ({string.Join(", ", by)})" : "")
              + (at is { } a ? " · " + L.F("{0:M.d HH:mm} 검색", a) : "");
    }

    // ---- 저장함 (나중에 디깅) -------------------------------------------------

    public ObservableCollection<CrateSection> SavedSections { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoSaved))] private int _savedCount;
    public bool NoSaved => SavedCount == 0;

    private ReleaseItem MakeItem(ReleaseRecommendation r, string? savedNote = null) => MakeItemCore(Regroup(r), savedNote);

    private ReleaseItem MakeItemCore(ReleaseRecommendation r, string? savedNote) =>
        new(r, PlayReleaseAsync, ToggleTracksAsync, ToggleSave, _saved.Contains(r.DeezerAlbumId))
        {
            SavedNote = savedNote,
            FoundCover = _foundCovers.GetValueOrDefault(r.DeezerAlbumId),
            // 카드의 비슷한 내 곡: 곡마다 들은 결과(수록곡 목록과 같은 곡) — 수록곡 목록에서 다시 계산한 것 → 점수 때 낸 곡 짝 → (예전 결과) 앨범 전체 기준
            NearItems = [.. (_trackNear.TryGet(r.DeezerAlbumId.ToString(), out var tp) && tp.Count > 0 ? tp.Select(p => p.Near)
                    : r.Pairs is { Count: > 0 } pairs ? pairs.Select(p => p.Near) : r.NearTracks.Take(3))
                .Select(n => new NearTrackItem(n, FindLocalFile(n), PlayNear))],
            ToggleDislikeAction = item => KeepPlace(item, () => ToggleDislike(item)),
            IsDisliked = _disliked.Contains(r.DeezerAlbumId),
            IsNew = _newThisRun.Contains(r.DeezerAlbumId),
            IsFollowed = IsFollowedRec(r),
            ToggleFollowAction = ToggleFollowAsync,
            OpenAppleMusicAction = OpenAppleMusicAsync,
            ToggleLikeAction = ToggleLike,
            IsLiked = _likedAlbums.Contains(r.DeezerAlbumId),
            IsTrackLiked = _likedTracks.Contains,
            ToggleTrackLikeAction = ToggleTrackLike,
        };

    /// <summary>앨범 하트 ↔ 해제 (별로였으면 별로는 푼다)</summary>
    private void ToggleLike(ReleaseItem item)
    {
        var id = item.Rec.DeezerAlbumId;
        bool like = !_likedAlbums.Contains(id);
        if (like)
        {
            _likedAlbums.Add(item.Rec);
            if (_disliked.Contains(id)) _disliked.Remove(id);
        }
        else _likedAlbums.Remove(id);
        foreach (var i in _all.Where(i => i.Rec.DeezerAlbumId == id)) { i.IsLiked = like; if (like) i.IsDisliked = false; }
        item.IsLiked = like;
        if (like) item.IsDisliked = false;
        _ = SyncFavoritesAsync();
    }

    /// <summary>곡 하트 ↔ 해제</summary>
    private void ToggleTrackLike(TrackItem track)
    {
        if (track.Id == 0) return;
        var rec = track.Release.Rec;
        track.IsLiked = _likedTracks.Toggle(new LikedTrack(track.Id, rec.DeezerAlbumId, track.Artist, track.Title, DateTime.Now));
        _ = SyncFavoritesAsync();
    }

    /// <summary>별로 ↔ 되돌리기. 별로로 하면 저장함에서는 뺀다 (둘 다일 수는 없으니)</summary>
    private void ToggleDislike(ReleaseItem item)
    {
        var id = item.Rec.DeezerAlbumId;
        bool dislike = !_disliked.Contains(id);
        bool wasLiked = _likedAlbums.Contains(id);
        if (dislike)
        {
            _disliked.Add(item.Rec);
            if (_likedAlbums.Contains(id)) _likedAlbums.Remove(id);
        }
        else _disliked.Remove(id);
        foreach (var i in _all.Where(i => i.Rec.DeezerAlbumId == id)) { i.IsDisliked = dislike; if (dislike) i.IsLiked = false; }
        item.IsDisliked = dislike;
        if (dislike) item.IsLiked = false;
        ApplyFilters();   // 추천에서 바로 숨긴다
        RebuildSaved();
        _ = SyncFavoritesAsync();
        if (!dislike) { DropSimilarOfferFrom(item); return; }
        _ = OfferSimilarAsync(item);
        OfferUndo(L.F("'{0}' 관심 없음으로 숨겼어요", item.Album), () =>
        {
            if (_disliked.Contains(id)) ToggleDislike(item);
            if (wasLiked && !_likedAlbums.Contains(id)) ToggleLike(item);  // 별로로 풀린 좋아요도 되돌린다
            DropSimilarOfferFrom(item);
        });
    }

    // ---- 별로·'소리 때문' 차단을 누른 자리에서: 이 목록에서 아주 닮은 추천도 같이 숨길지 묻는다 ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSimilarOffer))] private string _similarOfferText = "";
    public bool HasSimilarOffer => SimilarOfferText.Length > 0;
    /// <summary>배너가 묻는 중 (숨기기 버튼). false 면 자동으로 뺀 결과를 알리기만 한다</summary>
    [ObservableProperty] private bool _similarAsk = true;
    private List<long> _similarOffer = [];
    /// <summary>배너를 띄운 별로·차단 앨범 (되돌리면 배너도 거둔다)</summary>
    private long? _similarFrom;
    /// <summary>다시 분석 후 물어본 것 (저장 키 → 앨범 id). 숨기기·그대로를 누르기 전에 앱을 껐다 켜도 그 기간을 열면 다시 묻는다</summary>
    private readonly JsonCache<List<long>> _pendingDrops = new("pending-drops", TimeSpan.FromDays(90));
    private string? _pendingKey;

    /// <summary>저장된 결과를 열 때: 아직 답하지 않은 질문이 있으면 다시 띄운다 (목록에 남아 있는 것만)</summary>
    private void RestorePendingDrops(string key)
    {
        if (!_pendingDrops.TryGet(key, out var ids) || ids.Count == 0) return;
        var still = ids.Where(id => _all.Any(i => i.Rec.DeezerAlbumId == id)).ToList();
        if (still.Count == 0) { _pendingDrops.Set(key, []); _pendingDrops.Save(); return; }
        _similarOffer = still;
        _similarFrom = null;
        _pendingKey = key;
        SimilarAsk = true;
        SimilarOfferText = L.F("관심 없는 앨범과 소리가 거의 같은 추천이 {0}장 있어요 · {1}", still.Count, ShortList(still));
    }

    /// <summary>
    /// 다시 분석한 뒤: 별로 목록(별로·'소리 때문' 차단·바로 넘긴 앨범)과 아주 닮은(0.85) 추천을 설정대로 묻거나 뺀다.
    /// 점수에는 이미 감점이 들어가지만 화면은 소리를 들은 앨범을 다 보여 줘서, 감점만으로는 목록에서 사라지지 않는다.
    /// </summary>
    private async Task CheckDislikeMatchesAsync(Period period, string key)
    {
        if (_sound == null || _recPrefs.DropMode == 2) return;
        var candidates = FilteredBase().Where(i => !i.IsMine && !i.IsLiked && !i.IsSaved && !i.IsDisliked)
            .Select(i => i.Rec.DeezerAlbumId).Distinct().ToList();
        if (candidates.Count == 0) return;
        try
        {
            // 기준은 직접 별로·'소리 때문' 차단한 앨범만 (바로 넘긴 앨범까지 넣으면 거의 다 걸린다)
            var refs = _disliked.All.Select(d => d.Rec.DeezerAlbumId).Concat(_blocked.AlbumIds).Distinct();
            var hits = await _sound.DislikeMatchesAsync(candidates, refs, ct: _life.Token);
            if (hits.Count == 0 || CurrentPeriod.Label != period.Label) return;
            _similarOffer = [.. hits.Select(h => h.Id)];
            _similarFrom = null;
            var names = ShortList(hits.Select(h => h.Id));
            if (_recPrefs.DropMode == 1)
            {
                HideSimilar();  // 목록·저장 결과에서 뺀다 (배너를 닫는다)
                SimilarAsk = false;
                SimilarOfferText = L.F("관심 없는 앨범과 소리가 거의 같은 {0}장을 숨겼어요 · {1}", hits.Count, names);
            }
            else
            {
                SimilarAsk = true;
                SimilarOfferText = L.F("관심 없는 앨범과 소리가 거의 같은 추천이 {0}장 있어요 · {1}", hits.Count, names);
                _pendingKey = key;
                _pendingDrops.Set(key, _similarOffer);
                _pendingDrops.Save();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private async Task OfferSimilarAsync(ReleaseItem item)
    {
        if (_sound == null) return;
        // 내 라이브러리 뮤지션·팔로우·좋아요·저장한 앨범은 묻지 않는다 (그 자체로 원하는 앨범)
        var candidates = FilteredBase().Where(i => i != item && !i.IsMine && !i.IsLiked && !i.IsSaved && !i.IsDisliked)
            .Select(i => i.Rec.DeezerAlbumId).Distinct().ToList();
        if (candidates.Count == 0) return;
        try
        {
            var near = (await _sound.SimilarAlbumsAsync(item.Rec.DeezerAlbumId, candidates, ct: _life.Token)).Take(5).ToList();
            // 기다리는 사이 되돌렸으면 묻지 않는다
            if (near.Count == 0 || (!_disliked.Contains(item.Rec.DeezerAlbumId) && !_blocked.IsBlocked(item.Rec))) return;
            _similarOffer = [.. near.Select(n => n.Id)];
            _similarFrom = item.Rec.DeezerAlbumId;
            SimilarAsk = true;
            SimilarOfferText = L.F("방금 관심 없음으로 표시한 '{0}'와 소리가 거의 같은 추천 {1}장 · {2}", item.Album, near.Count, ShortList(near.Select(n => n.Id)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }  // 서비스가 바쁘면 묻지 않는다
    }

    /// <summary>배너에 넣을 앨범 이름: 앞 3장 + '외 N장'</summary>
    private string ShortList(IEnumerable<long> ids)
    {
        var names = ids.Select(id => _all.FirstOrDefault(i => i.Rec.DeezerAlbumId == id)).OfType<ReleaseItem>().Select(i => $"{i.Artist} – {i.Album}").ToList();
        return string.Join(" · ", names.Take(3)) + (names.Count > 3 ? " " + L.F("외 {0}장", names.Count - 3) : "");
    }

    /// <summary>같이 숨기기: 이 목록과 저장된 결과에서만 뺀다 (별로로 학습하지 않는다 — 한 번 누른 것보다 학습이 커지지 않게)</summary>
    [RelayCommand]
    private void HideSimilar()
    {
        var ids = _similarOffer.ToHashSet();
        _all = [.. _all.Where(i => !ids.Contains(i.Rec.DeezerAlbumId))];
        var key = RecKey(CurrentPeriod);
        if (_recResults.TryGet(key, out var saved)) { _recResults.Set(key, [.. saved.Where(r => !ids.Contains(r.DeezerAlbumId))]); _recResults.Save(); }
        var ai = AiPicksFor(CurrentPeriod);
        if (ai.Any(r => ids.Contains(r.DeezerAlbumId))) { _aiPicks.Set(AiKey(CurrentPeriod), [.. ai.Where(r => !ids.Contains(r.DeezerAlbumId))]); _aiPicks.Save(); }
        KeepSimilar();
        ApplyFilters();
    }

    [RelayCommand]
    private void KeepSimilar()
    {
        _similarOffer = [];
        _similarFrom = null;
        SimilarOfferText = "";
        if (_pendingKey != null)  // 답했으니 다시 묻지 않는다
        {
            _pendingDrops.Set(_pendingKey, []);
            _pendingDrops.Save();
            _pendingKey = null;
        }
    }

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
            // 학습: 앨범 하트 + 곡 하트 (저장함은 위시리스트라 쓰지 않는다) / 별로: 별로 + 바로 넘긴 앨범 + 차단할 때 본 앨범
            var favs = _likedAlbums.All.Select(s => (s.Rec.DeezerAlbumId.ToString(), $"{s.Rec.Artist} – {s.Rec.Album}"))
                .Concat(_likedTracks.All.Select(t => ($"t{t.TrackId}", $"{t.Artist} – {t.Title}")))
                .Concat(ImplicitLikes().Where(x => !_disliked.Contains(x.Id)).Select(x => (x.Id.ToString(), x.Name)))
                .DistinctBy(x => x.Item1).ToList();
            await _sound.SetFavoritesAsync(favs, _life.Token,
                _disliked.All.Select(d => d.Rec.DeezerAlbumId).Concat(_blocked.AlbumIds)
                    .Concat(ImplicitSkips().Where(id => !_likedAlbums.Contains(id))).Distinct());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }  // 서비스가 아직 안 떴으면 시작할 때 다시 보낸다
    }

    /// <summary>저장함이 바뀌면 추천 결과·후보 풀 저장 키가 바뀐다 (저장 앨범이 점수와 시드에 들어가니까)</summary>
    private string FavoritesVersion =>
        $"{_likedAlbums.All.Count}-{_likedAlbums.All.Sum(s => s.Rec.DeezerAlbumId % 100_000):x}-{_likedTracks.All.Count}-{_follows.All.Count}";

    private void RebuildSaved()
    {
        // 차단한 뮤지션·레이블은 저장함에서도 숨긴다 (저장 기록은 두어 차단을 풀면 다시 보인다)
        var all = _saved.All.Where(s => !_blocked.IsBlocked(s.Rec)).ToList();
        SavedCount = all.Count;
        SavedSections.Clear();
        var items = all.Select(s => MakeItem(s.Rec, L.F("{0:M.d} 저장", s.SavedAt))).ToList();
        var order = CrateOrder.ToList();
        foreach (var g in items.GroupBy(i => i.CrateName)
                     .OrderBy(g => order.IndexOf(g.Key) is var k && k >= 0 ? k : 99))
        {
            var list = g.ToList();
            SavedSections.Add(new CrateSection(g.Key, list, list.Count, list.Count(i => i.IsMine), false, _ => { }));
        }
        // 별로 표시한 앨범은 별로 탭에 (👎를 다시 누르면 되돌린다)
        DislikedItems.Clear();
        foreach (var d in _disliked.All.OrderByDescending(d => d.SavedAt)) DislikedItems.Add(MakeItem(d.Rec, L.F("{0:M.d} 관심 없음", d.SavedAt)));
        DislikedCount = DislikedItems.Count;
        _ = FillCoversAsync(items.Concat(DislikedItems));
    }

    public ObservableCollection<ReleaseItem> DislikedItems { get; } = [];
    [ObservableProperty] private int _dislikedCount;
    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsRecsTab));
        OnPropertyChanged(nameof(IsLibraryTab));
        OnPropertyChanged(nameof(IsSavedTab));
        OnPropertyChanged(nameof(IsDislikedTab));
        ScrollTopRequested?.Invoke();
    }

    private void SelectCrate(string key)
    {
        _crate = key;
        _pageSize = PageStep;
        ApplyFilters();
        ScrollTopRequested?.Invoke();
    }

    /// <summary>한 레시피만 볼 때 처음 그리는 카드 수. 연 보기는 레시피 하나에 수백 장이라 한 번에 그리면 느리다</summary>
    public const int PageStep = 60;
    private int _pageSize = PageStep;

    private void ShowMoreCards()
    {
        _pageSize += PageStep;
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

    private void SyncChips()
    {
        foreach (var c in CrateChips) c.IsSelected = c.Key == _crate;
        foreach (var c in TypeChips) c.IsSelected = c.Key == "all" ? _types.Count == 0 : _types.Contains(c.Key);
        OnPropertyChanged(nameof(FilterSummary));
    }

    /// <summary>탭 줄 오른쪽 필터 버튼: 지금 고른 형식 (예: "앨범·EP")</summary>
    public string FilterSummary =>
        _types.Count == 0 ? L.T("모든 형식") : string.Join("·", TypeChips.Where(c => c.IsSelected).Select(c => c.Label));

    private IEnumerable<ReleaseItem> FilteredBase()
    {
        var dislikedArtists = DislikedArtists;
        var floor = OutsideTasteFloor();
        // 별로 표시한 앨범은 숨기고, 그 아티스트의 추가 제안도 숨긴다 (내 라이브러리 뮤지션 신보는 그대로)
        // 인스트루멘털 버전: 같은 아티스트의 원래 앨범이 결과나 내 라이브러리에 있으면 뺀다 (저장된 예전 결과에도)
        var titles = _recPrefs.ExcludeInstrumental
            ? _all.Select(i => ListeningAnalyzer.NormalizeArtist(i.Artist) + "|" + ListeningAnalyzer.NormalizeArtist(i.Album)).ToHashSet() : null;
        bool RedundantInstrumental(ReleaseItem i) => titles != null && AlbumVariants.InstrumentalBase(i.Album) is { } original
            && (titles.Contains(ListeningAnalyzer.NormalizeArtist(i.Artist) + "|" + ListeningAnalyzer.NormalizeArtist(original))
                || _owned?.HasAlbum(i.Artist, original) == true);
        return _all.Where(i => !_blocked.IsBlocked(i.Rec) && !RedundantInstrumental(i))
            .Where(i => !i.IsDisliked
            && !(i.Rec.FromPlaylist == null && dislikedArtists.Contains(ListeningAnalyzer.NormalizeArtist(i.Rec.Artist))))
            // AI·웹진이 찾은 앨범도 내 곡과 닮지 않았으면 숨긴다 (내 라이브러리·팔로우 뮤지션 것은 그대로)
            .Where(i => !(i.IsAi && !i.IsMine && i.Rec.Taste is { } t && t < floor))
            .Where(i => _types.Count == 0 || _types.Contains(i.Rec.RecordType));
    }

    /// <summary>
    /// AI·웹진 앨범을 보여 줄 닮음 기준: 이 기간 일반 추천(이미 점수 상위)의 하위 25% 선. 일반 추천이 적으면 기본값.
    /// 라이브러리마다 닮음 값의 크기가 달라서 고정값 대신 그 기간 추천과 견준다
    /// </summary>
    private double OutsideTasteFloor()
    {
        var ts = _all.Where(i => !i.IsAi && !i.IsMine && i.Rec.Taste != null).Select(i => i.Rec.Taste!.Value).OrderBy(t => t).ToList();
        return ts.Count >= 20 ? ts[ts.Count / 4] : new RecommendOptions().MinOutsideTaste;
    }

    /// <summary>크레이트 칩 옆 "AI 제안" 칩: AI 커넥터가 찾은 것만 모아 본다</summary>
    private const string AiChip = "AI 제안";
    /// <summary>"웹진" 칩: 웹진 RSS에서 가져온 것만</summary>
    private const string WebzineChip = "웹진";

    // ---- 외부 추천 가져오기: 웹진 RSS (켤 때와 6시간마다 새 글만) ----
    private readonly JsonCache<List<ReleaseRecommendation>> _webzinePicks = new("webzine-picks", TimeSpan.FromDays(3650));
    private readonly JsonCache<bool> _webzineSeen = new("webzine-seen", TimeSpan.FromDays(365));
    private readonly JsonCache<DateTime> _webzineRun = new("webzine-run", TimeSpan.FromDays(3650));

    /// <summary>끈 웹진 이름 (추천 설정에서)</summary>
    public HashSet<string> DisabledWebzines { get; private set; } = [];

    private List<ReleaseRecommendation> AllWebzinePicks() => _webzinePicks.TryGet("all", out var l) ? l : [];

    /// <summary>이 기간에 나온 웹진 추천 (꺼 둔 웹진 것은 뺀다)</summary>
    private List<ReleaseRecommendation> WebzinePicksFor(Period p) =>
        [.. AllWebzinePicks().Where(r => p.Contains(r.ReleaseDate))
            .Where(r => r.AiBy?.Any(b => !DisabledWebzines.Contains(b.Replace("웹진 · ", ""))) != false)];

    /// <summary>웹진 새 글을 읽어 Deezer 확인·소리 점수까지 매겨 저장한다. force면 6시간 안 지나도.</summary>
    public async Task RefreshWebzinesAsync(bool force = false)
    {
        if (!force && _webzineRun.TryGet("last", out var last) && DateTime.Now - last < TimeSpan.FromHours(6)) return;
        var reader = new WebzineFeeds(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        var picks = new List<AiPick>();
        foreach (var feed in WebzineFeeds.Defaults.Where(f => !DisabledWebzines.Contains(f.Name)))
        {
            try
            {
                SyncText = L.F("웹진 새 글 확인 중 · {0}", feed.Name);
                picks.AddRange((await reader.ReadAsync(feed, _life.Token)).Where(p => p.Source is { } link && !_webzineSeen.TryGet(link, out _)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { }
        }
        try
        {
            if (picks.Count > 0)
            {
                _owned ??= new OwnedLibrary(await _library.GetTracksAsync(_life.Token));
                var (recs, _, _) = await _recommender.ScoreAiPicksAsync(picks, _owned, null,
                    new Progress<string>(m => SyncText = L.F("웹진 추천 확인 중 · {0}", m)), _life.Token);
                var all = AllWebzinePicks().ToDictionary(r => r.DeezerAlbumId);
                foreach (var r in recs)
                    all[r.DeezerAlbumId] = all.TryGetValue(r.DeezerAlbumId, out var o) ? MergeAi(o, r) : r with { BecauseOf = ["웹진"] };
                _webzinePicks.Set("all", [.. all.Values]);
                _webzinePicks.Save();
                foreach (var p in picks) _webzineSeen.Set(p.Source!, true);
                _webzineSeen.Save();
                // 지금 보는 기간에 새로 들어온 게 있으면 바로 넣는다
                var period = CurrentPeriod;
                var add = recs.Where(r => period.Contains(r.ReleaseDate) && _all.All(i => i.Rec.DeezerAlbumId != r.DeezerAlbumId)).ToList();
                if (add.Count > 0)
                {
                    _all = [.. _all, .. add.Select(r => MakeItem(r with { BecauseOf = ["웹진"] }))];
                    ApplyFilters();
                    MarkSeen();
                }
                UpdateAiMarks();
            }
            _webzineRun.Set("last", DateTime.Now);
            _webzineRun.Save();
            SyncText = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SyncText = L.F("웹진을 읽지 못했어요: {0}", ex.Message); }
    }

    private void ApplyFilters()
    {
        var items = FilteredBase().ToList();
        int aiCount = items.Count(i => i.IsAiConnector);
        int webzineCount = items.Count(i => i.IsWebzine);

        // 크레이트 칩: 전체 · (AI 제안) · 장르 칸 순서 · 미분류
        var names = CrateOrder.Concat(items.Select(i => i.CrateName)).Distinct().ToList();
        var keys = new List<string> { Labels.All };
        if (aiCount > 0) keys.Add(AiChip);
        if (webzineCount > 0) keys.Add(WebzineChip);
        keys.AddRange(names);
        if (!CrateChips.Select(c => c.Key).SequenceEqual(keys))
        {
            CrateChips.Clear();
            // 키(크레이트 이름·칩 이름)는 순서 저장·비교에 쓰니 그대로, 보이는 이름만 번역
            foreach (var k in keys)
                CrateChips.Add(new FilterChip(k, L.T(k), SelectCrate) { Mover = k == Labels.All || k == AiChip || k == WebzineChip || k == Labels.Unsorted ? null : MoveCrate });
        }
        if (!keys.Contains(_crate)) _crate = Labels.All;
        foreach (var c in CrateChips)
            c.Count = c.Key == Labels.All ? items.Count : c.Key == AiChip ? aiCount : c.Key == WebzineChip ? webzineCount
                : items.Count(i => i.CrateName == c.Key);
        SyncChips();

        bool aiOnly = _crate == AiChip;
        bool webzineOnly = _crate == WebzineChip;
        bool all = _crate == Labels.All;
        Sections.Clear();
        foreach (var g in items.Where(i => (!aiOnly || i.IsAiConnector) && (!webzineOnly || i.IsWebzine)).GroupBy(i => i.CrateName)
                     .OrderBy(g => g.Key == Labels.Unsorted ? 99 : names.IndexOf(g.Key)))
        {
            if (!all && !aiOnly && !webzineOnly && g.Key != _crate) continue;
            // 라이브러리 뮤지션 → AI 제안 → 추가 제안 순
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
            // 고르는 건 위 순서(점수), 보여 주는 건 발매일 최신순. 레시피 하나만 볼 때는 전부 최신순이라 60장씩도 최신부터
            // (같은 날이면 위 순서 그대로 — 정렬이 안정적이다)
            shown = [.. shown.OrderByDescending(i => i.Rec.ReleaseDate)];
            int hidden = 0;
            if (!all && shown.Count > _pageSize)
            {
                // 수록곡 창에 열린 카드는 잘리지 않게 (재생 중인 앨범으로 이동할 때)
                var page = shown.Take(_pageSize).ToList();
                if (Detail is { } d && shown.Contains(d) && !page.Contains(d)) page.Add(d);
                hidden = shown.Count - page.Count;
                shown = page;
            }
            Sections.Add(new CrateSection(g.Key, shown, list.Count, mine, all && list.Count > 6, SelectCrate, hidden, ShowMoreCards));
        }
        RecCount = items.Count;
        EmptyText = IsBusy ? "" : items.Count == 0
            ? (_all.Count == 0 ? L.T("이 기간에 찾은 발매작이 없어요.") : L.T("이 조건에 맞는 발매작이 없어요. 형식·지역 필터를 넓혀 보세요."))
            : "";
        _ = FillCoversAsync(Sections.SelectMany(s => s.Items));
    }

    // ---- 커버가 없는 카드: 화면에 그린 것만 따로 찾는다 (발매 전 앨범은 Deezer 목록에 커버가 아직 없다) ----
    /// <summary>이번 실행에서 찾아 본 결과 (null = 찾는 중이거나 못 찾음 → 이번 실행에서는 다시 찾지 않는다)</summary>
    private readonly Dictionary<long, string?> _foundCovers = [];

    private async Task FillCoversAsync(IEnumerable<ReleaseItem> items)
    {
        var todo = items.Where(i => i.CoverUrl == null).Select(i => i.Rec).DistinctBy(r => r.DeezerAlbumId)
            .Where(r => !_foundCovers.ContainsKey(r.DeezerAlbumId)).ToList();
        if (todo.Count == 0) return;
        foreach (var r in todo) _foundCovers[r.DeezerAlbumId] = null;
        foreach (var r in todo)
        {
            try
            {
                if (await _artwork.FindReleaseCoverAsync(r.DeezerAlbumId, r.Artist, r.Album, _life.Token) is not { } url) continue;
                _foundCovers[r.DeezerAlbumId] = url;
                foreach (var i in _all.Concat(SavedSections.SelectMany(x => x.Items)).Concat(DislikedItems).Where(i => i.Rec.DeezerAlbumId == r.DeezerAlbumId))
                    i.FoundCover = url;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { _foundCovers.Remove(r.DeezerAlbumId); }
        }
        _artwork.Save();
    }

    // ---- 불러오기 -----------------------------------------------------------

    public async Task InitializeAsync()
    {
        IsBusy = true;
        BusyText = L.T("라이브러리 읽는 중…");
        var tracks = await _library.GetTracksAsync(_life.Token);
        _owned = new OwnedLibrary(tracks);
        BuildFileIndex(tracks);
        WatchLibrary();
        _ = StartSoundAsync();
        _ = BuildLabelAffinityAsync(tracks);
        BuildTasteExtras(tracks);
        await LoadAsync();
    }

    private async Task StartSoundAsync()
    {
        if (_sound == null)
        {
            ModelDevice = L.T("꺼짐");
            ModelTitle = L.T("소리 분석을 쓸 수 없음");
            ModelSub = L.T("ml 폴더나 uv 를 찾지 못했어요. 아티스트 연결로만 추천합니다.");
            return;
        }
        try
        {
            ModelSub = L.T("소리 분석 서비스 시작 중…");
            await _sound.StartAsync(_life.Token);
            await SyncFavoritesAsync();
            await RefreshStatusAsync();
            _ = RefreshWebzinesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ModelDevice = L.T("오류");
            ModelSub = L.F("소리 분석 서비스를 시작하지 못했어요: {0}", ex.Message);
        }
    }

    private async Task<CandidatePool> PoolAsync(IProgress<string> progress, CancellationToken ct)
    {
        const string key = "all";
        await _poolLock.WaitAsync(ct);
        try
        {
            if (_pools.TryGetValue(key, out var p) && DateTime.Now - p.BuiltAt < TimeSpan.FromHours(12)
                && _poolFavVersion.GetValueOrDefault(key) == FavoritesVersion) return p;
            var tracks = await _library.GetTracksAsync(ct);
            var seeds = TasteProfile.FromLibrary(tracks);
            // 좋아요(앨범·곡 하트)한 아티스트도 출발점으로 (라이브러리 시드와 겹치면 라이브러리 쪽을 둔다)
            var savedSeeds = _likedAlbums.All.Select(s => s.Rec.Artist).Concat(_likedTracks.All.Select(t => t.Artist))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(a => seeds.All(x => !string.Equals(x.Artist, a, StringComparison.OrdinalIgnoreCase)))
                .Select(a => new TasteArtist(a, 0.9, 0, "좋아요"));
            seeds = [.. seeds, .. savedSeeds];
            // 팔로우한 아티스트는 가중치 최대로 출발점에
            seeds = [.. seeds, .. _follows.All.Where(f => seeds.All(x => !string.Equals(x.Artist, f.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(f => new TasteArtist(f.Name, 1.0, 0, FollowList.Tag))];
            _mine ??= TasteProfile.LibraryArtists(tracks);
            _likedArtists.UnionWith(_mine.Select(m => ListeningAnalyzer.NormalizeArtist(m.Artist)));  // 피처링 가산점용
            p = await _recommender.BuildPoolAsync(seeds, _mine, RecOptions, progress, ct);
            _pools[key] = p;
            _poolFavVersion[key] = FavoritesVersion;
            if (p.MinePending > 0 && _mineRefresh == null) _mineRefresh = RefreshMineAsync(_mine);
            return p;
        }
        finally { _poolLock.Release(); }
    }

    /// <summary>라이브러리 뮤지션 디스코그래피를 뒤에서 모으고, 끝나면 지금 보는 기간을 다시 불러온다.</summary>
    private async Task RefreshMineAsync(IReadOnlyList<PlaylistArtist> mine)
    {
        var progress = new Progress<string>(m => SyncText = L.F("내 라이브러리 뮤지션 발매작 모으는 중… {0}",
            m.Replace("라이브러리 뮤지션 ", "").Replace(L.T("라이브러리 뮤지션 "), "")));
        try
        {
            _mineSyncText = true;
            SyncText = L.F("내 라이브러리 뮤지션 {0:N0}명의 발매작 모으는 중…", mine.Count);
            await Task.Run(() => _recommender.RefreshMineAsync(mine, progress, _life.Token));
            SyncText = "";
            _mineSyncText = false;
            await _poolLock.WaitAsync(_life.Token);
            try { _pools.Clear(); } finally { _poolLock.Release(); }
            if (!IsBusy) await LoadAsync(force: true);  // 새로 모은 라이브러리 뮤지션 신보 반영
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SyncText = L.F("라이브러리 뮤지션 발매작을 모으지 못했어요: {0}", ex.Message); }
        finally { _mineRefresh = null; }
    }

    /// <summary>SyncText에 지금 라이브러리 뮤지션 수집 문구(또는 그 오류)가 떠 있다 → 레이블 진행 문구로 덮지 않는다</summary>
    private bool _mineSyncText;

    // ---- 추천 결과 저장: 한 번 계산한 기간은 다음에 바로 꺼낸다 ----
    private readonly JsonCache<List<ReleaseRecommendation>> _recResults = new("rec-results", TimeSpan.FromDays(90));

    // ---- Apple Music: iTunes 검색으로 앨범 주소를 찾아 iTunes 앱에서 연다 (못 찾으면 Apple Music 검색) ----
    private ITunesSearchClient _itunes = null!;
    private readonly JsonCache<string> _appleLinks = new("apple-links", TimeSpan.FromDays(30));

    private async Task OpenAppleMusicAsync(ReleaseItem item)
    {
        var key = item.Rec.DeezerAlbumId.ToString();
        if (!_appleLinks.TryGet(key, out var url))
        {
            url = await _itunes.FindAlbumLinkAsync(item.Artist, item.Album, _life.Token);
            // 발매 전이라 아직 없을 수 있으니 찾은 것만 저장한다
            if (url != null) { _appleLinks.Set(key, url); _appleLinks.Save(); }
        }
        AppleMusic.Open(url ?? _itunes.SearchLink(item.Artist, item.Album));
    }

    // ---- NEW: 처음 보는 앨범 (어느 기간에서든 화면에 한 번 나오면 본 것) ----
    private readonly JsonCache<List<long>> _seenStore = new("seen-albums", TimeSpan.FromDays(36500));
    private HashSet<long>? _seen;
    /// <summary>이번 실행에서 처음 본 앨범: 앱을 끌 때까지 NEW 배지를 단다</summary>
    private readonly HashSet<long> _newThisRun = [];

    private HashSet<long> Seen
    {
        get
        {
            if (_seen != null) return _seen;
            if (_seenStore.TryGet("ids", out var ids)) return _seen = [.. ids];
            // 이 기능을 처음 켰을 때: 지금까지 저장된 결과는 모두 본 것으로 (NEW가 한꺼번에 쏟아지지 않게)
            _seen = [.. _recResults.FreshValues.SelectMany(l => l).Concat(_aiPicks.FreshValues.SelectMany(l => l))
                .Concat(AllWebzinePicks()).Select(r => r.DeezerAlbumId)];
            SaveSeen();
            return _seen;
        }
    }

    private void SaveSeen()
    {
        _seenStore.Set("ids", [.. _seen!]);
        _seenStore.Save();
    }

    /// <summary>화면에 나온 카드를 본 것으로 적는다. 처음 본 건 NEW (달력의 '처음 보는 앨범' 점도 지운다)</summary>
    private void MarkSeen()
    {
        var seen = Seen;
        bool changed = false;
        foreach (var i in _all)
        {
            if (seen.Add(i.Rec.DeezerAlbumId)) { _newThisRun.Add(i.Rec.DeezerAlbumId); changed = true; }
            i.IsNew = _newThisRun.Contains(i.Rec.DeezerAlbumId);
        }
        if (!changed) return;
        SaveSeen();
        UpdateAiMarks();
    }
    /// <summary>분석 이력 (달력 표시용): 기간마다 마지막 분석 시각과 추천 장수</summary>
    private readonly JsonCache<RecHistory> _recHistory = new("rec-history", TimeSpan.FromDays(3650));

    /// <summary>
    /// 설정 → 저장된 추천 비우기: 모델·설정이 바뀐 뒤 예전 추천을 버리고 지금 기간부터 새로 계산한다.
    /// 지우는 것: 기간별 추천 결과·분석 이력(달력 회색 선). 남기는 것: AI·웹진 제안, 좋아요·별로·차단·팔로우, 소리 분석 캐시(다시 듣지 않아 빠르다).
    /// </summary>
    public async Task ClearSavedRecsAsync()
    {
        _recResults.Clear();
        _recResults.Save();
        _recHistory.Clear();
        _recHistory.Save();
        await _poolLock.WaitAsync(_life.Token);
        try { _pools.Clear(); } finally { _poolLock.Release(); }
        UpdateAiMarks();
        await LoadAsync(force: true);
    }
    public sealed record RecHistory(PeriodKind Kind, DateTime Start, DateTime End, DateTime At, int Count);
    [ObservableProperty] private string _recSavedNote = "";
    public bool HasRecSaved => RecSavedNote.Length > 0;
    partial void OnRecSavedNoteChanged(string value)
    {
        OnPropertyChanged(nameof(HasRecSaved));
        OnPropertyChanged(nameof(PeriodRange));
    }

    /// <summary>기간 · 추천 설정마다 따로 저장 (설정을 바꾸면 결과가 달라지니까). 가운데 "all" 은 지역 칩이 있던 때의 자리 (저장 결과를 계속 쓰게 둔다)</summary>
    private string RecKey(Period period) =>
        $"{period.Label}|all|{_recPrefs.Amount}{_recPrefs.Adventure}{(_recPrefs.ExcludeKnown ? 1 : 0)}{(_recPrefs.ExcludeInstrumental ? "" : "i")}{(_recPrefs.Tracks == 1 ? "h" : "")}";

    /// <summary>
    /// 지난 기간은 저장 결과를 그대로 쓴다. 끝난 지 7일이 안 된 기간은 신보·디스코그래피가 계속 바뀌니 잠깐만 쓴다 (FreshFor).
    /// 유효 시간이 지난 결과도 버리지 않는다 — 열 때 먼저 보여 주고 뒤에서 새로 계산한다 (LoadAsync).
    /// </summary>
    private bool TryGetSavedRecs(string key, Period period, out List<ReleaseRecommendation> recs, out DateTime at)
    {
        if (!_recResults.TryGetAt(key, out recs, out at)) return false;
        bool recent = period.End >= DateTime.Today.AddDays(-7);
        return !recent || DateTime.Now - at < FreshFor(period.Kind);
    }

    /// <summary>
    /// 진행 중인 기간의 저장 결과 유효 시간: 주 12시간, 월 하루, 연 3일.
    /// 넓은 기간일수록 다시 계산이 오래 걸리고(연은 앨범 만 장 안팎) 하루 사이 바뀌는 비율은 작다.
    /// </summary>
    private static TimeSpan FreshFor(PeriodKind kind) => kind switch
    {
        PeriodKind.Year => TimeSpan.FromDays(3),
        PeriodKind.Month => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(12),
    };

    /// <summary>
    /// 주·월을 열 때 그 기간을 품은 월·연 결과가 저장돼 있으면 그 기간 발매작만 잘라 온다 (같은 추천 설정일 때).
    /// 연·월 결과는 기간 전체에서 순위를 매긴 것이라, 그 주만 더 촘촘하게 보려면 "다시 분석".
    /// </summary>
    private bool TryGetWiderRecs(Period period, out List<ReleaseRecommendation> recs, out DateTime at, out string label,
        bool allowStale = false)
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
            var wKey = RecKey(w);
            if (!(allowStale ? _recResults.TryGetAt(wKey, out var all, out var t) : TryGetSavedRecs(wKey, w, out all, out t))) continue;
            recs = [.. all.Where(r => period.Contains(r.ReleaseDate))];
            at = t;
            label = w.Display;  // 표시용 (Label은 캐시 키)
            return true;
        }
        return false;
    }

    // ---- 정밀 분석: 연은 월 단위로, 월은 주 단위로 하나씩 분석해 저장 → 넓은 기간 보기는 그걸 합친다 ----
    public bool CanPrecise => Mode != PeriodKind.Week;
    public string PreciseLabel => Mode == PeriodKind.Year ? L.T("연 정밀 분석") : L.T("월 정밀 분석");
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
    private bool TryGetPrecise(Period period, out List<ReleaseRecommendation> recs, out DateTime latest)
    {
        recs = [];
        latest = default;
        var parts = PartsOf(period);
        if (parts.Count == 0) return false;
        var all = new List<ReleaseRecommendation>();
        foreach (var part in parts)
        {
            if (!TryGetSavedRecs(RecKey(part), part, out var got, out var at)) return false;
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
        var parts = PartsOf(period);
        bool yearly = period.Kind == PeriodKind.Year;
        var title = yearly ? L.T("연 정밀 분석") : L.T("월 정밀 분석");
        _preciseCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var ct = _preciseCts.Token;
        IsPrecise = true;
        int done = 0, skipped = 0;
        try
        {
            _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
            var pool = await PoolAsync(new Progress<string>(m => PreciseText = L.F("{0} 준비 · {1}", title, m)), ct);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var key = RecKey(part);
                if (TryGetSavedRecs(key, part, out _, out _)) { skipped++; continue; }
                var head = $"{title} {i + 1}/{parts.Count} · {part.Display}";
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
            PreciseText = L.F(yearly ? "{0} 완료 · 새로 {1}개월" : "{0} 완료 · 새로 {1}주", title, done)
                + (skipped > 0 ? " " + L.F(yearly ? "(이미 있던 {0}개월은 건너뜀)" : "(이미 있던 {0}주는 건너뜀)", skipped) : "");
            // 지금 그 기간을 보고 있으면 합친 결과로 다시 그린다
            if (CurrentPeriod.Label == period.Label) await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            PreciseText = L.F(yearly ? "{0} 멈춤 · {1}개월 저장됨 (다시 누르면 이어서)" : "{0} 멈춤 · {1}주 저장됨 (다시 누르면 이어서)", title, done);
        }
        catch (Exception ex) { PreciseText = L.F("{0} 실패: {1}", title, ex.Message); }
        finally { IsPrecise = false; }
    }

    /// <summary>"다시 분석": 저장된 결과를 무시하고 지금 다시 계산</summary>
    [RelayCommand]
    private Task Reanalyze() => LoadAsync(force: true);

    /// <summary>AI 결과 + 웹진 추천 (같은 앨범이면 배지를 합친다)</summary>
    private List<ReleaseRecommendation> SavedPicksFor(Period period) =>
        [.. AiPicksFor(period).Concat(WebzinePicksFor(period)).GroupBy(r => r.DeezerAlbumId).Select(g => g.Aggregate(MergeAi))];

    // ---- 계산 멈추기·뒤에서 새로 계산 ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowProgress))] private bool _isRefreshing;
    /// <summary>진행 막대와 '멈추기': 처음 계산 중이거나, 저장된 결과를 보여 준 채 뒤에서 새로 계산 중</summary>
    public bool ShowProgress => IsBusy || IsRefreshing;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowProgress));

    /// <summary>계산을 멈춘다. 처음 계산이었으면 AI·웹진 제안만 남기고, 뒤에서 새로 계산 중이었으면 보던 저장 결과를 그대로 둔다</summary>
    [RelayCommand]
    private void StopLoad()
    {
        bool first = IsBusy;
        _loadCts?.Cancel();
        IsBusy = false;
        IsRefreshing = false;
        if (first)
        {
            _all = [.. SavedPicksFor(CurrentPeriod).Select(r => MakeItem(r))];
            ApplyFilters();
            BusyText = L.T("멈췄어요 · '다시 분석'을 누르면 처음부터 다시 계산해요");
        }
        else BusyText = L.T("새로 계산을 멈췄어요 · 저장된 추천을 그대로 보여 줘요");
    }

    /// <summary>유효 시간이 지난 저장 결과를 보여 준 채 뒤에서 새로 계산하고, 끝나면 새 결과로 다시 그린다</summary>
    private async Task RefreshStaleAsync(Period period, string key, CancellationToken ct)
    {
        IsRefreshing = true;
        var progress = new Progress<string>(m => { if (!ct.IsCancellationRequested) BusyText = L.F("저장된 추천을 보여 주는 중 · 새로 계산: {0}", m); });
        try
        {
            _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
            var pool = await PoolAsync(progress, ct);
            var result = await _recommender.RecommendAsync(pool, _owned, period, RecOptions.ForPeriod(period.Kind), progress, ct);
            if (ct.IsCancellationRequested) return;
            // 소리 분석까지 된 결과만 저장 (아니면 보던 저장 결과를 그대로 둔다)
            if (!result.SoundScored || result.Releases.Count == 0)
            {
                BusyText = L.T("새로 계산하지 못해 저장된 추천을 그대로 보여 줘요");
                return;
            }
            _recResults.Set(key, [.. result.Releases]);
            _recHistory.Set(period.Label, new RecHistory(period.Kind, period.Start, period.End, DateTime.Now, result.Releases.Count));
            _recHistory.Save();
            _recResults.Save();
            IsRefreshing = false;
            BusyText = "";
            await LoadAsync();  // 이제 저장 결과가 새것이라 그대로 다시 그린다
            if (CurrentPeriod.Label == period.Label) _ = CheckDislikeMatchesAsync(period, key);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BusyText = L.F("새로 계산하지 못했어요: {0}", ex.Message); }
        finally { if (!ct.IsCancellationRequested) IsRefreshing = false; }
    }

    private async Task LoadAsync(bool force = false)
    {
        _loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _loadCts = cts;
        var ct = cts.Token;
        var period = CurrentPeriod;
        var progress = new Progress<string>(m => { if (!ct.IsCancellationRequested) BusyText = m; });
        var key = RecKey(period);
        string? freshKey = null;  // 새로 계산했으면 그 저장 키 — 별로·차단과 아주 닮은 추천을 확인한다
        bool refresh = false;     // 지난 저장 결과를 그렸다 → 끝에서 새로 계산
        var aiRecs = SavedPicksFor(period);
        if (aiRecs.Any(r => r.Taste == null && r.Crate == null && r.AiBy?.Any(b => !b.StartsWith("웹진", StringComparison.Ordinal)) == true))
            _ = RescoreAiAsync(period);
        UpdateAiSaved();

        IsBusy = true;
        IsRefreshing = false;  // 앞 기간의 뒤 계산은 위에서 취소됐다
        EmptyText = "";
        RecSavedNote = "";
        _ = LoadLibraryAsync(period, ct);

        // 저장된 AI 결과는 계산을 기다리지 않고 먼저 보여 준다
        if (aiRecs.Count > 0)
        {
            _all = [.. aiRecs.Select(r => MakeItem(r))];
            ApplyFilters();
        }
        try
        {
            List<ReleaseRecommendation> releases;
            bool soundScored = true;
            bool fresh = false;  // 이번에 새로 계산했다 (저장된 결과가 아니다)
            bool stale = false;  // 유효 시간이 지난 저장 결과를 보여 준다 → 그린 뒤 뒤에서 새로 계산
            if (!force && period.Kind != PeriodKind.Week && TryGetPrecise(period, out var precise, out var preciseAt))
            {
                releases = precise;
                RecSavedNote = L.F(period.Kind == PeriodKind.Year ? "월별 정밀 분석 합침 · {0:M.d HH:mm}" : "주별 정밀 분석 합침 · {0:M.d HH:mm}", preciseAt);
            }
            else if (!force && TryGetWiderRecs(period, out var wider, out var widerAt, out var widerLabel)
                && !TryGetSavedRecs(key, period, out _, out _))
            {
                // 이 주(달)를 따로 분석한 적은 없지만 그걸 품은 달·연 결과가 저장돼 있다 → 잘라서 바로
                releases = wider;
                RecSavedNote = L.F("{0} 분석에서 가져옴 · {1:M.d HH:mm}", widerLabel, widerAt);
            }
            else if (!force && TryGetSavedRecs(key, period, out var saved, out var savedAt))
            {
                releases = saved;
                RecSavedNote = L.F("저장된 추천 · {0:M.d HH:mm} 분석", savedAt);
                // 이력 기능 전에 저장한 결과도 달력에 표시되게
                if (!_recHistory.TryGet(period.Label, out _))
                {
                    _recHistory.Set(period.Label, new RecHistory(period.Kind, period.Start, period.End, savedAt, saved.Count));
                    _recHistory.Save();
                }
            }
            // 유효 시간이 지났어도 저장 결과가 있으면 기다리게 하지 않는다 (연 보기는 다시 계산이 몇 분 걸린다)
            else if (!force && _recResults.TryGetAt(key, out var old, out var oldAt))
            {
                releases = old;
                stale = true;
                RecSavedNote = L.F("저장된 추천 · {0:M.d HH:mm} 분석", oldAt);
            }
            else if (!force && TryGetWiderRecs(period, out var oldWider, out var oldWiderAt, out var oldLabel, allowStale: true))
            {
                releases = oldWider;
                stale = true;
                RecSavedNote = L.F("{0} 분석에서 가져옴 · {1:M.d HH:mm}", oldLabel, oldWiderAt);
            }
            else
            {
                _owned ??= new OwnedLibrary(await _library.GetTracksAsync(ct));
                var pool = await PoolAsync(progress, ct);
                var result = await _recommender.RecommendAsync(pool, _owned, period, RecOptions.ForPeriod(period.Kind), progress, ct);
                if (ct.IsCancellationRequested) return;
                releases = [.. result.Releases];
                soundScored = result.SoundScored;
                fresh = soundScored;
                // 소리 분석까지 된 결과만 저장 (소리 서비스가 늦게 뜬 경우는 다음에 다시 계산)
                if (soundScored && releases.Count > 0)
                {
                    _recResults.Set(key, releases);
                    _recHistory.Set(period.Label, new RecHistory(period.Kind, period.Start, period.End, DateTime.Now, releases.Count));
                    _recHistory.Save();
                    _recResults.Save();
                    RecSavedNote = L.F("방금 분석 · {0:M.d HH:mm}", DateTime.Now);
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
            await MergeFollowedAsync(period, ct);
            if (ct.IsCancellationRequested) return;
            if (fresh) freshKey = key;
            // 장르 DB에 다 못 물은 앨범은 뒤에서 태그를 받아 둔다 (다음 계산부터 칸이 정확해진다)
            if (fresh) _ = Task.Run(() => _recommender.WarmGenresAsync(releases, ct: _life.Token));
            IsBusy = false;
            BusyText = soundScored ? "" : L.T("소리 분석 없이 아티스트 연결로만 골랐어요");
            if (stale) refresh = true;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _all = [];
            IsBusy = false;
            BusyText = L.F("추천을 불러오지 못했어요: {0}", ex.Message);
        }
        ApplyFilters();
        UpdateHeat();
        if (!IsBusy) MarkSeen();
        if (freshKey != null) _ = CheckDislikeMatchesAsync(period, freshKey);
        else if (!IsBusy) RestorePendingDrops(key);
        // 기다리지 않는다 (재생 중 앨범으로 이동처럼 불러오기를 기다리는 곳이 몇 분씩 막히지 않게)
        if (refresh) _ = RefreshStaleAsync(period, key, ct);
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
                LibraryGroups.Add(new LibraryArtistGroup(a.Artist, L.F("이 기간 발매 {0}장 · 재생 {1}회", albums.Count, a.PlayCount), albums));
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
        Detail = item;  // 재생하면 상세 패널에 수록곡을 연다 (지금 어느 곡인지 보이게)
        if (!await EnsureTracksAsync(item)) return;
        target ??= item.Tracks.FirstOrDefault(t => t.HasPreview);
        if (target?.Preview is not { } url)
        {
            Player.Subtitle = $"{item.Album}: {(target == null ? L.T("미리듣기가 없는 앨범이에요") : L.T("이 곡은 미리듣기가 없어요"))}";
            return;
        }
        item.Current = target;
        Player.Play(new Uri(url), $"{target.Title} — {target.Artist}",
            L.F("{0} · {1}/{2}번 곡 · Deezer 미리듣기 30초", item.Album, target.Number, item.Tracks.Count), item.CoverUrl, target.SetPlaying,
            ended: () => PlayNextTrack(item, target), release: item);
    }

    // 30초 미리듣기가 끝나면 같은 앨범의 다음 곡으로 (앨범 훑어보기)
    private void PlayNextTrack(ReleaseItem item, TrackItem done)
    {
        var next = item.Tracks.SkipWhile(t => t != done).Skip(1).FirstOrDefault(t => t.HasPreview);
        if (next != null) _ = PlayReleaseAsync(item, next);
    }

    // ---- 앨범 상세 패널 (오른쪽): 수록곡을 카드 안에서 펼치면 그 줄만 길어져 목록이 들쭉날쭉해서 따로 연다 ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDetail))] private ReleaseItem? _detail;
    public bool HasDetail => Detail != null;

    /// <summary>열린 앨범의 카드는 ShowTracks 로 표시만 (수록곡 버튼이 주황)</summary>
    partial void OnDetailChanged(ReleaseItem? oldValue, ReleaseItem? newValue)
    {
        if (oldValue != null) oldValue.ShowTracks = false;
        if (newValue != null) newValue.ShowTracks = true;
        Mark(newValue ?? oldValue);
        // 창을 닫으면 카드 칸 수가 늘어 자리가 밀린다 → 닫은 카드가 화면 밖이면 그리로
        if (newValue == null && oldValue != null) RevealRequested?.Invoke(oldValue);
    }

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    // ---- 마지막으로 본 카드: 수록곡 창을 닫거나 관심 없음·차단으로 카드가 빠져도 어디까지 봤는지 남긴다 ----
    private ReleaseItem? _marked;
    /// <summary>그 카드가 화면 밖에 있을 때만 스크롤 (MainWindow가 받는다)</summary>
    public event Action<ReleaseItem>? RevealRequested;

    private void Mark(ReleaseItem? item)
    {
        if (_marked == item) return;
        if (_marked != null) _marked.IsMarked = false;
        _marked = item;
        if (item != null) item.IsMarked = true;
    }

    /// <summary>화면에 보이는 카드 순서 (추천 목록, 아니면 저장함)</summary>
    private List<ReleaseItem> VisibleOrder(bool saved) =>
        saved ? [.. SavedSections.SelectMany(s => s.Items)] : [.. Sections.SelectMany(s => s.Items)];

    /// <summary>관심 없음·차단으로 카드가 빠지면 그 자리를 이어받은 다음 카드로: 수록곡 창으로 보던 중이면 다음 앨범을 열고, 아니면 표시만</summary>
    private void KeepPlace(ReleaseItem item, Action change)
    {
        bool saved = !VisibleOrder(false).Contains(item);
        var before = VisibleOrder(saved);
        int i = before.IndexOf(item);
        bool wasOpen = Detail == item;
        change();
        if (i < 0) return;
        // 저장함은 다시 만들면 카드가 새 객체라 앨범 id 로 맞춘다
        var now = VisibleOrder(saved).DistinctBy(x => x.Rec.DeezerAlbumId).ToDictionary(x => x.Rec.DeezerAlbumId);
        if (now.ContainsKey(item.Rec.DeezerAlbumId)) return;
        // 같은 아티스트 카드가 같이 빠질 수 있어 남아 있는 것 중 바로 다음 (끝이면 바로 앞)
        ReleaseItem? Alive(ReleaseItem b) => now.GetValueOrDefault(b.Rec.DeezerAlbumId);
        var next = before.Skip(i + 1).Select(Alive).FirstOrDefault(x => x != null) ?? before.Take(i).Select(Alive).LastOrDefault(x => x != null);
        if (wasOpen)
        {
            if (next != null) _ = ToggleTracksAsync(next);
            else Detail = null;
        }
        Mark(next);
        if (next != null) RevealRequested?.Invoke(next);
    }

    /// <summary>오른쪽 창에서 ↑↓: 화면에 보이는 순서대로 이전·다음 앨범을 연다 (카드 목록으로 돌아가지 않고 넘겨 보기)</summary>
    public void StepDetail(int step)
    {
        if (Detail == null) return;
        var list = VisibleOrder(false);
        if (!list.Contains(Detail)) list = VisibleOrder(true);
        int i = list.IndexOf(Detail);
        if (i < 0 || i + step < 0 || i + step >= list.Count) return;
        var next = list[i + step];
        _ = ToggleTracksAsync(next);
        ScrollToRequested?.Invoke(next);
    }

    /// <summary>←→: 지금 재생 중인 앨범의 이전·다음 곡 (미리듣기가 있는 곡만)</summary>
    public void StepTrack(int step)
    {
        if (Player.NowRelease is not { Current: { } cur } item) return;
        var tracks = step > 0 ? item.Tracks.SkipWhile(t => t != cur).Skip(1) : item.Tracks.TakeWhile(t => t != cur).Reverse();
        if (tracks.FirstOrDefault(t => t.HasPreview) is { } next) _ = PlayReleaseAsync(item, next);
    }

    /// <summary>Space: 듣던 것이 있으면 재생/일시정지, 없으면 열린 앨범을 튼다</summary>
    public void TogglePlay()
    {
        if (Player.HasMedia) Player.Toggle();
        else if (Detail != null) _ = PlayReleaseAsync(Detail, null);
    }

    private async Task ToggleTracksAsync(ReleaseItem item)
    {
        if (Detail == item) { Detail = null; return; }
        Detail = item;
        await EnsureTracksAsync(item);
    }

    /// <summary>수록곡을 받는다. 미리듣기 URL은 몇 시간 뒤 만료되므로 30분이 지났으면 새로 받는다.</summary>
    private async Task<bool> EnsureTracksAsync(ReleaseItem item)
    {
        // 30분 안이면 그대로. 단 미리듣기가 빈 곡이 있으면 다시 열 때마다(1분 간격) 다시 받아 본다 (발매 직후에는 곡마다 미리듣기가 늦게 붙는다)
        var age = DateTime.Now - item.TracksLoadedAt;
        if (item.Tracks.Count > 0 && age < TimeSpan.FromMinutes(30)
            && (item.Tracks.All(t => t.HasPreview) || age < TimeSpan.FromMinutes(1))) return true;
        bool first = item.Tracks.Count == 0;
        if (first) item.TracksStatus = L.T("수록곡 불러오는 중…");
        try
        {
            var tracks = await _recommender.AlbumTracksAsync(item.Rec.DeezerAlbumId);
            item.SetTracks(tracks);
            item.TracksStatus = tracks.Count == 0 ? L.T("수록곡 정보가 없어요") : null;
            _ = LoadTrackNearAsync(item);
            return tracks.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            item.TracksStatus = L.T("수록곡을 불러오지 못했어요");
            return !first;
        }
    }

    /// <summary>수록곡마다 비슷한 내 곡 (곡 미리듣기를 소리 서비스가 듣는다, 한 번 들은 곡은 캐시)</summary>
    private readonly JsonCache<List<TrackPair>> _trackNear = new("track-near", TimeSpan.FromDays(365));

    private async Task LoadTrackNearAsync(ReleaseItem item)
    {
        if (_sound == null) return;
        var tracks = item.Tracks.Where(t => t.Id != 0 && t.HasPreview).ToList();
        if (tracks.Count == 0) return;
        try
        {
            var near = await _sound.TrackNearAsync(tracks.Select(t => (t.Id, t.Preview)), _life.Token);
            if (near.Count == 0) return;
            // 많이 닮은 곡만: 가장 닮은 곡과 0.03 이내, 최대 3곡, 내 곡이 겹치면 한 번만.
            // 카드의 '비슷한 내 곡'도 같은 결과로 바꿔 둘이 어긋나지 않게 한다 (전에는 카드는 앞 3곡을 섞은 평균 기준이라 달랐다)
            double best = near.Values.Max(v => v.Sim);
            var top = new List<KeyValuePair<long, (string Near, double Sim)>>();
            var mine = new HashSet<string>();
            foreach (var kv in near.OrderByDescending(kv => kv.Value.Sim))
            {
                if (kv.Value.Sim < best - 0.03 || top.Count == 3) break;
                if (mine.Add(kv.Value.Near)) top.Add(kv);
            }
            var shown = top.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var t in item.Tracks)
            {
                t.Near = shown.TryGetValue(t.Id, out var n) ? n.Near : null;
                t.IsClosest = t.Near != null && n.Sim == best && near.Count > 1;
            }
            var titleOf = item.Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Title);
            List<TrackPair> pairs = [.. top.Select(kv => new TrackPair(titleOf.GetValueOrDefault(kv.Key, ""), kv.Value.Near, kv.Value.Sim))];
            _trackNear.Set(item.Rec.DeezerAlbumId.ToString(), pairs);
            _trackNear.Save();
            item.NearItems = [.. pairs.Select(p => new NearTrackItem(p.Near, FindLocalFile(p.Near), PlayNear))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }  // 서비스가 바쁘거나 꺼져 있으면 표시하지 않는다
    }

    // ---- 비슷한 내 곡: "아티스트 – 곡명" → 내 라이브러리 파일 ----
    private Dictionary<string, string>? _fileByTitle;

    private static string FileKey(string artist, string title) =>
        ListeningAnalyzer.NormalizeArtist(artist) + "|" + ListeningAnalyzer.NormalizeArtist(title);

    private void BuildFileIndex(IEnumerable<TrackRecord> tracks)
    {
        var map = new Dictionary<string, string>();
        foreach (var t in tracks)
        {
            if (t.Location is not { } path) continue;
            map.TryAdd(FileKey(t.GroupArtist, t.Title), path);
            map.TryAdd(FileKey(t.Artist, t.Title), path);
        }
        _fileByTitle = map;
    }

    private string? FindLocalFile(string near)
    {
        if (_fileByTitle == null) return null;
        var dash = near.IndexOf(" – ", StringComparison.Ordinal);
        if (dash < 0) return null;
        var path = _fileByTitle.GetValueOrDefault(FileKey(near[..dash], near[(dash + 3)..]));
        return path != null && File.Exists(path) ? path : null;
    }

    private void PlayNear(NearTrackItem item) => _ = PlayNearAsync(item);

    /// <summary>비슷한 내 곡: 내 파일로 (전곡). 파일이 없으면 Deezer 에서 같은 곡을 찾아 30초 미리듣기로</summary>
    private async Task PlayNearAsync(NearTrackItem item)
    {
        if (Player.IsCurrent(item.SetPlaying)) { Player.Toggle(); return; }
        var dash = item.Label.IndexOf(" – ", StringComparison.Ordinal);
        var (artist, title) = dash > 0 ? (item.Label[..dash], item.Label[(dash + 3)..]) : ("", item.Label);
        if (item.Path is { } path)
        {
            Player.Play(new Uri(path), $"{title} — {artist}", L.T("비슷한 내 곡 · 내 파일 (전곡)"), null, item.SetPlaying);
            return;
        }
        try
        {
            var hit = await _deezer.FindTrackAsync(artist, title, _life.Token);
            if (hit?.Preview is not { Length: > 0 } preview)
            {
                Player.Subtitle = L.F("{0}: 내 파일도 Deezer 미리듣기도 찾지 못했어요", item.Label);
                return;
            }
            Player.Play(new Uri(preview), $"{title} — {artist}", L.T("비슷한 내 곡 · Deezer 미리듣기 30초 (내 PC에 파일이 없어요)"), null, item.SetPlaying);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Player.Subtitle = L.F("{0}: 미리듣기를 불러오지 못했어요", item.Label);
        }
    }

    private Task PlayLibraryAsync(LibraryAlbumItem item)
    {
        if (item.Album.SampleTrackPath is not { } path) return Task.CompletedTask;
        if (Player.IsCurrent(item.SetPlaying)) { Player.Toggle(); return Task.CompletedTask; }
        Player.Play(new Uri(path), $"{item.Album.SampleTrackTitle} — {item.Album.Artist}", L.F("{0} · 내 파일", item.Title),
            item.ArtUrl, item.SetPlaying);
        return Task.CompletedTask;
    }

    // ---- 내 취향 모델 / 자동 재학습 -----------------------------------------

    [ObservableProperty] private string _modelTitle = L.T("내 취향 모델 확인 중…");
    [ObservableProperty] private string _modelSub = "";
    [ObservableProperty] private string _modelDevice = "";
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _updateProgressText = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _autoUpdate = true;

    /// <summary>설정 창에서 자동 분석을 켜면 밀린 새 곡부터 바로 분석</summary>
    partial void OnAutoUpdateChanged(bool value)
    {
        if (_settings.AutoRetrain != value) { _settings.AutoRetrain = value; _settings.Save(); }
        if (value && HasUpdate && !IsUpdating) _ = RunUpdateAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_sound == null) return;
        var s = await _sound.StatusAsync(_life.Token);
        IsTrained = s.Trained;
        if (!s.Trained)
        {
            // 처음 설치: '분석 시작'을 보인다 (그때까지는 아티스트 연결만으로 추천)
            ModelDevice = s.Gpu != null ? "GPU" : s.Device.ToUpperInvariant();
            ModelTitle = L.T("아직 소리 분석 전");
            // 걸리는 시간: 남은 곡 기준 (GPU 로 1분에 250곡 안팎, CPU 는 훨씬 느리다)
            int minutes = Math.Max(1, (int)Math.Ceiling(s.NewTracks / 250.0));
            ModelSub = s.Gpu != null
                ? L.F("라이브러리 곡을 모두 들어 내 소리 기준을 만들어요. 처음 한 번, {0:N0}곡 · GPU로 {1}분 안팎", s.NewTracks, minutes)
                : L.F("라이브러리 곡을 모두 들어 내 소리 기준을 만들어요. 처음 한 번, {0:N0}곡 · GPU가 없어 오래 걸려요", s.NewTracks);
            UpdateText = s.Embedded > 0 ? L.F("분석해 둔 {0:N0}곡에서 이어서 해요", s.Embedded) : "";
            HasUpdate = true;
            ApplyFilters();
            return;
        }
        RebuildSaved();
        ModelDevice = s.Gpu != null ? "GPU" : s.Device.ToUpperInvariant();
        ModelTitle = L.F("내 곡 {0:N0}곡과 비교", s.Compared);
        // 하트·재생 수가 바뀐 것은 서비스가 라이브러리를 다시 읽을 때 곡 가중치에 바로 들어간다 (다시 분석할 것 없음)
        ModelSub = L.F("분석 {0:N0}곡 · 오래 안 들은 {1:N0}곡은 빼고 비교 · {2:yyyy.M.d}", s.Embedded, Math.Max(0, s.Embedded - s.Compared), s.TrainedAt);
        HasUpdate = s.NewTracks > 0;
        UpdateText = s.NewTracks > 0 ? L.F("iTunes에 새 곡 {0:N0}곡", s.NewTracks) : "";
        ApplyFilters();
        if (HasUpdate && AutoUpdate && !IsUpdating && !_autoFailed) await RunUpdateAsync();
    }

    /// <summary>새 곡 분석이 실패했으면 iTunes 라이브러리가 다시 바뀔 때까지 자동으로 다시 하지 않는다 (같은 실패를 되풀이하지 않게)</summary>
    private bool _autoFailed;

    /// <summary>라이브러리 소리를 분석해 두었다 (false 면 '지금 업데이트' 버튼이 '분석 시작')</summary>
    [ObservableProperty] private bool _isTrained = true;

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
            bool first = !IsTrained;
            var r = await _sound.UpdateAsync(progress, _life.Token);
            UpdateProgressText = first ? L.F("분석 완료 · {0:N0}곡", r.Added) : L.F("새 곡 {0}곡 반영", r.Added);
            _library.Invalidate();
            _owned = new OwnedLibrary(await _library.GetTracksAsync(_life.Token));
            _pools.Clear();  // 많이 듣는 아티스트가 바뀌었을 수 있으니 시드부터 다시
            _mine = null;
            if (first) _ = LoadAsync();  // 이제 소리로 판정한다
            _autoFailed = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateProgressText = L.F("업데이트 실패: {0}", ex.Message);
            _autoFailed = true;
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
            _autoFailed = false;
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
