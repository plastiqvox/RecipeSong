using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public partial class SoulseekWorkspaceView : UserControl, IDisposable
{
    private readonly AlbumDownloads _downloads;
    private readonly SoulseekSettingsView _settings;
    private readonly SoulseekTransfersView _transfers;
    private readonly ContentControl _transferHost = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SoulseekSearchView? _search;
    private ReleaseRecommendation? _album;
    private Guid? _replacementId;
    private readonly Func<AlbumDownload, ReleaseRecommendation>? _recommendation;
    public event Action? RecommendationsRequested;

    public SoulseekWorkspaceView(AlbumDownloads downloads, Func<AlbumDownload, ReleaseRecommendation>? recommendation = null,
        Func<AlbumDownload, bool>? isFavorite = null, Func<AlbumDownload, Task>? toggleFavorite = null)
    {
        InitializeComponent();
        _downloads = downloads;
        _recommendation = recommendation;
        _settings = new SoulseekSettingsView(downloads.Session);
        _transfers = new SoulseekTransfersView(downloads, isFavorite, toggleFavorite);
        _transferHost.Content = _transfers;
        ((TabItem)Sections.Items[3]).Content = _settings;
        _settings.LoginSucceeded += () =>
        {
            if (_search?.WantsLogin != true) return;
            ShowSection("search");
            _search.ResumeSearch();
        };
        _transfers.SearchAgainRequested += job =>
        {
            var album = _recommendation?.Invoke(job) ?? job.Recommendation ?? new ReleaseRecommendation(job.Artist, job.Album, job.RecordType,
                job.ReleaseDate, null, job.CoverUrl, 0, null, null, Region.International, null, null, null, [], [], false, 0);
            ShowAlbum(album, job.Id, job.Username);
            _search?.StartSearch();
        };
        _timer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) => { RefreshStatus(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        RefreshStatus();
    }

    public void ShowAlbum(ReleaseRecommendation album, Guid? replaceJobId = null, string? excludedPeer = null)
    {
        if (_album != album || _replacementId != replaceJobId || replaceJobId != null)
        {
            _search?.Stop();
            _album = album;
            _replacementId = replaceJobId;
            _search = new SoulseekSearchView(album, _downloads, replaceJobId, excludedPeer);
            _search.BackRequested += () => RecommendationsRequested?.Invoke();
            _search.SettingsRequested += () => ShowSection("settings");
            _search.DownloadsRequested += () => ShowSection("downloads");
            _search.JobQueued += id => { _transfers.SelectAlbum(id); ShowSection("downloads"); };
            SearchHost.Content = _search;
            SearchHost.Visibility = Visibility.Visible;
            EmptySearch.Visibility = Visibility.Collapsed;
        }
        ShowSection("search");
    }

    public void ShowSection(string section) => Sections.SelectedIndex = section switch
    {
        "downloads" => 1, "uploads" => 2, "settings" => 3, _ => 0,
    };

    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Sections || _transfers == null) return;
        // 같은 전송 화면을 옮겨 써 선택 상태와 상세 정보를 유지한다.
        var down = (TabItem)Sections.Items[1];
        var up = (TabItem)Sections.Items[2];
        if (Sections.SelectedIndex is 1 or 2)
        {
            down.Content = null;
            up.Content = null;
            _transfers.ShowUploads(Sections.SelectedIndex == 2);
            ((TabItem)Sections.SelectedItem).Content = _transferHost;
        }
    }

    private void RefreshStatus()
    {
        var session = _downloads.Session;
        ConnectionText.Text = session.Status;
        if (!session.IsLoggedIn) return;
        var capacity = session.Downloads.Capacity;
        ConnectionText.Text += "\n" + L.F("내 빈 다운로드 슬롯: {0}/{1}개", capacity.Free, capacity.Total);
    }
    private void Recommendations_Click(object sender, RoutedEventArgs e) => RecommendationsRequested?.Invoke();
    public void Dispose()
    {
        _timer.Stop();
        _search?.Stop();
        _settings.Dispose();
        _transfers.Dispose();
    }
}
