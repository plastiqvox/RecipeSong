using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecipeSong.Core;

namespace RecipeSong.Wpf.ViewModels;

/// <summary>추천 카드 한 장.</summary>
public sealed partial class ReleaseItem : ObservableObject
{
    private readonly Func<ReleaseItem, TrackItem?, Task> _play;

    /// <param name="play">track이 null이면 커버 재생 버튼: 듣던 곡 이어서 / 첫 곡</param>
    public ReleaseItem(ReleaseRecommendation r, Func<ReleaseItem, TrackItem?, Task> play, Func<ReleaseItem, Task> toggleTracks,
        Action<ReleaseItem> toggleSave, bool saved)
    {
        Rec = r;
        _play = play;
        _isSaved = saved;
        PlayCommand = new AsyncRelayCommand(() => play(this, null));
        ToggleTracksCommand = new AsyncRelayCommand(() => toggleTracks(this));
        ToggleSaveCommand = new RelayCommand(() => toggleSave(this));
        int dots = r.CrateFit is double p ? Math.Clamp((int)Math.Round(p * 5), 1, 5) : 0;
        FitDots = [.. Enumerable.Range(0, 5).Select(i => i < dots)];
    }

    public ReleaseRecommendation Rec { get; }
    public IAsyncRelayCommand PlayCommand { get; }

    public string Album => Rec.Album;
    public string Artist => Rec.Artist;
    public string? CoverUrl => Rec.CoverUrl;
    public string TypeLabel => Rec.RecordType.ToUpperInvariant();
    public string Meta => string.Join(" · ", new[] { Rec.ReleaseDate.ToString("yyyy.M.d"), Rec.Label, RegionLabel }.Where(s => !string.IsNullOrEmpty(s)));
    /// <summary>표시용 지역 이름 (Labels.Region은 키로도 쓰이므로 여기서만 번역)</summary>
    public string RegionLabel => L.T(Labels.Region(Rec.Region));
    public string CrateName => Rec.Crate ?? Labels.Unsorted;
    /// <summary>표시용 레시피 이름 (CrateName은 그룹·필터 키라 번역하지 않는다)</summary>
    public string CrateLabel => L.T(CrateName);
    public bool HasFit => Rec.CrateFit != null;
    public string FitText => Rec.CrateFit is double p ? L.F("{0} 어울림 {1:P0}", L.T(Rec.Crate ?? ""), p) : "";
    public bool[] FitDots { get; }
    public bool Known => Rec.IsKnownArtist;
    public bool IsMine => Rec.FromPlaylist != null;
    /// <summary>저장함 앨범과 특히 닮았을 때: 가장 닮은 저장 앨범</summary>
    public string? FavNear => Rec.FavNear;
    /// <summary>좋아하는 레이블에서 나온 신보면 그 레이블</summary>
    public string? LikedLabel => Rec.LikedLabel;
    /// <summary>수록곡에 참여한 좋아하는 아티스트</summary>
    public string? WithLiked => Rec.WithLiked;
    public bool HasWithLiked => !string.IsNullOrEmpty(Rec.WithLiked);
    public bool HasLikedLabel => !string.IsNullOrEmpty(Rec.LikedLabel);
    public bool HasFavNear => !string.IsNullOrEmpty(Rec.FavNear);
    public bool IsAi => Rec.AiReason != null;
    /// <summary>웹진 RSS에서 온 추천</summary>
    public bool IsWebzine => Rec.AiBy?.Any(b => b.StartsWith("웹진", StringComparison.Ordinal)) == true;
    /// <summary>AI 커넥터(Claude·GPT)가 찾은 추천</summary>
    public bool IsAiConnector => Rec.AiBy?.Any(b => !b.StartsWith("웹진", StringComparison.Ordinal)) == true;
    public bool HasSource => !string.IsNullOrEmpty(Rec.SourceUrl);

