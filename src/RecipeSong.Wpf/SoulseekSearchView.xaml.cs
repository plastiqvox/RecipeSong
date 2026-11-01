using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RecipeSong.Core;
using RecipeSong.Core.Providers;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public partial class SoulseekSearchView : UserControl
{
    private readonly ReleaseRecommendation _album;
    private readonly AlbumDownloads _downloads;
    private readonly ObservableCollection<SoulseekFolder> _groups = [];
    private CancellationTokenSource? _search;
    private bool _disposed;
    private string _format = "";
    private int? _albumTrackCount;
    private readonly Guid? _replaceJobId;
    private readonly string? _excludedPeer;
    public event Action? BackRequested;
    public event Action? SettingsRequested;
    public event Action? DownloadsRequested;
    public event Action<Guid>? JobQueued;
    public bool WantsLogin { get; private set; }
    public void ResumeSearch() { if (WantsLogin) Search_Click(this, new RoutedEventArgs()); }
    public SoulseekSearchView(ReleaseRecommendation album, AlbumDownloads downloads, Guid? replaceJobId = null, string? excludedPeer = null, int? albumTrackCount = null)
    {
        InitializeComponent();
        _album = album;
        _albumTrackCount = albumTrackCount;
        _downloads = downloads;
        _replaceJobId = replaceJobId;
        _excludedPeer = excludedPeer;
        if (!string.IsNullOrWhiteSpace(excludedPeer))
        {
            ExcludedPeerText.Text = L.F("다른 상대 찾기 · 이전 상대: {0}", excludedPeer);
            ExcludedPeerText.Visibility = Visibility.Visible;
        }
        AlbumText.Text = $"{album.Artist} — {album.Album}";
        QueryBox.Text = $"{album.Artist} {album.Album}";
        Groups.ItemsSource = _groups;
        Groups.Items.Filter = item => item is SoulseekFolder group && _albumTrackCount is > 0 && group.Files.Count == _albumTrackCount;
        _groups.CollectionChanged += (_, _) => RefreshFilterCount();
        FormatBox.Items.Add(new ComboBoxItem { Content = L.T("전체"), Tag = "" });
        foreach (var format in new[] { "MP3", "FLAC", "OGG", "OPUS", "M4A", "AAC", "WAV", "AIFF", "AIF", "WMA", "ALAC" })
            FormatBox.Items.Add(new ComboBoxItem { Content = format, Tag = format });
        FormatBox.SelectedIndex = 0;
        StatusText.Text = L.T("판본 하나를 골라 수록곡을 확인한 뒤 받으세요.");
    }
    public void Stop() { _disposed = true; _search?.Cancel(); }
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Stop_Click(object sender, RoutedEventArgs e) => _search?.Cancel();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Downloads_Click(object sender, RoutedEventArgs e) => DownloadsRequested?.Invoke();
    public void StartSearch() => Search_Click(this, new RoutedEventArgs());

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Groups == null || FormatBox.SelectedItem is not ComboBoxItem { Tag: string format }) return;
        _format = format;
        foreach (var group in Groups.Items.SourceCollection.OfType<SoulseekFolder>()) group.FilterFormat(format);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Groups.ItemsSource);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription("Peer.QueueLength", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(SoulseekFolder.ReportedSpeed), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription("Peer.Username", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(SoulseekFolder.Folder), ListSortDirection.Ascending));
        }
        RefreshFilterCount();
    }

    private void RefreshFilterCount()
    {
        var total = Groups.Items.SourceCollection.OfType<SoulseekFolder>().Count();
        FilterCountText.Text = L.F("표시 {0} / 전체 {1}개 폴더", Groups.Items.Count, total);
        TrackCountText.Text = _albumTrackCount is > 0
            ? L.F("앨범 {0}곡과 일치하는 결과만 표시해요.", _albumTrackCount)
            : L.T("앨범 곡수를 확인한 뒤 일치하는 결과만 표시해요.");
        FilterEmptyText.Visibility = total > 0 && Groups.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        if (!_downloads.Session.IsLoggedIn) { WantsLogin = true; SettingsRequested?.Invoke(); return; }
        WantsLogin = false;
        _groups.Clear();
        SearchButton.IsEnabled = false; StopButton.IsEnabled = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _search = cancel;
        StatusText.Text = L.T("Soulseek에서 검색 중…");
        var seen = new HashSet<(string, string)>();
        try
        {
            if (_albumTrackCount is not > 0)
            {
                StatusText.Text = L.T("앨범 곡수를 확인 중…");
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var album = _album.DeezerAlbumId > 0
                    ? await new DeezerClient(http).AlbumAsync(_album.DeezerAlbumId, cancel.Token) : null;
                cancel.Token.ThrowIfCancellationRequested();
                _albumTrackCount = album?.TrackCount;
                if (_albumTrackCount is not > 0)
                    throw new InvalidOperationException(L.T("앨범 곡수를 확인하지 못해 검색 결과를 표시할 수 없어요. 다시 검색해 주세요."));
                Groups.Items.Refresh();
                RefreshFilterCount();
            }
            StatusText.Text = L.T("Soulseek에서 검색 중…");
            await _downloads.Session.SearchAsync(QueryBox.Text.Trim(), peer => Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || cancel.IsCancellationRequested) return;
                if (string.Equals(peer.Username, _excludedPeer, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var folder in peer.Files.Where(f => f.IsAudio).GroupBy(f => f.Folder))
                {
                    var key = (peer.Username, folder.Key);
                    if (!seen.Add(key)) continue;
                    var group = new SoulseekFolder(peer, folder.Key, folder.DistinctBy(f => f.Filename).ToArray());
                    group.FilterFormat(_format);
                    _groups.Add(group);
                }
                StatusText.Text = L.F("검색 중… 앨범 폴더 {0}개", Groups.Items.Count);
            }), cancel.Token);
            StatusText.Text = Groups.Items.Count == 0 ? L.T("앨범 곡수와 확장자에 맞는 결과가 없어요. 검색어나 확장자를 바꿔 보세요.") : L.F("앨범 폴더 {0}개 · 받을 판본을 고르세요.", Groups.Items.Count);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { StatusText.Text = L.T("검색을 멈췄어요. 받은 결과에서 선택할 수 있어요."); }
        catch (Exception ex) { StatusText.Text = _albumTrackCount is > 0 ? ex.Message : L.T("앨범 곡수를 확인하지 못해 검색 결과를 표시할 수 없어요. 다시 검색해 주세요."); }
        finally { _search = null; SearchButton.IsEnabled = true; StopButton.IsEnabled = false; }
    }

    private async void Receive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoulseekFolder group } button) return;
        button.IsEnabled = false;
        try
        {
            var files = group.Files.Where(f => f.Selected).Select(f => f.File).ToList();
            if (files.Count == 0) throw new InvalidOperationException(L.T("음원 파일을 선택하세요."));
            var settings = SoulseekSettings.Load();
            settings.Validate();
            var cover = _album.CoverUrl;
            if (string.IsNullOrWhiteSpace(cover))
            {
                // 메타데이터에 그림이 없을 때만 직접 고른다.
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.T("앨범아트를 선택하세요"), Filter = "Images|*.jpg;*.jpeg;*.png;*.webp" };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                cover = dialog.FileName;
            }
            await AlbumArchive.RunFfmpegAsync(settings.ResolveFfmpeg(), ["-version"], CancellationToken.None);
            var job = new AlbumDownload
            {
                Artist = _album.Artist, Album = _album.Album, RecordType = _album.RecordType, ReleaseDate = _album.ReleaseDate,
                CoverUrl = cover, Username = group.Peer.Username, Files = files, DownloadsDirectory = settings.DownloadsDirectory,
                ArchiveDirectory = settings.ArchiveDirectory, FfmpegPath = settings.ResolveFfmpeg(),
                Recommendation = _album with { CoverUrl = cover },
            };
            if (_replaceJobId is { } previous) await _downloads.CancelAsync(previous);
            await _downloads.QueueAsync(job, CancellationToken.None);
            StatusText.Text = L.T("전송함에 추가했어요. 모든 선택 파일이 완료되면 앨범아트와 ZIP을 만들어요.");
            JobQueued?.Invoke(job.Id);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }
}

