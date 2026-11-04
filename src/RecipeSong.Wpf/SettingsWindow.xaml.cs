using System.IO;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>앱 설정 (톱니 버튼): 언어 · iTunes 라이브러리 · 자동 분석 · 장르 정보 키(넣을 게 있을 때만) · 고급 · 저장 위치와 용량. 추천 방식은 '추천 설정' 창에 따로.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _app;
    private readonly string? _mlDir;
    private readonly ViewModels.MainViewModel _vm;
    private readonly CancellationTokenSource _archiveScan = new();

    public SettingsWindow(AppSettings app, string? mlDir, ViewModels.MainViewModel vm)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 24;  // 화면보다 길면 본문만 스크롤 (버튼 줄은 아래 고정)
        _app = app;
        _mlDir = mlDir;
        _vm = vm;
        // 버전은 src/Directory.Build.props
        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = ver != null ? $"RecipeSong {ver.Major}.{ver.Minor}" : "";
        AutoRetrainBox.IsChecked = vm.AutoUpdate;
        AutoRetrainBox.Checked += (_, _) => vm.AutoUpdate = true;
        AutoRetrainBox.Unchecked += (_, _) => vm.AutoUpdate = false;
        DebugPipelineBox.IsChecked = vm.DebugPipeline;
        DebugPipelineBox.Checked += (_, _) => vm.DebugPipeline = true;
        DebugPipelineBox.Unchecked += (_, _) => vm.DebugPipeline = false;
        ShowLanguages();
        ShowLibrary();
        ShowArchiveFolders();
        ShowGenreKeys();
        ShowStorage();
        DownloadStorage.RestartRequired += () => RestartButton.Visibility = Visibility.Visible;
        DownloadStorage.Configure(_app, _mlDir);
    }

    // ---- 장르 정보 (Last.fm 키 · 예전에 넣은 Discogs 토큰): 저장하면 다음 추천 계산부터 쓴다 ----
    private void ShowGenreKeys()
    {
        var s = _vm.GenreSettings;
        // 앱에 키가 들어 있으면 Last.fm 은 기본으로 쓰니 칸을 숨긴다 (예전에 직접 넣은 키가 있으면 지울 수 있게 둔다)
        bool builtIn = RecipeSong.Core.Providers.GenreSourceSettings.BuiltInLastFmKey.Length > 0 && !s.HasStoredLastFmKey;
        LastFmField.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
        // Discogs 는 새로 넣지 않는다 (판본·크레딧은 MusicBrainz 로). 예전에 넣은 토큰은 계속 쓰고, 지울 수 있게 칸을 둔다
        DiscogsField.Visibility = s.HasStoredDiscogsToken ? Visibility.Visible : Visibility.Collapsed;
        // 넣을 칸이 하나도 없으면 '장르 정보' 전체를 숨긴다
        GenreSection.Visibility = builtIn && !s.HasStoredDiscogsToken ? Visibility.Collapsed : Visibility.Visible;
        LastFmStored.Visibility = s.HasStoredLastFmKey && LastFmBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscogsStored.Visibility = s.HasStoredDiscogsToken && DiscogsBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GenreKeyStatus.Text = s.LastFmKey == null && s.DiscogsToken == null
            ? L.T("키가 없으면 Deezer 장르와 소리로만 나눠요.")
            : "";
        LastFmBox.PasswordChanged -= GenreBox_Changed;
        LastFmBox.PasswordChanged += GenreBox_Changed;
        DiscogsBox.PasswordChanged -= GenreBox_Changed;
        DiscogsBox.PasswordChanged += GenreBox_Changed;
    }

    private void GenreBox_Changed(object sender, RoutedEventArgs e)
    {
        LastFmStored.Visibility = _vm.GenreSettings.HasStoredLastFmKey && LastFmBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscogsStored.Visibility = _vm.GenreSettings.HasStoredDiscogsToken && DiscogsBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>칸에 입력한 것만 바꾼다 (빈 칸은 저장한 키를 그대로)</summary>
    private void SaveGenreKeys_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.GenreSettings;
        if (LastFmBox.Password.Trim().Length > 0) s.SetLastFmKey(LastFmBox.Password);
        if (DiscogsBox.Password.Trim().Length > 0) s.SetDiscogsToken(DiscogsBox.Password);
        _vm.SaveGenreSettings();
        LastFmBox.Password = DiscogsBox.Password = "";
        ShowGenreKeys();
        GenreKeyStatus.Text = L.T("저장했어요. 다음 추천 계산부터 써요.");
    }

    private void ClearGenreKeys_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.GenreSettings;
        s.SetLastFmKey(null);
        s.SetDiscogsToken(null);
        _vm.SaveGenreSettings();
        LastFmBox.Password = DiscogsBox.Password = "";
        ShowGenreKeys();
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
        if (Application.Current.MainWindow is MainWindow main) main.Restart();
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

    private void ShowArchiveFolders()
    {
        ArchiveFoldersPanel.Children.Clear();
        if (_app.ArchiveFolders.Count == 0)
            ArchiveFoldersPanel.Children.Add(new TextBlock { Text = L.T("등록된 ZIP 경로가 없어요."), FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("Text2") });
        foreach (var folder in _app.ArchiveFolders)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = new Button { Content = "×", Style = (Style)FindResource("Bare"), Padding = new Thickness(8, 3, 8, 3),
                ToolTip = L.F("경로 등록 해제: {0}", folder) };
            System.Windows.Automation.AutomationProperties.SetName(remove, L.F("경로 등록 해제: {0}", folder));
            remove.Click += async (_, _) => { _app.ArchiveFolders.Remove(folder); _app.Save(); ShowArchiveFolders(); await RefreshArchiveAsync(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = folder, ToolTip = folder, TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            ArchiveFoldersPanel.Children.Add(row);
        }
        ArchiveResult.Text = L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", _vm.ArchiveAlbumCount)
            + (_vm.ArchiveWarnings.Count > 0 ? "\n" + string.Join("\n", _vm.ArchiveWarnings.Take(3)) : "");
    }

    private async void AddArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("앨범 ZIP 보관 폴더를 고르세요") };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        if (_app.ArchiveFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _app.ArchiveFolders.Add(folder);
        _app.Save();
        ShowArchiveFolders();
        await RefreshArchiveAsync();
    }

    private async void RefreshArchive_Click(object sender, RoutedEventArgs e) => await RefreshArchiveAsync();
    private async Task RefreshArchiveAsync()
    {
        ArchiveSettings.IsEnabled = false;
        ArchiveResult.Text = L.T("ZIP 보유 정보를 읽는 중…");
        bool reading = true;
        var progress = new Progress<RecipeSong.Core.Library.ArchiveScanProgress>(p =>
        {
            if (!reading || _archiveScan.IsCancellationRequested) return;
            var stage = p.Stage switch
            {
                RecipeSong.Core.Library.ArchiveScanStage.Waiting => L.T("다른 ZIP 읽기 작업을 기다리는 중…"),
                RecipeSong.Core.Library.ArchiveScanStage.Enumerating => L.T("ZIP 경로를 확인하는 중…"),
                RecipeSong.Core.Library.ArchiveScanStage.Saving => L.T("ZIP 색인을 저장하는 중…"),
                RecipeSong.Core.Library.ArchiveScanStage.Completed => L.T("보유 정보를 적용하는 중…"),
                _ => L.T("ZIP 메타데이터를 읽는 중…"),
            };
            var current = Path.GetFileName(Path.TrimEndingDirectorySeparator(p.CurrentPath));
            if (current.Length == 0) current = p.CurrentPath;
            ArchiveResult.Text = stage + "\n" + L.F("ZIP {0}개 처리 · 앨범 {1}개 · 재사용 {2}개 · 경고 {3}개", p.Processed, p.Albums, p.Reused, p.Warnings)
                + (current.Length > 0 ? "\n" + current : "");
            ArchiveResult.ToolTip = p.CurrentPath.Length > 0 ? p.CurrentPath : null;
        });
        try
        {
            var result = await _vm.RefreshArchiveAsync(_archiveScan.Token, progress);
            reading = false;
            ArchiveResult.ToolTip = null;
            ArchiveResult.Text = L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", result.Count)
                + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings.Take(3)) : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ArchiveResult.Text = ex.Message; }
        finally { reading = false; ArchiveSettings.IsEnabled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _archiveScan.Cancel();
        base.OnClosed(e);
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
                L.F("· 발매작 미리듣기 분석 {0}", CacheLocation.Format(albums)) + Environment.NewLine +
                L.F("· 내 곡 소리 분석 {0} · 취향 기준 {1}", CacheLocation.Format(library), CacheLocation.Format(model));
        }
    }

    private async void ClearRecs_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmWindow.Ask(this, L.T("저장된 추천 비우기"),
                L.T("저장된 추천을 모두 지우고 지금 기간부터 새로 계산할까요?\n다른 기간은 열 때 새로 계산해요 (기간마다 30초 안팎)."), L.T("비우기"))) return;
        ClearRecsButton.IsEnabled = false;
        ClearResult.Text = L.T("비우고 지금 기간을 새로 계산하는 중…");
        await _vm.ClearSavedRecsAsync();
        ClearResult.Text = L.T("비웠어요. 지금 기간은 새로 계산했고, 다른 기간은 열 때 새로 계산해요.");
        ShowStorage();
    }

    // 다른 설정 창: 이 창을 닫고 연다 (설정 창 위에 설정 창이 겹치지 않게)
    private void OpenRecommend_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Dispatcher.BeginInvoke(() => _vm.OpenRecommendSettingsCommand.Execute(null));
    }

    private void OpenConnector_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Dispatcher.BeginInvoke(() => _vm.OpenConnectorCommand.Execute(null));
    }

    private void OpenSoulseek_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Dispatcher.BeginInvoke(() => _vm.OpenSoulseekSettingsCommand.Execute(null));
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