    /// <summary>추천 이유를 누르면 원문(웹진 리뷰·AI가 확인한 페이지)을 연다</summary>
    [RelayCommand]
    private void OpenSource()
    {
        if (Rec.SourceUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    /// <summary>추천 이유. 웹진의 'OO에서 소개'는 보라 배지와 겹쳐서 뺀다 (AI 이유와 합쳐진 경우 그 부분만)</summary>
    public string? AiReason => IsRecovered ? L.T(AiPick.RecoveredReason)
        : Rec.AiReason?.Split("  /  ").Where(r => !r.EndsWith("에서 소개", StringComparison.Ordinal)).ToList() is { Count: > 0 } rs
        ? string.Join("  /  ", rs) : null;
    /// <summary>로그에서 다시 불러온 AI 제안: 이유가 없다는 안내라 흐리게 보인다</summary>
    public bool IsRecovered => Rec.AiReason == AiPick.RecoveredReason;
    public bool HasAiReason => AiReason != null;
    /// <summary>제안한 AI 커넥터 ("Claude", "GPT") — 커넥터마다 배지 하나</summary>
    public IReadOnlyList<AiBadge> AiBy => [.. (Rec.AiBy ?? (IsAi ? ["AI"] : [])).Select(b => new AiBadge(b))];
    // 내 플리 배지는 플레이리스트 이름만 (앞의 플레이리스트 아이콘이 '내 플리'를 뜻한다)
    public string Badge => Rec.FromPlaylist == FollowList.Tag ? L.T("팔로우")
        : IsMine ? L.T(Rec.FromPlaylist ?? "") : IsAi ? "" : Known ? L.T("아는 아티스트") : "";
    public bool BadgeIsPlaylist => IsMine && Rec.FromPlaylist != FollowList.Tag;
    public string BadgeTip => Rec.FromPlaylist == FollowList.Tag ? L.T("팔로우한 아티스트의 신보")
        : IsMine ? L.F("{0} 플레이리스트에 있는 뮤지션의 신보", L.T(Rec.FromPlaylist ?? ""))
        : IsAi ? L.T("AI 커넥터가 웹에서 찾고 Deezer에서 확인한 신보") : L.T("내 라이브러리에 있는 아티스트");
    public System.Windows.Thickness BadgeMargin => HasFit ? new(8, 0, 0, 0) : new(0);
    public bool HasNear => NearItems.Count > 0;
    /// <summary>장르 태그 (EffNet 이 고른 Discogs 스타일, 최대 3개)</summary>
    public IReadOnlyList<string> Genres => Rec.Genres ?? [];
    public bool HasGenres => Genres.Count > 0;

    /// <summary>비슷한 내 곡 (내 파일로 바로 재생). VM이 라이브러리에서 파일 경로를 찾아 채운다.</summary>
    /// <summary>비슷한 내 곡 — 수록곡을 곡마다 분석했으면 그 결과(수록곡 목록과 같은 곡), 아니면 앨범 전체와 닮은 곡</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNear))] private IReadOnlyList<NearTrackItem> _nearItems = [];
    public string Near => string.Join("  /  ", Rec.NearTracks.Take(2).Select(Shorten));
    // BecauseOf는 아티스트 이름 또는 "웹진"·"AI 제안" → 표시할 때만 번역. 웹진·AI 제안은 배지가 이미 말해 줘서 뺀다
    public string Because => SavedNote ?? (IsMine ? ""
        : Rec.BecauseOf.Where(b => b is not ("웹진" or "AI 제안")).ToList() is { Count: > 0 } bs ? L.F("{0}에서 출발", string.Join(", ", bs.Select(L.T))) : "");

    public IAsyncRelayCommand ToggleTracksCommand { get; }
    public IRelayCommand ToggleSaveCommand { get; }

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveTip))] private bool _isSaved;
    public string SaveTip => IsSaved ? L.T("저장함에서 빼기") : L.T("저장함에 넣기 (나중에 디깅)");

    /// <summary>앨범 하트: 좋아요 (취향 학습에 반영). 책갈피(저장함)는 위시리스트일 뿐 학습에는 쓰지 않는다.</summary>
    public Action<ReleaseItem>? ToggleLikeAction { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LikeTip))] private bool _isLiked;
    /// <summary>처음 보는 앨범 (이번 실행 동안 NEW 배지)</summary>
    [ObservableProperty] private bool _isNew;
    /// <summary>이 아티스트를 팔로우 중 (☆)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FollowTip)), NotifyPropertyChangedFor(nameof(FollowMenu))] private bool _isFollowed;
    public string FollowMenu => IsFollowed ? L.T("뮤지션 팔로우 해제") : L.T("뮤지션 팔로우");
    public bool HasLabel => !string.IsNullOrEmpty(Rec.Label);
    public string FollowTip => IsFollowed ? L.T("팔로우 중 · 누르면 해제") : L.T("이 아티스트 팔로우 (신보를 항상 맨 앞에)");
    public Func<ReleaseItem, Task>? ToggleFollowAction { get; init; }

    [RelayCommand]
    private Task ToggleFollow() => ToggleFollowAction?.Invoke(this) ?? Task.CompletedTask;
    public string LikeTip => IsLiked ? L.T("좋아요 취소") : L.T("좋아요 · 이 앨범과 비슷한 소리를 더 추천");

    [RelayCommand]
    private void ToggleLike() => ToggleLikeAction?.Invoke(this);

    /// <summary>곡 하트: VM이 채운다 (곡 ID → 하트 여부, 하트 토글)</summary>
    public Func<long, bool>? IsTrackLiked { get; init; }
    public Action<TrackItem>? ToggleTrackLikeAction { get; init; }

    /// <summary>별로: 추천에서 숨기고 학습에 반영 (저장함 탭 아래에서 되돌릴 수 있다)</summary>
    public Action<ReleaseItem>? ToggleDislikeAction { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DislikeTip))] private bool _isDisliked;
    public string DislikeTip => IsDisliked ? L.T("별로 취소 (다시 추천에 나와요)") : L.T("별로예요 · 추천에서 숨기고 비슷한 소리·이 아티스트는 덜 추천");

    [RelayCommand]
    private void ToggleDislike() => ToggleDislikeAction?.Invoke(this);
    /// <summary>저장함 탭에서: 언제 저장했는지</summary>
    public string? SavedNote { get; init; }

    // ---- 수록곡 ----
    public ObservableCollection<TrackItem> Tracks { get; } = [];
    public DateTime TracksLoadedAt { get; private set; }
    /// <summary>이 앨범에서 마지막으로 재생한 곡 (커버 버튼은 이 곡을 이어서 튼다).</summary>
    public TrackItem? Current { get; set; }
    [ObservableProperty] private bool _showTracks;
    [ObservableProperty] private string? _tracksStatus;
    public string TracksButton => ShowTracks ? L.T("수록곡 닫기") : Tracks.Count > 0 ? L.F("수록곡 {0}", Tracks.Count) : L.T("수록곡");
    partial void OnShowTracksChanged(bool value) => OnPropertyChanged(nameof(TracksButton));

    /// <summary>새로 받은 수록곡으로 채운다. 같은 앨범이면 곡 객체는 두고 만료된 미리듣기 URL만 바꾼다.</summary>
    public void SetTracks(IReadOnlyList<AlbumTrack> tracks)
    {
        if (Tracks.Count == tracks.Count)
            for (int i = 0; i < tracks.Count; i++) Tracks[i].Preview = tracks[i].Preview;
        else
        {
            Tracks.Clear();
            Current = null;
            // 갓 나온 앨범은 곡마다 인기 점수가 같다 → 그때는 인기도를 표시하지 않는다
            int maxRank = tracks.Count > 1 && tracks.Max(t => t.Rank) > tracks.Min(t => t.Rank) ? tracks.Max(t => t.Rank) : 0;
            bool singleTop = tracks.Count(t => t.Rank == maxRank) == 1;
            foreach (var t in tracks)
                Tracks.Add(new TrackItem(this, t, Artist, maxRank, singleTop, _play)
                {
                    IsLiked = t.Id != 0 && IsTrackLiked?.Invoke(t.Id) == true,
                    ToggleLikeAction = ToggleTrackLikeAction,
                });
        }
        TracksLoadedAt = DateTime.Now;
        OnPropertyChanged(nameof(TracksButton));
    }

    /// <summary>Apple Music(iTunes 앱)에서 열기 — 주소 찾기는 MainViewModel이</summary>
    public Func<ReleaseItem, Task>? OpenAppleMusicAction { get; init; }

    [RelayCommand]
    private Task OpenAppleMusic() => OpenAppleMusicAction?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>YouTube에서 "아티스트 앨범" 검색 (공식 음원·뮤직비디오가 보통 맨 위)</summary>
    [RelayCommand]
    private void OpenYouTube() => YouTube.Search($"{Artist} {Album}");

    // 컴필레이션 곡은 아티스트 목록이 아주 길다 → 앞부분만
    private static string Shorten(string s)
    {
        var dash = s.IndexOf(" – ", StringComparison.Ordinal);
        if (dash <= 40) return s;
        var artists = s[..dash];
        var comma = artists.IndexOf(',');
        return L.F("{0} 외", comma > 0 ? artists[..comma] : artists[..37]) + s[dash..];
    }
}