public sealed class SoulseekPick(SoulseekFile file)
{
    public SoulseekFile File { get; } = file;
    public bool Selected { get; set; } = true;
}
public sealed class SoulseekFolder : ObservableObject
{
    private readonly IReadOnlyList<SoulseekPick> _allFiles;
    private IReadOnlyList<SoulseekPick> _files;
    public SoulseekPeer Peer { get; }
    public string Folder { get; }
    public IReadOnlyList<SoulseekPick> Files => _files;
    public SoulseekFolder(SoulseekPeer peer, string folder, IReadOnlyList<SoulseekFile> files)
    {
        Peer = peer;
        Folder = folder;
        _files = _allFiles = files.Select(f => new SoulseekPick(f)).ToArray();
    }
    public void FilterFormat(string format)
    {
        _files = format.Length == 0 ? _allFiles : _allFiles.Where(p => p.File.Format == format).ToArray();
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(Summary));
    }
    public string Summary => string.Join(" / ", Files.Select(p => p.File).Select(f =>
        (f.Format is "FLAC" or "WAV" or "AIFF" or "ALAC" ? f with { BitRate = null } : f).Quality).Distinct().Take(3))
        + " · " + L.F("{0}곡 · {1:0.0} MiB", Files.Count, Files.Sum(p => p.File.Size) / 1048576d);
    public bool HasAvailableSlot => Peer.HasFreeUploadSlot;
    public string Availability => HasAvailableSlot ? L.T("다운로드 가능") : L.T("다운로드 대기");
    public string SlotText => HasAvailableSlot ? L.T("상대 빈 슬롯: 1개 이상") : L.T("상대 빈 슬롯: 없음");
    public string QueueText => L.F("대기열 {0:N0}개", Peer.QueueLength);
    public int ReportedSpeed => Math.Max(0, Peer.UploadSpeed);
    public string SpeedText => ReportedSpeed <= 0 ? L.T("상대 속도: 정보 없음")
        : ReportedSpeed >= 1048576 ? L.F("상대 속도: {0:0.0} MiB/s", ReportedSpeed / 1048576d)
        : L.F("상대 속도: {0:0.0} KiB/s", ReportedSpeed / 1024d);
}
