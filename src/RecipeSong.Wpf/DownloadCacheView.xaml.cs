using System.IO;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

public partial class DownloadCacheView : UserControl
{
    private AppSettings? _settings;
    private DownloadCache? _cache;
    public event Action? RestartRequired;
    public DownloadCacheView() => InitializeComponent();

    public void Configure(AppSettings settings, string? mlDirectory)
    {
        _settings = settings;
        _cache = new(DownloadCache.Location(settings, mlDirectory));
        CachePath.Text = _cache.DirectoryPath;
        ShowPending();
        _ = RefreshAsync();
    }

    private void ShowPending()
    {
        Result.Text = _settings?.PendingDownloadCacheDirectory is { } next ? L.F("다음 실행 때 {0}(으)로 옮겨요.", next) : "";
        if (_settings?.ClearDownloadCacheOnNextStart == true)
            Result.Text += "\n" + L.T("다음 실행 때 다운로드 캐시를 비워요. 모델·패키지는 필요할 때 다시 받아요.");
        Result.Text = Result.Text.Trim();
        CancelPendingButton.Visibility = Result.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Result.Text.Length > 0) RestartRequired?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (_cache == null) return;
        Actions.IsEnabled = false;
        CacheSizes.Text = L.T("용량을 확인하는 중…");
        try
        {
            var parts = await Task.Run(_cache.Sizes);
            CacheSizes.Text = L.F("패키지 {0} · 모델 {1} · 임시 {2}", CacheLocation.Format(parts[0].Bytes),
                CacheLocation.Format(parts[1].Bytes + parts[2].Bytes), CacheLocation.Format(parts[3].Bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CacheSizes.Text = ex.Message; }
        finally { Actions.IsEnabled = true; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("다운로드 캐시를 둘 폴더를 고르세요") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = Path.Combine(dialog.FolderName, "RecipeSong-download-cache");
        try { _cache?.ValidateDestination(target); }
        catch (IOException ex) { Result.Text = L.F("옮기지 못했어요: {0}", ex.Message); return; }
        _settings.PendingDownloadCacheDirectory = target;
        _settings.Save();
        ShowPending();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null || !ConfirmWindow.Ask(Window.GetWindow(this), L.T("다운로드 캐시 비우기"),
            L.T("다음 실행 때 앱 전용 패키지·모델 캐시를 비울까요? 다시 다운로드할 수 있어요. 취향 모델·곡 분석 결과·추천 기록은 보존해요."), L.T("다음 실행 때 비우기"))) return;
        _settings.ClearDownloadCacheOnNextStart = true;
        _settings.Save();
        ShowPending();
    }
    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.PendingDownloadCacheDirectory = null;
        _settings.ClearDownloadCacheOnNextStart = false;
        _settings.Save();
        ShowPending();
    }
}