/// <summary>Apple Music 주소 열기: iTunes가 itmss:// 를 받으면 앱에서, 아니면 브라우저에서</summary>
public static class AppleMusic
{
    private static readonly bool HasApp = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("itmss") != null;

    public static void Open(string url)
    {
        var target = HasApp && url.StartsWith("https://music.apple.com/", StringComparison.Ordinal) && !url.Contains("/search?")
            ? "itmss://" + url["https://".Length..] : url;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    }
}

public static class YouTube
{
    public static void Search(string query) =>
        Process.Start(new ProcessStartInfo("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query)) { UseShellExecute = true });
}

/// <summary>AI 커넥터 배지: "Claude Sonnet 5.5", "GPT gpt-5" (예전 기록은 "Claude"/"GPT"만)</summary>
public sealed record AiBadge(string Text)
{
    public bool IsGpt => Text.StartsWith("GPT", StringComparison.Ordinal);
    /// <summary>Claude 오렌지 / GPT 초록, 비싼 모델일수록 진하게</summary>
    public System.Windows.Media.Brush Color => AiColors.For(Text);
    public System.Windows.Media.Brush Tint => AiColors.Background(Text);
    /// <summary>웹진 배지는 보라색 + 잡지 아이콘에 웹진 이름만 (Text 는 색·순서·필터 키라 "웹진 · 이름" 그대로)</summary>
    public bool IsWebzine => Text.StartsWith("웹진", StringComparison.Ordinal);
    public string Display => IsWebzine ? Text.Replace("웹진 · ", "").Replace("웹진", "") : Text;
}

/// <summary>카드의 "비슷한 내 곡" 하나: 내 라이브러리 파일을 바로 재생한다.</summary>
public sealed partial class NearTrackItem(string label, string? path, Action<NearTrackItem> play) : ObservableObject
{
    public string Label { get; } = label;
    public string? Path { get; } = path;
    public bool CanPlay => Path != null;
    public string Tip => Path != null ? L.F("내 파일로 듣기 · {0}", Label) : L.F("{0} (내 라이브러리에서 파일을 못 찾았어요)", Label);
    [ObservableProperty] private bool _isPlaying;
    public void SetPlaying(bool playing) => IsPlaying = playing;

    [RelayCommand]
    private void Play() => play(this);
}

/// <summary>카드 안 수록곡 목록의 한 줄.</summary>
public sealed partial class TrackItem : ObservableObject
{
    public TrackItem(ReleaseItem release, AlbumTrack t, string albumArtist, int albumMaxRank, bool singleTop,
        Func<ReleaseItem, TrackItem?, Task> play)
    {
        // 인기도: 앨범 안에서 가장 인기 있는 곡 대비 (Deezer rank). 신보는 아직 낮고 비슷할 수 있다.
        int level = albumMaxRank > 0 ? Math.Clamp((int)Math.Ceiling(5.0 * t.Rank / albumMaxRank), t.Rank > 0 ? 1 : 0, 5) : 0;
        PopBars = [.. Enumerable.Range(0, 5).Select(i => i < level)];
        HasPopularity = albumMaxRank > 0;
        IsTop = albumMaxRank > 0 && singleTop && t.Rank == albumMaxRank;
        PopTip = t.Rank > 0 ? L.F("Deezer 인기 {0:N0}", t.Rank) + (IsTop ? " · " + L.T("앨범에서 가장 인기") : "") : L.T("인기 정보 없음");
        Release = release;
        Number = t.Number;
        Title = t.Title;
        Artist = t.Artist ?? albumArtist;
        // 앨범 아티스트와 다를 때만 (피처링, 컴필레이션) 따로 표시
        ArtistSuffix = t.Artist != null && !string.Equals(t.Artist, albumArtist, StringComparison.OrdinalIgnoreCase) ? $"· {t.Artist}" : "";
        DurationText = t.Duration > TimeSpan.Zero ? t.Duration.ToString(@"m\:ss") : "";
        _preview = t.Preview;
        Id = t.Id;
        PlayCommand = new AsyncRelayCommand(() => play(release, this));
    }

    public ReleaseItem Release { get; }
    public int Number { get; }
    public string Title { get; }
    public string Artist { get; }
    public string ArtistSuffix { get; }
    /// <summary>Deezer 곡 ID (곡 하트에 쓴다)</summary>
    public long Id { get; }
    public bool CanLike => Id != 0;
    public Action<TrackItem>? ToggleLikeAction { get; init; }
    [ObservableProperty] private bool _isLiked;

    [RelayCommand]
    private void ToggleLike() => ToggleLikeAction?.Invoke(this);
    public bool[] PopBars { get; }
    public bool HasPopularity { get; }
    public bool IsTop { get; }
    public string PopTip { get; }
    public string DurationText { get; }
    public IAsyncRelayCommand PlayCommand { get; }

    /// <summary>YouTube에서 "아티스트 곡명" 검색 — 미리듣기가 없는 곡도 전곡을 들어 볼 수 있게</summary>
    [RelayCommand]
    private void OpenYouTube() => YouTube.Search($"{Artist} {Title}");

    /// <summary>이 곡과 가장 닮은 내 곡 ("아티스트 – 곡명", 수록곡을 펼치면 소리 서비스가 채운다)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NearText))] private string? _near;
    /// <summary>수록곡 중 내 곡과 가장 닮은 곡</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NearText))] private bool _isClosest;
    public string NearText => Near == null ? "" : "└ " + L.F("비슷한 내 곡: {0}", Near) + (IsClosest ? " · " + L.T("가장 닮음") : "");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreview))] private string? _preview;
    public bool HasPreview => Preview != null;
    [ObservableProperty] private bool _isPlaying;

    /// <summary>재생 바 콜백: 곡과 카드(커버 버튼)에 함께 표시.</summary>
    public void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        Release.IsPlaying = playing;
    }
}

