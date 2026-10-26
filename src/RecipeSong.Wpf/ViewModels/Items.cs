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
    public string RegionLabel => Labels.Region(Rec.Region);
    public string CrateName => Rec.Crate ?? Labels.Unsorted;
    public bool HasFit => Rec.CrateFit != null;
    public string FitText => Rec.CrateFit is double p ? $"{Rec.Crate} 어울림 {p:P0}" : "";
    public bool[] FitDots { get; }
    public bool Known => Rec.IsKnownArtist;
    public bool IsMine => Rec.FromPlaylist != null;
    /// <summary>저장함 앨범과 특히 닮았을 때: 가장 닮은 저장 앨범</summary>
    public string? FavNear => Rec.FavNear;
    public bool HasFavNear => !string.IsNullOrEmpty(Rec.FavNear);
    public bool IsAi => Rec.AiReason != null;
    public string? AiReason => Rec.AiReason;
    /// <summary>제안한 AI 커넥터 ("Claude", "GPT") — 커넥터마다 배지 하나</summary>
    public IReadOnlyList<AiBadge> AiBy => [.. (Rec.AiBy ?? (IsAi ? ["AI"] : [])).Select(b => new AiBadge(b))];
    public string Badge => IsMine ? $"내 플리 · {Rec.FromPlaylist}" : IsAi ? "" : Known ? "아는 아티스트" : "";
    public string BadgeTip => IsMine ? $"{Rec.FromPlaylist} 플레이리스트에 있는 뮤지션의 신보"
        : IsAi ? "AI 커넥터가 웹에서 찾고 Deezer에서 확인한 신보" : "내 라이브러리에 있는 아티스트";
    public System.Windows.Thickness BadgeMargin => HasFit ? new(8, 0, 0, 0) : new(0);
    public bool HasNear => Rec.NearTracks.Count > 0;
    public string Near => string.Join("  /  ", Rec.NearTracks.Take(2).Select(Shorten));
    public string Because => SavedNote ?? (IsMine || Rec.BecauseOf.Count == 0 ? "" : $"{string.Join(", ", Rec.BecauseOf)}에서 출발");

    public IAsyncRelayCommand ToggleTracksCommand { get; }
    public IRelayCommand ToggleSaveCommand { get; }

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveTip))] private bool _isSaved;
    public string SaveTip => IsSaved ? "저장함에서 빼기" : "저장함에 넣기 (나중에 디깅)";

    /// <summary>별로: 추천에서 숨기고 학습에 반영 (저장함 탭 아래에서 되돌릴 수 있다)</summary>
    public Action<ReleaseItem>? ToggleDislikeAction { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DislikeTip))] private bool _isDisliked;
    public string DislikeTip => IsDisliked ? "별로 취소 (다시 추천에 나와요)" : "별로예요 · 추천에서 숨기고 비슷한 소리·이 아티스트는 덜 추천";

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
    public string TracksButton => ShowTracks ? "수록곡 접기" : Tracks.Count > 0 ? $"수록곡 {Tracks.Count}" : "수록곡";
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
            foreach (var t in tracks) Tracks.Add(new TrackItem(this, t, Artist, maxRank, singleTop, _play));
        }
        TracksLoadedAt = DateTime.Now;
        OnPropertyChanged(nameof(TracksButton));
    }

    [RelayCommand]
    private void Open()
    {
        if (Rec.Link != null) Process.Start(new ProcessStartInfo(Rec.Link) { UseShellExecute = true });
    }

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
        return (comma > 0 ? artists[..comma] : artists[..37]) + " 외" + s[dash..];
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
        PopTip = t.Rank > 0 ? $"Deezer 인기 {t.Rank:N0}" + (IsTop ? " · 앨범에서 가장 인기" : "") : "인기 정보 없음";
        Release = release;
        Number = t.Number;
        Title = t.Title;
        Artist = t.Artist ?? albumArtist;
        // 앨범 아티스트와 다를 때만 (피처링, 컴필레이션) 따로 표시
        ArtistSuffix = t.Artist != null && !string.Equals(t.Artist, albumArtist, StringComparison.OrdinalIgnoreCase) ? $"· {t.Artist}" : "";
        DurationText = t.Duration > TimeSpan.Zero ? t.Duration.ToString(@"m\:ss") : "";
        _preview = t.Preview;
        PlayCommand = new AsyncRelayCommand(() => play(release, this));
    }

    public ReleaseItem Release { get; }
    public int Number { get; }
    public string Title { get; }
    public string Artist { get; }
    public string ArtistSuffix { get; }
    public bool[] PopBars { get; }
    public bool HasPopularity { get; }
    public bool IsTop { get; }
    public string PopTip { get; }
    public string DurationText { get; }
    public IAsyncRelayCommand PlayCommand { get; }

    /// <summary>YouTube에서 "아티스트 곡명" 검색 — 미리듣기가 없는 곡도 전곡을 들어 볼 수 있게</summary>
    [RelayCommand]
    private void OpenYouTube() => YouTube.Search($"{Artist} {Title}");

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
    public string Name { get; } = name;
    public IReadOnlyList<ReleaseItem> Items { get; } = items;
    public string Meta => mine > 0 && mine < total ? $"{total}장 · 내 플리 {mine} · 추가 제안 {total - mine}"
        : mine == total ? $"{total}장 · 모두 내 플리" : $"{total}장 · 추가 제안";
    public bool CanMore { get; } = canMore;

    [RelayCommand]
    private void More() => showOnly(Name);
}

/// <summary>크레이트 · 형식 · 지역 필터 칩.</summary>
public sealed partial class FilterChip(string key, string label, Action<string> select) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
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
    /// <summary>툴팁: 분석 이력 + AI가 찾은 앨범</summary>
    [ObservableProperty] private string? _tip;

    [RelayCommand]
    private void Select() => select(Date);
}

public sealed partial class MonthCell(int month, Action<int> select) : ObservableObject
{
    public int Month { get; } = month;
    public string Label => $"{Month}월";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _heat;
    [ObservableProperty] private IReadOnlyList<AiBadge> _aiDots = [];
    [ObservableProperty] private string? _aiTip;
    /// <summary>이 기간을 일반 추천으로 분석해 둔 적이 있다 (칸 위쪽 회색 선)</summary>
    [ObservableProperty] private bool _analyzed;
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
    public string Title => string.IsNullOrWhiteSpace(Album.Album) ? "(앨범 없음)" : Album.Album;
    public string Meta => string.Join(" · ", new[]
    {
        Album.ReleaseDate?.ToString("yyyy.M.d") ?? Album.Year?.ToString(),
        $"{Album.TrackCount}곡",
        Album.SampleTrackPath != null ? "내 파일" : null
    }.Where(s => s != null));
    public bool CanPlay => Album.SampleTrackPath != null;
    [ObservableProperty] private string? _artUrl;
    [ObservableProperty] private bool _isPlaying;

    public void SetPlaying(bool playing) => IsPlaying = playing;
}

public static class Labels
{
    public const string All = "전체";
    public const string Unsorted = "미분류";

    public static string Region(Region r) => r switch
    {
        Core.Region.Korea => "한국",
        Core.Region.Japan => "일본",
        Core.Region.International => "해외",
        Core.Region.SoutheastAsia => "동남아",
        _ => ""
    };
}
