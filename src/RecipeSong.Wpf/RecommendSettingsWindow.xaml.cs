using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>추천 설정: 0 기본 / 1 많이 / 2 최대, 모험 0 익숙하게 / 1 보통 / 2 새롭게</summary>
public sealed record RecPrefs(int Amount = 0, int Adventure = 1, bool ExcludeKnown = false, bool ExcludeInstrumental = true);

public partial class RecommendSettingsWindow : Window
{
    private static readonly (string Label, string Hint)[] Amounts =
    [
        ("기본", "후보 아티스트 1,000명 · 추가 제안 150장까지 소리 분석. 처음 보는 주 30초 안팎."),
        ("많이", "후보 아티스트 2,000명 · 추가 제안 300장. 처음 보는 주 1분 안팎, 후보를 넓힐 때 Deezer에서 한 번 더 받아요."),
        ("최대", "후보 아티스트 3,000명 · 추가 제안 500장. 처음 보는 주 1~2분, 월·연 단위로 넓게 볼 때."),
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
        _blocked = blocked;
        _vm = vm;
        ShowBlocked();
        ShowWebzines();
        Result = prefs;
        Build(AmountGrid, AmountHint, Amounts, prefs.Amount);
        Build(AdventureGrid, AdventureHint, Adventures, prefs.Adventure);
        ExcludeKnownBox.IsChecked = prefs.ExcludeKnown;
        ExcludeInstrumentalBox.IsChecked = prefs.ExcludeInstrumental;
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
    private void ShowBlocked()
    {
        BlockedPanel.Children.Clear();
        void Add(string kind, string name, Action unblock)
        {
            var b = new Button { Style = (Style)FindResource("Bare"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = L.T("눌러서 차단 해제") };
            b.Content = new TextBlock { Text = $"{kind} · {name}  ✕", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Text2") };
            b.Click += (_, _) => { unblock(); ShowBlocked(); };
            BlockedPanel.Children.Add(b);
        }
        foreach (var a in _blocked.Artists.ToList()) Add(L.T("아티스트"), a, () => _blocked.UnblockArtist(a));
        foreach (var l in _blocked.Labels.ToList()) Add(L.T("레이블"), l, () => _blocked.UnblockLabel(l));
        foreach (var s in _blocked.SameName.ToList()) Add(L.T("동명이인"), $"{s.Name} ({s.Album})", () => _blocked.UnblockSameName(s));
        BlockedEmpty.Visibility = BlockedPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int Selected(UniformGrid grid) =>
        grid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 0;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new RecPrefs(Selected(AmountGrid), Selected(AdventureGrid), ExcludeKnownBox.IsChecked == true, ExcludeInstrumentalBox.IsChecked == true);
        DialogResult = true;
    }
}