public sealed partial class CrateSection(string name, IReadOnlyList<ReleaseItem> items, int total, int mine, bool canMore, Action<string> showOnly)
    : ObservableObject
{
    /// <summary>레시피 이름 (키: More → showOnly)</summary>
    public string Name { get; } = name;
    /// <summary>표시용 이름 ("미분류" 같은 내장 이름만 번역되고 사용자 레시피 이름은 그대로)</summary>
    public string DisplayName => L.T(Name);
    public IReadOnlyList<ReleaseItem> Items { get; } = items;
    public string Meta => mine > 0 && mine < total ? L.F("{0}장 · 내 플리 {1} · 추가 제안 {2}", total, mine, total - mine)
        : mine == total ? L.F("{0}장 · 모두 내 플리", total) : L.F("{0}장 · 추가 제안", total);
    public bool CanMore { get; } = canMore;

    [RelayCommand]
    private void More() => showOnly(Name);
}

/// <summary>크레이트 · 형식 · 지역 필터 칩.</summary>
public sealed partial class FilterChip(string key, string label, Action<string> select) : ObservableObject
{
    /// <summary>키 (선택·순서 저장). 원문 그대로</summary>
    public string Key { get; } = key;
    /// <summary>표시용: 내장 이름("전체", "미분류", 레시피 이름 등)은 번역, 모르는 이름은 그대로</summary>
    public string Label { get; } = L.T(label);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int? _count;

