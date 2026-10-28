using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

namespace RecipeSong.Wpf;

/// <summary>플레이리스트마다 레시피로 쓸지 · 레시피 이름 · 지역을 고른다. 저장하면 Result (ml/data/crates.json 에 쓰는 건 부르는 쪽)</summary>
public partial class RecipeSetupWindow : Window
{
    private sealed record Row(string Playlist, CheckBox Use, TextBox Name, UniformGrid Regions);

    private readonly List<Row> _rows = [];
    private readonly CrateConfig _current;

    public CrateConfig? Result { get; private set; }

    public RecipeSetupWindow(IReadOnlyList<Playlist> playlists, CrateConfig current, bool firstRun)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 24;  // 화면보다 길면 본문만 스크롤 (버튼 줄은 아래 고정)
        _current = current;
        if (firstRun) CancelText.Text = L.T("나중에");
        // 학습에 쓰는 플레이리스트와 같은 기준: 직접 만든 것, 15곡 이상
        var list = playlists.Where(p => p.Kind == PlaylistKind.User && p.Tracks.Count >= 15)
            .GroupBy(p => p.Name).Select(g => g.First())
            .OrderByDescending(p => current.Playlists.ContainsKey(p.Name)).ThenByDescending(p => p.Tracks.Count).ToList();
        foreach (var p in list) AddRow(p);
        Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRow(Playlist p)
    {
        var entry = Existing(p.Name);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

        var use = new CheckBox { IsChecked = entry != null, VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
            Content = new TextBlock { Inlines = { new System.Windows.Documents.Run(p.Name),
                new System.Windows.Documents.Run($"  {L.F("{0}곡", p.Tracks.Count)}") { Foreground = (Brush)FindResource("Muted"), FontSize = 12 } } } };
        var name = new TextBox { Text = entry is null ? p.Name : entry.Style ?? "", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5),
            FontSize = 13, Background = (Brush)FindResource("Surface3"), Foreground = (Brush)FindResource("Text"), CaretBrush = (Brush)FindResource("Text"),
            BorderBrush = (Brush)FindResource("Line3"), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(name, L.F("레시피 이름: {0}", p.Name));
        var regions = new UniformGrid { Rows = 1 };
        var region = entry?.Region ?? Guess(p);
        foreach (var r in CrateConfig.Regions)
            regions.Children.Add(new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "region-" + p.Name, Content = L.T(r), Tag = r,
                IsChecked = r == region, FontSize = 12, Padding = new Thickness(0, 4, 0, 4) });
        var regionBox = new Border { Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(3), CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("Surface2"), Child = regions };

        // 쓰지 않는 플레이리스트는 이름·지역을 흐리게
        void Sync()
        {
            name.IsEnabled = regionBox.IsEnabled = use.IsChecked == true;
            regionBox.Opacity = use.IsChecked == true ? 1 : 0.35;  // 세그먼트는 꺼져도 주황이 남아서
        }
        use.Checked += (_, _) => Sync();
        use.Unchecked += (_, _) => Sync();
        Sync();

        Grid.SetColumn(name, 1);
        Grid.SetColumn(regionBox, 2);
        grid.Children.Add(use);
        grid.Children.Add(name);
        grid.Children.Add(regionBox);
        Rows.Children.Add(grid);
        _rows.Add(new Row(p.Name, use, name, regions));
    }

    private CrateConfig.Entry? Existing(string playlist) =>
        _current.Uses(playlist) ? _current.Playlists[playlist] : null;

    /// <summary>처음 고를 때 지역 기본값: 곡 아티스트 이름의 글자 (한글 → 한국, 가나·한자 → 일본), 아니면 해외</summary>
    private static string Guess(Playlist p)
    {
        int ko = 0, ja = 0;
        foreach (var t in p.Tracks.Take(200))
        {
            var r = RegionResolver.FromScript(t.Artist);
            if (r == Region.Korea) ko++;
            else if (r == Region.Japan) ja++;
        }
        int n = Math.Min(200, p.Tracks.Count);
        return ko * 3 > n ? "한국" : ja * 3 > n ? "일본" : "해외";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var used = _rows.Where(r => r.Use.IsChecked == true).ToList();
        if (used.Count == 0) { Problem.Text = L.T("레시피로 쓸 플레이리스트를 하나 이상 골라 주세요."); return; }
        if (used.All(r => r.Name.Text.Trim().Length == 0)) { Problem.Text = L.T("레시피 이름이 하나 이상 있어야 해요."); return; }
        Result = new CrateConfig
        {
            Playlists = used.ToDictionary(r => r.Playlist, r => new CrateConfig.Entry(
                r.Name.Text.Trim() is { Length: > 0 } n ? n : null,
                r.Regions.Children.OfType<RadioButton>().FirstOrDefault(b => b.IsChecked == true)?.Tag as string ?? "해외")),
            // 예전 설정의 '학습에서 빼는 플레이리스트'는 그대로 (고른 플레이리스트는 빼지 않는다)
            Ignore = [.. _current.Ignore.Where(i => used.All(r => r.Playlist != i))],
        };
        DialogResult = true;
    }
}
