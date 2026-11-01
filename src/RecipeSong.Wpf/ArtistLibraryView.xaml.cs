using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Core.Providers;

namespace RecipeSong.Wpf;

public partial class ArtistLibraryView : UserControl, IDisposable
{
    private readonly Func<CancellationToken, Task<ArtistAlbumDiscovery>> _load;
    private ArtistAlbumDiscovery? _discovery;
    private IReadOnlyList<DiscoveredAlbum> _albums = [];
    private CancellationTokenSource? _request;
    private ReleaseRecommendation? _selected;
    private bool _disposed;
    private bool _updating;
    private bool _applyingIdentity;
    private CancellationTokenSource _coverRequests = new();
    private readonly SemaphoreSlim _coverSlot = new(1, 1);
    private readonly Dictionary<string, Task<string?>> _coverTasks = [];
    public event Action<ReleaseRecommendation>? SearchRequested;
    private sealed record Source(string Name, Playlist? Playlist)
    {
        public override string ToString() => Name;
    }
    private sealed record Candidate(DzArtist Artist, int AlbumMatches, int? TrackMatches)
    {
        public string Label => L.F("{0} · ID {1} · 보유 앨범 {2}개 일치", Artist.Name, Artist.Id, AlbumMatches)
            + (TrackMatches is { } count ? " · " + L.F("수록곡 {0}개 일치", count) : "");
        public override string ToString() => Label;
    }

    public ArtistLibraryView(Func<CancellationToken, Task<ArtistAlbumDiscovery>> load)
    {
        InitializeComponent();
        _load = load;
    }

    public async Task InitializeAsync()
    {
        if (_discovery != null || _disposed) return;
        var token = BeginRequest();
        StatusText.Text = L.T("플레이리스트를 불러오는 중…");
        try
        {
            var discovery = await _load(token);
            if (token.IsCancellationRequested) return;
            _discovery = discovery;
            PlaylistBox.ItemsSource = new[] { new Source(L.T("전체 직접 만든 플리"), null) }
                .Concat(discovery.Playlists.Select(p => new Source(p.Name, p))).ToArray();
            PlaylistBox.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusText.Text = ex.Message; }
    }