    /// <summary>크레이트 칩만: 우클릭 메뉴로 순서를 옮긴다 (first / left / right / last / reset)</summary>
    public Action<string, string>? Mover { get; init; }
    public bool CanMove => Mover != null;
    /// <summary>드래그 중 이 칩 위에 놓으면 들어갈 쪽: "left" / "right" / "" (표시용 주황 세로선)</summary>
    [ObservableProperty] private string _dropSide = "";

    [RelayCommand]
    private void Select() => select(Key);

    [RelayCommand]
    private void Move(string where) => Mover?.Invoke(Key, where);
}

public sealed partial class DayCell(DateTime date, bool inMonth, Action<DateTime> select) : ObservableObject
{
    public DateTime Date { get; } = date;
    public int Day => Date.Day;
    public bool InMonth { get; } = inMonth;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _inRange;
    [ObservableProperty] private int _heat;
    /// <summary>이 날 발매된 앨범 중 AI가 찾은 것: 모델마다 점 하나 (색 = 커넥터·모델)</summary>
    [ObservableProperty] private IReadOnlyList<AiBadge> _aiDots = [];
    [ObservableProperty] private string? _aiTip;
    /// <summary>이 기간을 일반 추천으로 분석해 둔 적이 있다 (칸 위쪽 회색 선)</summary>
    [ObservableProperty] private bool _analyzed;
    /// <summary>아직 화면에서 본 적 없는 앨범 수 (칸 오른쪽 위 흰 고리)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNew))] private int _newCount;
    public bool HasNew => NewCount > 0;
    /// <summary>팔로우한 아티스트의 발매작 수 (칸 왼쪽 위 ★)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFollow))] private int _followCount;
    public bool HasFollow => FollowCount > 0;
    /// <summary>툴팁: 분석 이력 + AI가 찾은 앨범</summary>
    [ObservableProperty] private string? _tip;

