using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public partial class SoulseekTransfersView : UserControl, IDisposable
{
    private readonly AlbumDownloads _downloads;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private bool _replacingJobs;
    private bool _favoriteBusy;
    private Guid? _selectedAlbum;
    private readonly Func<AlbumDownload, bool>? _isFavorite;
    private readonly Func<AlbumDownload, Task>? _toggleFavorite;
    public SoulseekTransfersView(AlbumDownloads downloads, Func<AlbumDownload, bool>? isFavorite = null, Func<AlbumDownload, Task>? toggleFavorite = null)
    {
        InitializeComponent();
        _downloads = downloads;
        _isFavorite = isFavorite;
        _toggleFavorite = toggleFavorite;
        UpdateSelectedAlbum();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var session = _downloads.Session;
            var settings = SoulseekSettings.Load();
            string Limit(int kib) => kib == 0 ? L.T("무제한") : $"{kib:N0} KiB/s";
            var transfers = session.Transfers;
            SummaryText.Text = session.Status + "\n" + L.F("다운로드 {0}/{1}슬롯 · {2:0} KiB/s / {3}", session.Downloads.Active, settings.DownloadSlots,
                transfers.Where(t => !t.Upload).Sum(t => t.Speed) / 1024d, Limit(settings.DownloadKiB)) + "\n" +
                L.F("업로드 {0}/{1}슬롯 · {2:0} KiB/s / {3}", session.Uploads.Active, settings.UploadSlots,
                    transfers.Where(t => t.Upload).Sum(t => t.Speed) / 1024d, Limit(settings.UploadKiB));
            var jobs = await _downloads.SnapshotAsync();
            var favoriteIds = jobs.Where(j => _isFavorite?.Invoke(j) == true).Select(j => j.Id).ToHashSet();
            var statuses = await Task.Run(() => jobs.AsEnumerable().Reverse().Select(j => new AlbumDownloadStatus(j, transfers) { IsFavorite = favoriteIds.Contains(j.Id) }).ToList());
            _replacingJobs = true;
            try
            {
                JobsList.ItemsSource = statuses;
                JobsList.SelectedItem = statuses.FirstOrDefault(j => j.Id == _selectedAlbum) ?? statuses.FirstOrDefault();
            }
            finally { _replacingJobs = false; }
            UpdateSelectedAlbum();
            Replace(UploadsList, transfers.Where(t => t.Upload).Reverse().ToList(), x => x.Id);
            var uploads = transfers.Where(t => t.Upload).ToList();
            UploadInfoText.Text = L.F("공유 파일 {0:N0}개 · 대기 {1}개 · 전송 {2}개 · 종료 {3}개", session.Shares.Files.Count,
                uploads.Count(t => !t.IsFinished && !t.IsActive), uploads.Count(t => t.IsActive), uploads.Count(t => t.IsFinished));
            UploadEmptyText.Visibility = uploads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _refreshing = false; }
    }

    private static void Replace<T>(ListBox list, List<T> items, Func<T, Guid> id)
    {
        Guid? selected = list.SelectedItem is T old ? id(old) : null;
        list.ItemsSource = items;
        if (selected != null) list.SelectedItem = items.FirstOrDefault(i => id(i) == selected);
    }
    public event Action<AlbumDownload>? SearchAgainRequested;
    public void SelectAlbum(Guid id) => _selectedAlbum = id;

    private void Album_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_replacingJobs) UpdateSelectedAlbum();
    }

    private void UpdateSelectedAlbum()
    {
        var selected = JobsList.SelectedItem as AlbumDownloadStatus;
        _selectedAlbum = selected?.Id;
        Replace(DownloadsList, selected?.Tracks.ToList() ?? [], x => x.Id);
        SelectedAlbumText.Text = selected == null ? L.T("왼쪽에서 앨범을 선택하세요.") : $"{selected.Job.Artist} — {selected.Job.Album}";
        SearchAgainButton.IsEnabled = selected != null;
        MoreButton.IsEnabled = JobsList.Items.Count > 0;
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        var state = selected?.Job.State;
        RetryButton.Visibility = Show(state is "failed" or "cancelled");
        // 열린 메뉴는 항목 위치를 유지한다. 실제 작업 가능 여부는 각 처리부에서 다시 확인한다.
        if (AlbumMenu.IsOpen) return;
        FavoriteMenu.IsEnabled = selected != null && _toggleFavorite != null;
        FavoriteMenu.Header = selected?.IsFavorite == true ? L.T("즐겨찾기 해제") : L.T("즐겨찾기 등록");
        FavoriteMenu.Visibility = Show(selected != null && _toggleFavorite != null);
        FolderMenu.Visibility = Show(selected?.Job.ArchivePath is { Length: > 0 });
        ArtworkMenu.Visibility = Show(state == "failed");
        CleanupMenu.Visibility = Show(selected != null && state is "done" or "cancelled" && !selected.Job.FilesRemoved);
        StopAlbumMenu.Visibility = Show(selected != null && state != "done" && !selected.Job.FilesRemoved);
        DeleteHistoryMenu.Visibility = Show(state is "done" or "failed" or "cancelled");
        var hasExtras = new[] { FavoriteMenu, FolderMenu, ArtworkMenu, CleanupMenu }.Any(item => item.Visibility == Visibility.Visible);
        TaskMenuSeparator.Visibility = Show(hasExtras && (StopAlbumMenu.Visibility == Visibility.Visible || DeleteHistoryMenu.Visibility == Visibility.Visible));
        HistoryMenuSeparator.Visibility = Show(hasExtras || StopAlbumMenu.Visibility == Visibility.Visible || DeleteHistoryMenu.Visibility == Visibility.Visible);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedAlbum();
        AlbumMenu.PlacementTarget = MoreButton;
        AlbumMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AlbumMenu.IsOpen = true;
    }

    private void SearchAgain_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is AlbumDownloadStatus { Job: var job }) SearchAgainRequested?.Invoke(job);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e) => await ToggleFavoriteAsync();
    private void JobRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem row || row.ContextMenu != null) return;
        var menu = new ContextMenu();
        var favorite = new MenuItem { Header = L.T("즐겨찾기 등록") };
        favorite.Click += JobFavorite_Click;
        menu.Items.Add(favorite);
        menu.Opened += JobMenu_Opened;
        row.ContextMenu = menu;
    }
    private void JobMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: ListBoxItem { DataContext: AlbumDownloadStatus row } } menu) return;
        JobsList.SelectedItem = row;
        // 자동 갱신으로 목록의 행이 교체되어도 우클릭한 앨범에 작업한다.
        menu.Tag = row.Job;
        var item = (MenuItem)menu.Items[0];
        item.Header = _isFavorite?.Invoke(row.Job) == true ? L.T("즐겨찾기 해제") : L.T("즐겨찾기 등록");
        item.IsEnabled = _toggleFavorite != null && !_favoriteBusy;
    }
    private async void JobFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { Tag: AlbumDownload job } } item) return;
        item.IsEnabled = false;
        await ToggleFavoriteAsync(job);
    }
    public async Task ToggleFavoriteAsync()
    {
        if (_toggleFavorite == null || JobsList.SelectedItem is not AlbumDownloadStatus { Job: var job }) return;
        await ToggleFavoriteAsync(job);
    }
    private async Task ToggleFavoriteAsync(AlbumDownload job)
    {
        if (_toggleFavorite == null || _favoriteBusy) return;
        _favoriteBusy = true;
        FavoriteMenu.IsEnabled = false;
        try { await _toggleFavorite(job); await RefreshAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _favoriteBusy = false; FavoriteMenu.IsEnabled = JobsList.SelectedItem != null && _toggleFavorite != null; }
    }
    public void Dispose() => _timer.Stop();
    public void ShowUploads(bool uploads)
    {
        TransferTabs.SelectedIndex = uploads ? 1 : 0;
        TransferTitle.Text = uploads ? L.T("업로드") : L.T("다운로드");
    }
    private async void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        try { if (JobsList.SelectedItem is AlbumDownloadStatus { Job: var job }) { await _downloads.CancelAsync(job.Id); await RefreshAsync(); } }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        try { if (JobsList.SelectedItem is AlbumDownloadStatus { Job: var job }) { await _downloads.RetryAsync(job.Id, SoulseekSettings.Load()); await RefreshAsync(); } }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not AlbumDownloadStatus { Id: var id }) return;
        CleanupMenu.IsEnabled = false;
        try
        {
            var count = await _downloads.RetryCleanupAsync(id);
            await RefreshAsync();
            StatusText.Text = count > 0 ? L.F("파일 정리 완료: {0}개 앨범", count) : L.T("정리할 파일이 없거나 아직 정리를 완료하지 못했어요.");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { CleanupMenu.IsEnabled = true; }
    }
    private async void ChangeArt_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not AlbumDownloadStatus { Job: { State: "failed" } job }) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.T("앨범아트를 선택하세요"), Filter = "Images|*.jpg;*.jpeg;*.png;*.webp" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await _downloads.RetryAsync(job.Id, SoulseekSettings.Load(), dialog.FileName); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void CancelUpload_Click(object sender, RoutedEventArgs e) { if (UploadsList.SelectedItem is SoulseekTransfer row) _downloads.Session.Cancel(row.Id); }
    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not AlbumDownloadStatus { Job: var job }) { StatusText.Text = L.T("삭제할 앨범 기록을 선택하세요."); return; }
        await DeleteHistoryAsync(job.Id);
    }
    private async void ClearHistory_Click(object sender, RoutedEventArgs e) => await DeleteHistoryAsync(null);
    private async Task DeleteHistoryAsync(Guid? id)
    {
        try
        {
            var count = await _downloads.DeleteHistoryAsync(id);
            StatusText.Text = id != null && count == 0 ? L.T("진행 중이거나 정리할 파일이 남아 있어요. 작업 중단·파일 정리를 먼저 완료하세요.")
                : L.F("앨범 기록 {0}개를 삭제했어요. 받은 파일은 보존했어요.", count);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is not AlbumDownloadStatus { Job.ArchivePath: { } zip }) return;
        var folder = Path.GetDirectoryName(zip);
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
