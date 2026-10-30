using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>추천 설정: 0 기본 / 1 많이 / 2 최대, 모험 0 익숙하게 / 1 보통 / 2 새롭게</summary>
/// <param name="DropMode">다시 분석한 뒤 별로·차단과 아주 닮은 추천: 0 물어보기 / 1 자동으로 빼기 / 2 그대로(순서만 내림)</param>
/// <param name="ExcludeLive">라이브판 빼기 · <paramref name="ExcludeRemix"/> 리믹스 빼기: 제목으로 알아보고 화면에서 거른다 (다시 계산하지 않아도 바로)</param>
public sealed record RecPrefs(int Amount = 0, int Adventure = 1, bool ExcludeKnown = false, bool ExcludeInstrumental = true, int Tracks = 0,
    int DropMode = 0, bool ExcludeLive = false, bool ExcludeRemix = false);

public partial class RecommendSettingsWindow : Window
{
    private static readonly (string Label, string Hint)[] Amounts =
    [
        ("기본", "후보 아티스트 1,000명 · 추가 제안 150장까지 소리 분석. 처음 보는 주 30초 안팎."),
        ("많이", "후보 아티스트 2,000명 · 추가 제안 300장. 처음 보는 주 1분 안팎, 후보를 넓힐 때 Deezer에서 한 번 더 받아요."),
        ("최대", "후보 아티스트 3,000명 · 추가 제안 500장. 처음 보는 주 1~2분, 월·연 단위로 넓게 볼 때."),
    ];

    private static readonly (string Label, string Hint)[] TrackModes =
    [
        ("전곡", "수록곡 전부(최대 20곡)의 30초 미리듣기를 들어요. 앨범 전체의 성격을 봐요. 처음 보는 주는 오래 걸려요."),
        ("인기곡 3곡", "Deezer 인기 상위 3곡만 들어요. 빠르지만 앨범 뒤쪽 곡의 성격은 빠질 수 있어요."),
    ];

    private static readonly (string Label, string Hint)[] DropModes =
    [
        ("물어보기", "다시 계산한 뒤 관심 없음·차단한 앨범과 아주 닮은 추천이 있으면 목록 위에서 뺄지 물어요."),
        ("자동으로 빼기", "묻지 않고 빼고, 뺀 앨범을 알려 줘요."),
        ("그대로", "빼지 않고 점수만 깎아 아래로 내려요."),
    ];

    private static readonly (string Label, string Hint)[] Adventures =
    [
        ("익숙하게", "내가 듣는 아티스트와 가깝게 연결된 사람을 먼저. 소리가 조금 달라도 '그 사람이랑 친한' 아티스트."),
        ("보통", "아티스트 연결과 소리 닮음을 함께 봐요 (기본)."),
        ("새롭게", "연결은 멀어도 소리가 내 곡과 닮은 낯선 아티스트를 먼저."),
    ];

    public RecPrefs Result { get; private set; }

    private readonly RecipeSong.Core.BlockList _blocked;

    private readonly ViewModels.MainViewModel _vm;