    [RelayCommand]
    private void Select() => select(Date);
}

public sealed partial class MonthCell(int month, Action<int> select) : ObservableObject
{
    public int Month { get; } = month;
    public string Label => new DateTime(2000, Month, 1).ToString(L.T("M월"));
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _heat;
    [ObservableProperty] private IReadOnlyList<AiBadge> _aiDots = [];
    [ObservableProperty] private string? _aiTip;
    /// <summary>이 기간을 일반 추천으로 분석해 둔 적이 있다 (칸 위쪽 회색 선)</summary>
    [ObservableProperty] private bool _analyzed;
    /// <summary>아직 화면에서 본 적 없는 앨범 수 (칸 오른쪽 위 흰 고리)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNew))] private int _newCount;
    public bool HasNew => NewCount > 0;
    /// <summary>팔로우한 아티스트의 발매작 수 (칸 왼쪽 위 ★)</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFollow))] private int _followCount;
    public bool HasFollow => FollowCount > 0;
    /// <summary>툴팁: 분석 이력 + AI가 찾은 앨범</summary>
    [ObservableProperty] private string? _tip;

    [RelayCommand]
    private void Select() => select(Month);
}

/// <summary>내 라이브러리 탭: 아티스트 → 앨범.</summary>
public sealed class LibraryArtistGroup(string artist, string meta, IReadOnlyList<LibraryAlbumItem> albums)
{
    public string Artist { get; } = artist;
    public string Initial { get; } = artist.Length > 0 ? artist[..1].ToUpperInvariant() : "?";
    public string Meta { get; } = meta;
    public IReadOnlyList<LibraryAlbumItem> Albums { get; } = albums;
}

public sealed partial class LibraryAlbumItem : ObservableObject
{
    public LibraryAlbumItem(AlbumSummary album, Func<LibraryAlbumItem, Task> play)
    {
        Album = album;
        PlayCommand = new AsyncRelayCommand(() => play(this));
    }

    public AlbumSummary Album { get; }
    public IAsyncRelayCommand PlayCommand { get; }
    public string Title => string.IsNullOrWhiteSpace(Album.Album) ? L.T("(앨범 없음)") : Album.Album;
    public string Meta => string.Join(" · ", new[]
    {
        Album.ReleaseDate?.ToString("yyyy.M.d") ?? Album.Year?.ToString(),
        L.F("{0}곡", Album.TrackCount),
        Album.SampleTrackPath != null ? L.T("내 파일") : null
    }.Where(s => s != null));
    public bool CanPlay => Album.SampleTrackPath != null;
    [ObservableProperty] private string? _artUrl;
    [ObservableProperty] private bool _isPlaying;

    public void SetPlaying(bool playing) => IsPlaying = playing;
}

public static class Labels
{
    // 키로 쓰는 이름 (칩 키·그룹·순서 저장) — 번역하지 않는다. 표시할 때 L.T(...)
    public const string All = "전체";
    public const string Unsorted = "미분류";

    /// <summary>달력 요일 머리글 (월~일), 현재 문화권의 짧은 요일 이름</summary>
    public static string[] WeekDays =>
        [.. Enumerable.Range(1, 7).Select(i => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[i % 7])];

    /// <summary>지역 이름 (키). 화면에는 L.T(Region(r))</summary>
    public static string Region(Region r) => r switch
    {
        Core.Region.Korea => "한국",
        Core.Region.Japan => "일본",
        Core.Region.International => "해외",
        Core.Region.SoutheastAsia => "동남아",
        _ => ""
    };
}
