using System.IO;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>앱 설정 (톱니 버튼): 언어 · iTunes 라이브러리 · 자동 재학습 · 저장 위치와 용량. 추천 방식은 '추천 설정' 창에 따로.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _app;
    private readonly string? _mlDir;
    private readonly ViewModels.MainViewModel _vm;

    public SettingsWindow(AppSettings app, string? mlDir, ViewModels.MainViewModel vm)
    {
        InitializeComponent();
        _app = app;
        _mlDir = mlDir;
        _vm = vm;
        AutoRetrainBox.IsChecked = vm.AutoUpdate;
        AutoRetrainBox.Checked += (_, _) => vm.AutoUpdate = true;
        AutoRetrainBox.Unchecked += (_, _) => vm.AutoUpdate = false;
        ShowLanguages();
        ShowLibrary();
        ShowStorage();
    }

    // ---- 언어: 다시 시작하면 바뀐다 ----
    private void ShowLanguages()
    {
        foreach (var (code, name) in L.Languages)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "lang", Content = name, IsChecked = code == L.Lang };
            rb.Checked += (_, _) =>
            {
                _app.Language = code;
                _app.Save();
                bool changed = code != L.Lang;
                LangHint.Text = changed ? L.T("다시 시작하면 바뀌어요.") : "";
                RestartButton.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
            };
            LangGrid.Children.Add(rb);
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
    }

    // ---- iTunes 라이브러리 ----
    private void ShowLibrary()
    {
        LibraryPath.Text = _app.ITunesLibraryPath ?? L.T("찾지 못했어요");
        LibraryPath.ToolTip = _app.ITunesLibraryPath;
    }

    private void PickLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("iTunes 라이브러리 XML을 선택하세요 (iTunes Music Library.xml)"),
            Filter = L.T("iTunes 라이브러리 (*.xml)|*.xml"),
            InitialDirectory = Path.GetDirectoryName(_app.ITunesLibraryPath) ?? "",
        };
        if (dlg.ShowDialog(this) != true || dlg.FileName == _app.ITunesLibraryPath) return;
        _app.ITunesLibraryPath = dlg.FileName;
        _app.Save();
        ShowLibrary();
        LibraryResult.Text = L.T("앱을 다시 시작하면 새 라이브러리를 읽어요.");
        RestartButton.Visibility = Visibility.Visible;
    }

    // ---- 저장 위치와 용량 ----
    private void ShowStorage()
    {
        var dir = CacheLocation.Directory;
        CachePath.Text = dir;
        CachePath.ToolTip = dir;
        var parts = CacheLocation.Breakdown(dir);
        long total = parts.Sum(p => p.Bytes);
        CacheSizes.Text = L.F("캐시 합계 {0}", CacheLocation.Format(total)) + Environment.NewLine +
            string.Join(Environment.NewLine, parts.Select(p => $"· {p.Name} {CacheLocation.Format(p.Bytes)}"));

        // 소리 분석 데이터는 ml 폴더에 있다 (학습 데이터라 옮기지 않는다)
        if (_mlDir is { } ml && Directory.Exists(Path.Combine(ml, "data")))
        {
            var data = new DirectoryInfo(Path.Combine(ml, "data"));
            long albums = data.EnumerateFiles("album_*").Sum(f => f.Length);
            long library = data.EnumerateDirectories("emb_*").Sum(CacheLocation.Size);
            long model = data.EnumerateFiles("crate_model.json").Sum(f => f.Length);
            MlSizes.Text = L.F("소리 분석 데이터 ({0})", data.FullName) + Environment.NewLine +
                L.F("· 신보 미리듣기 분석 {0}", CacheLocation.Format(albums)) + Environment.NewLine +
                L.F("· 내 곡 학습 데이터 {0} · 취향 모델 {1}", CacheLocation.Format(library), CacheLocation.Format(model));
        }
    }

    private async void ClearRecs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.T("저장된 추천을 모두 지우고 지금 기간부터 새로 계산할까요?\n다른 기간은 열 때 새로 계산해요 (기간마다 30초 안팎)."),
                L.T("저장된 추천 비우기"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ClearRecsButton.IsEnabled = false;
        ClearResult.Text = L.T("비우고 지금 기간을 새로 계산하는 중…");
        await _vm.ClearSavedRecsAsync();
        ClearResult.Text = L.T("비웠어요. 지금 기간은 새로 계산했고, 다른 기간은 열 때 새로 계산해요.");
        ShowStorage();
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CacheLocation.Directory) { UseShellExecute = true });

    /// <summary>새 폴더로 캐시를 복사하고 설정에 적어 둔다. 다시 시작하면 새 위치를 쓴다 (예전 폴더는 그대로 남긴다).</summary>
    private void MoveCache_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("캐시를 둘 폴더를 고르세요"), InitialDirectory = CacheLocation.Directory };
        if (dlg.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(dlg.FolderName);
        var from = Path.GetFullPath(CacheLocation.Directory);
        if (string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), from.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            int copied = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // 쓰는 중인 로그 등은 공유 읽기로 복사
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = File.Create(dest);
                src.CopyTo(dst);
                copied++;
            }
            _app.CacheDirectory = target;
            _app.Save();
            MoveResult.Text = L.F("{0}개 파일을 {1}(으)로 복사했어요. 앱을 다시 시작하면 새 위치를 써요. 예전 폴더({2})는 확인 후 지우셔도 돼요.",
                copied, target, from);
            RestartButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoveResult.Text = L.F("옮기지 못했어요: {0}", ex.Message);
        }
    }
}