    public RecommendSettingsWindow(RecPrefs prefs, RecipeSong.Core.BlockList blocked, ViewModels.MainViewModel vm)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 24;  // 화면보다 길면 본문만 스크롤 (버튼 줄은 아래 고정)
        _blocked = blocked;
        _vm = vm;
        ShowBlocked();
        ShowFollows();
        ShowWebzines();
        Result = prefs;
        Build(AmountGrid, AmountHint, Amounts, prefs.Amount);
        Build(AdventureGrid, AdventureHint, Adventures, prefs.Adventure);
        Build(TracksGrid, TracksHint, TrackModes, prefs.Tracks);
        Build(DropGrid, DropHint, DropModes, prefs.DropMode);
        ExcludeKnownBox.IsChecked = prefs.ExcludeKnown;
        ExcludeInstrumentalBox.IsChecked = prefs.ExcludeInstrumental;
        ExcludeLiveBox.IsChecked = prefs.ExcludeLive;
        ExcludeRemixBox.IsChecked = prefs.ExcludeRemix;
    }

    private void Build(UniformGrid grid, TextBlock hint, (string Label, string Hint)[] options, int selected)
    {
        for (int i = 0; i < options.Length; i++)
        {
            var (label, text) = options[i];
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = grid.Name, Tag = i, Content = L.T(label), IsChecked = i == selected };
            rb.Checked += (_, _) => hint.Text = L.T(text);
            grid.Children.Add(rb);
        }
        hint.Text = L.T(options[Math.Clamp(selected, 0, options.Length - 1)].Hint);
    }

    // ---- 웹진: 체크를 끄면 그 웹진은 읽지도 보여 주지도 않는다 ----
    private void ShowWebzines()
    {
        foreach (var f in RecipeSong.Core.WebzineFeeds.Defaults)
        {
            var box = new CheckBox { Content = $"{f.Name} ({L.T(f.Region)})", IsChecked = !_vm.DisabledWebzines.Contains(f.Name),
                Foreground = (System.Windows.Media.Brush)FindResource("Text"), Margin = new Thickness(0, 0, 16, 6), Tag = f.Name };
            box.Checked += (_, _) => SaveWebzines();
            box.Unchecked += (_, _) => SaveWebzines();
            WebzinePanel.Children.Add(box);
        }
    }

    private void SaveWebzines() =>
        _vm.SetDisabledWebzines(WebzinePanel.Children.OfType<CheckBox>().Where(c => c.IsChecked != true).Select(c => (string)c.Tag));

    private async void RefreshWebzines_Click(object sender, RoutedEventArgs e)
    {
        WebzineRefreshText.Text = L.T("읽는 중…");
        await _vm.RefreshWebzinesAsync(force: true);
        WebzineRefreshText.Text = L.T("다 읽었어요");
    }

    // ---- 차단 목록: 칩을 누르면 해제 ----
    // ---- 팔로우: 칩을 누르면 해제 ----
    private bool _followsChanged;

    private void ShowFollows()
    {
        FollowPanel.Children.Clear();
        var all = _vm.Follows.All.ToList();
        ShowCount(FollowCount, FollowFilterBox, all.Count);
        foreach (var f in all.Where(f => Matches(FollowFilter, f.Name)))
        {
            var b = new Button { Style = (Style)FindResource("Bare"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = L.T("눌러서 팔로우 해제") };
            b.Content = new TextBlock { Text = $"★ {f.Name}  ✕", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Text2") };
            b.Click += (_, _) => { _vm.Follows.Unfollow(f.Id); _followsChanged = true; ShowFollows(); };
            FollowPanel.Children.Add(b);
        }
        FollowEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 늘어나는 목록 (팔로우·차단): 개수 표시, 6개를 넘으면 이름 찾기 ----
    private const int FilterFrom = 7;

    private static void ShowCount(TextBlock count, FrameworkElement filter, int n)
    {
        count.Text = n > 0 ? n.ToString() : "";
        filter.Visibility = n >= FilterFrom ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(TextBox filter, string name) =>
        filter.Text.Trim() is not { Length: > 0 } q || name.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        FollowFilterHint.Visibility = FollowFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockedFilterHint.Visibility = BlockedFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (sender == FollowFilter) ShowFollows(); else ShowBlocked();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_followsChanged) _ = _vm.RefreshFollowsAsync();
    }

    private void ShowBlocked()
    {
        BlockedPanel.Children.Clear();
        int total = _blocked.Artists.Count + _blocked.Labels.Count + _blocked.SameName.Count;
        ShowCount(BlockedCount, BlockedFilterBox, total);
        void Add(string kind, string name, Action unblock)
        {
            if (!Matches(BlockedFilter, name)) return;
            var b = new Button { Style = (Style)FindResource("Bare"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = L.T("눌러서 차단 해제") };
            b.Content = new TextBlock { Text = $"{kind} · {name}  ✕", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Text2") };
            b.Click += (_, _) => { unblock(); ShowBlocked(); };
            BlockedPanel.Children.Add(b);
        }
        foreach (var a in _blocked.Artists.ToList()) Add(L.T("아티스트"), a, () => _blocked.UnblockArtist(a));
        foreach (var l in _blocked.Labels.ToList()) Add(L.T("레이블"), l, () => _blocked.UnblockLabel(l));
        foreach (var s in _blocked.SameName.ToList()) Add(L.T("동명이인"), $"{s.Name} ({s.Album})", () => _blocked.UnblockSameName(s));
        BlockedEmpty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int Selected(UniformGrid grid) =>
        grid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 0;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new RecPrefs(Selected(AmountGrid), Selected(AdventureGrid), ExcludeKnownBox.IsChecked == true, ExcludeInstrumentalBox.IsChecked == true,
            Selected(TracksGrid), Selected(DropGrid), ExcludeLiveBox.IsChecked == true, ExcludeRemixBox.IsChecked == true);
        DialogResult = true;
    }
}