    private CancellationToken BeginRequest()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        return _request.Token;
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        _coverRequests.Cancel();
        _coverRequests.Dispose();
        _coverRequests = new();
        _coverTasks.Clear();
        _discovery = null;
        Artists.ItemsSource = null;
        await InitializeAsync();
    }

    private void Playlist_Changed(object sender, SelectionChangedEventArgs e) => RefreshArtists();
    private void Source_Changed(object sender, RoutedEventArgs e) => RefreshArtists();
    private void Query_Changed(object sender, TextChangedEventArgs e) => RefreshArtists();
    private async void ArtistCover_Loaded(object sender, RoutedEventArgs e) => await LoadArtistCoverAsync((FrameworkElement)sender);
    private async void ArtistCover_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true } element) await LoadArtistCoverAsync(element);
    }
    private async Task LoadArtistCoverAsync(FrameworkElement element)
    {
        if (_disposed || _discovery == null || element.DataContext is not ArtistAlbumThumbnail thumbnail) return;
        var album = thumbnail.Album;
        var token = _coverRequests.Token;
        var key = album.Artist + "\u001f" + album.Album;
        try
        {
            if (!_coverTasks.TryGetValue(key, out var task))
                _coverTasks[key] = task = GetArtistCoverAsync(_discovery, album, token);
            var cover = await task;
            if (!token.IsCancellationRequested) thumbnail.CoverUrl = cover;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) _coverTasks.Remove(key); }
    }
    private async Task<string?> GetArtistCoverAsync(ArtistAlbumDiscovery discovery, AlbumSummary album, CancellationToken token)
    {
        await _coverSlot.WaitAsync(token);
        try { return await discovery.ArtistCoverAsync(album, token); }
        finally { _coverSlot.Release(); }
    }
    private void RefreshArtists()
    {
        if (_discovery == null || Artists == null) return;
        var source = (PlaylistBox.SelectedItem as Source)?.Playlist;
        Artists.ItemsSource = _discovery.Musicians(source, FollowBox.IsChecked == true)
            .Where(a => a.Name.Contains(ArtistQuery.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        StatusText.Text = Artists.Items.Count == 0 ? L.T("직접 만든 플리에 곡을 담거나 팔로우를 추가해 주세요.")
            : L.F("아티스트 {0}명 · 취향순 (좋아요·재생 수 반영)", Artists.Items.Count);
    }

    private async void Artist_Changed(object sender, SelectionChangedEventArgs e)
    {
        var token = BeginRequest();
        ClearAlbums();
        _updating = true;
        CandidateBox.ItemsSource = null;
        CandidateList.ItemsSource = null;
        CandidateChoices.Visibility = Visibility.Collapsed;
        _updating = false;
        FindButton.IsEnabled = false;
        MySongs.ItemsSource = null;
        ArtistTitle.Text = L.T("왼쪽에서 아티스트를 골라 주세요.");
        ResetIdentityButton.IsEnabled = false;
        if (_discovery == null || Artists.SelectedItem is not PlaylistMusician musician) return;
        ArtistTitle.Text = musician.Name;
        ResetIdentityButton.IsEnabled = true;
        MySongs.ItemsSource = musician.Tracks.OrderBy(t => t.Album).ThenBy(t => t.Title).ToArray();
        StatusText.Text = L.T("보유 앨범·수록곡으로 아티스트 ID를 확인하는 중…");
        try
        {
            var identity = await _discovery.ResolveArtistAsync(musician, token);
            if (token.IsCancellationRequested) return;
            var candidates = identity.Candidates.Select(a => new Candidate(a.Artist, a.AlbumMatches, a.TrackMatches)).ToArray();
            CandidateBox.ItemsSource = candidates;
            CandidateList.ItemsSource = candidates;
            CandidateChoices.Visibility = identity.SelectedId == null && candidates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _applyingIdentity = true;
            try { CandidateBox.SelectedItem = candidates.FirstOrDefault(a => a.Artist.Id == identity.SelectedId); }
            finally { _applyingIdentity = false; }
            StatusText.Text = identity.FromStore ? L.T("DB에 저장된 아티스트 ID를 불러왔어요. ID 다시 찾기로 초기화할 수 있어요.")
                : identity.SelectedId != null ? musician.DeezerId > 0 && musician.TopAlbums.Count == 0
                ? L.T("저장된 팔로우 ID를 선택했어요. 확인 후 앨범을 찾아보세요.")
                : L.T("보유 정보와 일치하는 아티스트 ID를 선택했어요. 확인 후 앨범을 찾아보세요.")
                : candidates.Length > 0 ? L.T("아티스트 ID를 확정하지 못했어요. 일치 정보를 보고 직접 선택해 주세요.") : L.T("카탈로그에서 아티스트를 찾지 못했어요.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusText.Text = ex.Message; }
    }

    private void Candidate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Albums == null || _updating) return;
        _updating = true;
        CandidateList.SelectedItem = CandidateBox.SelectedItem;
        _updating = false;
        BeginRequest();
        ClearAlbums();
        FindButton.IsEnabled = CandidateBox.SelectedItem != null;
        if (!_applyingIdentity && _discovery != null && Artists.SelectedItem is PlaylistMusician musician && CandidateBox.SelectedItem is Candidate candidate)
        {
            try { _discovery.ConfirmArtist(musician, candidate.Artist); StatusText.Text = L.T("선택한 아티스트 ID를 DB에 저장했어요."); }
            catch (Exception ex) { FindButton.IsEnabled = false; StatusText.Text = ex.Message; }
        }
    }
    private void CandidateList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) CandidateBox.SelectedItem = CandidateList.SelectedItem;
    }
    private void ResetIdentity_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery == null || Artists.SelectedItem is not PlaylistMusician musician) return;
        BeginRequest();
        try { _discovery.ForgetArtist(musician.Name); Artist_Changed(sender, new SelectionChangedEventArgs(ListBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>())); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void ClearAlbums()
    {
        _albums = [];
        _updating = true;
        Albums.ItemsSource = null;
        _updating = false;
        _selected = null;
        SearchButton.IsEnabled = false;
        AlbumSongs.ItemsSource = null;
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery == null || CandidateBox.SelectedItem is not Candidate candidate) return;
        var token = BeginRequest();
        ClearAlbums();
        FindButton.IsEnabled = false;
        StatusText.Text = L.T("전체 디스코그래피를 라이브러리와 비교하는 중…");
        try
        {
            var albums = await _discovery.AlbumsAsync(candidate.Artist, token, (Artists.SelectedItem as PlaylistMusician)?.Name);
            if (token.IsCancellationRequested) return;
            _albums = albums;
            RefreshAlbums();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusText.Text = ex.Message; }
        finally { if (!token.IsCancellationRequested) FindButton.IsEnabled = true; }
    }

    private void Type_Changed(object sender, RoutedEventArgs e) { if (Albums != null) RefreshAlbums(); }
    private void RefreshAlbums()
    {
        Albums.ItemsSource = _albums.Where(a => ArtistAlbumDiscovery.MatchesType(a.Album, AlbumBox.IsChecked == true,
            EpBox.IsChecked == true, SingleBox.IsChecked == true, LiveBox.IsChecked == true, CompilationBox.IsChecked == true)).ToArray();
        StatusText.Text = L.F("놓친 앨범 {0}개 · 일부 보유와 다른 판본 포함", Albums.Items.Count);
    }

    private async void Album_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var token = BeginRequest();
        _selected = null;
        SearchButton.IsEnabled = false;
        AlbumSongs.ItemsSource = null;
        if (_discovery == null || Albums.SelectedItem is not DiscoveredAlbum album) return;
        FindButton.IsEnabled = CandidateBox.SelectedItem != null;
        StatusText.Text = L.T("수록곡을 불러오는 중…");
        try
        {
            var detail = await _discovery.DetailsAsync(album, token);
            if (token.IsCancellationRequested) return;
            _selected = detail.Recommendation;
            AlbumSongs.ItemsSource = detail.Tracks.Select((t, i) => $"{i + 1:00}  {t.Title}").ToArray();
            SearchButton.IsEnabled = detail.Tracks.Count > 0;
            StatusText.Text = album.Summary;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusText.Text = ex.Message; }
    }

    private void Search_Click(object sender, RoutedEventArgs e) { if (_selected != null) SearchRequested?.Invoke(_selected); }
    public void Dispose() { _disposed = true; _request?.Cancel(); _request?.Dispose(); _coverRequests.Cancel(); _coverRequests.Dispose(); }
}
