using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RecipeSong.Wpf;

/// <summary>추천 설정: 0 기본 / 1 많이 / 2 최대, 모험 0 익숙하게 / 1 보통 / 2 새롭게</summary>
public sealed record RecPrefs(int Amount = 0, int Adventure = 1, bool ExcludeKnown = false);

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

    private readonly RecipeSong.Core.AppSettings _app;
    private readonly string? _mlDir;

    private readonly RecipeSong.Core.BlockList _blocked;

    public RecommendSettingsWindow(RecPrefs prefs, RecipeSong.Core.AppSettings app, string? mlDir, RecipeSong.Core.BlockList blocked)
    {
        InitializeComponent();
        _app = app;
        _mlDir = mlDir;
        _blocked = blocked;
        ShowBlocked();
        ShowStorage();
        Result = prefs;
        Build(AmountGrid, AmountHint, Amounts, prefs.Amount);
        Build(AdventureGrid, AdventureHint, Adventures, prefs.Adventure);
        ExcludeKnownBox.IsChecked = prefs.ExcludeKnown;
    }

    private void Build(UniformGrid grid, TextBlock hint, (string Label, string Hint)[] options, int selected)
    {
        for (int i = 0; i < options.Length; i++)
        {
            var (label, text) = options[i];
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = grid.Name, Tag = i, Content = label, IsChecked = i == selected };
            rb.Checked += (_, _) => hint.Text = text;
            grid.Children.Add(rb);
        }
        hint.Text = options[Math.Clamp(selected, 0, options.Length - 1)].Hint;
    }

    // ---- 차단 목록: 칩을 누르면 해제 ----
    private void ShowBlocked()
    {
        BlockedPanel.Children.Clear();
        void Add(string kind, string name, Action unblock)
        {
            var b = new Button { Style = (Style)FindResource("Bare"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = "눌러서 차단 해제" };
            b.Content = new TextBlock { Text = $"{kind} · {name}  ✕", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Text2") };
            b.Click += (_, _) => { unblock(); ShowBlocked(); };
            BlockedPanel.Children.Add(b);
        }
        foreach (var a in _blocked.Artists.ToList()) Add("아티스트", a, () => _blocked.UnblockArtist(a));
        foreach (var l in _blocked.Labels.ToList()) Add("레이블", l, () => _blocked.UnblockLabel(l));
        BlockedEmpty.Visibility = BlockedPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 저장 위치와 용량 ----

    private void ShowStorage()
    {
        var dir = RecipeSong.Core.CacheLocation.Directory;
        CachePath.Text = dir;
        CachePath.ToolTip = dir;
        var parts = RecipeSong.Core.CacheLocation.Breakdown(dir);
        long total = parts.Sum(p => p.Bytes);
        CacheSizes.Text = $"캐시 합계 {RecipeSong.Core.CacheLocation.Format(total)}" + Environment.NewLine +
            string.Join(Environment.NewLine, parts.Select(p => $"· {p.Name} {RecipeSong.Core.CacheLocation.Format(p.Bytes)}"));

        // 소리 분석 데이터는 ml 폴더에 있다 (학습 데이터라 옮기지 않는다)
        if (_mlDir is { } ml && System.IO.Directory.Exists(System.IO.Path.Combine(ml, "data")))
        {
            var data = new System.IO.DirectoryInfo(System.IO.Path.Combine(ml, "data"));
            long albums = data.EnumerateFiles("album_*").Sum(f => f.Length);
            long library = data.EnumerateDirectories("emb_*").Sum(RecipeSong.Core.CacheLocation.Size);
            long model = data.EnumerateFiles("crate_model.json").Sum(f => f.Length);
            MlSizes.Text = $"소리 분석 데이터 ({data.FullName})" + Environment.NewLine +
                $"· 신보 미리듣기 분석 {RecipeSong.Core.CacheLocation.Format(albums)}" + Environment.NewLine +
                $"· 내 곡 학습 데이터 {RecipeSong.Core.CacheLocation.Format(library)} · 취향 모델 {RecipeSong.Core.CacheLocation.Format(model)}";
        }
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RecipeSong.Core.CacheLocation.Directory) { UseShellExecute = true });

    /// <summary>새 폴더로 캐시를 복사하고 설정에 적어 둔다. 다시 시작하면 새 위치를 쓴다 (예전 폴더는 그대로 남긴다).</summary>
    private void MoveCache_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "캐시를 둘 폴더를 고르세요", InitialDirectory = RecipeSong.Core.CacheLocation.Directory };
        if (dlg.ShowDialog(this) != true) return;
        var target = System.IO.Path.GetFullPath(dlg.FolderName);
        var from = System.IO.Path.GetFullPath(RecipeSong.Core.CacheLocation.Directory);
        if (string.Equals(target.TrimEnd(System.IO.Path.DirectorySeparatorChar), from.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            int copied = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(from, "*", System.IO.SearchOption.AllDirectories))
            {
                var dest = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(from, file));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                // 쓰는 중인 로그 등은 공유 읽기로 복사
                using var src = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                using var dst = System.IO.File.Create(dest);
                src.CopyTo(dst);
                copied++;
            }
            _app.CacheDirectory = target;
            _app.Save();
            MoveResult.Text = $"{copied}개 파일을 {target}(으)로 복사했어요. 앱을 다시 시작하면 새 위치를 써요. 예전 폴더({from})는 확인 후 지우셔도 돼요.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MoveResult.Text = $"옮기지 못했어요: {ex.Message}";
        }
    }

    private static int Selected(UniformGrid grid) =>
        grid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 0;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new RecPrefs(Selected(AmountGrid), Selected(AdventureGrid), ExcludeKnownBox.IsChecked == true);
        DialogResult = true;
    }
}
