using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;
using RecipeSong.Wpf;

static partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("RECIPESONG_DATA", Path.Combine(output, "data"));
        CacheLocation.Directory = Path.Combine(output, "data");
        var app = new UiCheckApplication();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args.Length > 1 && args[1] == "--week-range")
        {
            foreach (var language in new[] { "ko", "en" })
            {
                L.Init(language);
                CheckWeekRange(output, language);
                CheckSharedWorkspace(output, language);
            }
            Console.WriteLine("Week range and source navigation UI checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--download-storage")
        {
            CheckDownloadStorage(output);
            Console.WriteLine("Download storage UI checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--artist-library")
        {
            foreach (var language in new[] { "ko", "en" })
            {
                L.Init(language);
                CheckArtistLibrary(output, language);
            }
            Console.WriteLine("Artist library UI checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--artist-filters")
        {
            CheckArtistFilters();
            Console.WriteLine("Artist album filter persistence checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--card-actions")
        {
            CheckCardActions(output);
            Console.WriteLine("Card action layout checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--search-confirmation")
        {
            CheckSearchConfirmation(output);
            Console.WriteLine("Search confirmation checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--taste-feedback")
        {
            CheckTasteFeedback(output);
            Console.WriteLine("Track feedback UI checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--evidence")
        {
            CheckEvidence(output);
            Console.WriteLine("Recommendation evidence checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--playback")
        {
            CheckExcludedPlayback(output);
            Console.WriteLine("Excluded playback checks passed.");
            return 0;
        }
        if (args.Length > 1 && args[1] == "--pipeline")
        {
            L.Init(args.Length > 2 ? args[2] : "ko");
            CheckPipeline(output);
            Console.WriteLine("Pipeline UI checks passed.");
            return 0;
        }
        CheckRecommendationSettingsEdits(output);
        // OnStartup과 Application.Run은 호출하지 않는다. 사용자 앱·설정·소리 서비스·네트워크에 접근하지 않는다.
        foreach (var language in new[] { "ko", "en" })
        {
            L.Init(language);
            CheckRecommendationCalculationSettings(output, language);
            var settings = new SoulseekSettings { Username = "example-user", SharedDirectories = [@"D:\Music", @"E:\Albums"], ArchiveDirectory = Path.Combine(output, "albums"), DownloadsDirectory = Path.Combine(output, "originals") };
            settings.Save();
            var downloads = new AlbumDownloads();
            try
            {
                var actionAlbum = new ReleaseRecommendation("Test artist", "A long album title for layout review", "album", new(2026, 11, 1), "Test label", null, 1, null, null, Region.International, null, null, null, [], [], false, 0);
                CheckAlbumActions(actionAlbum, output, language);
                using var settingsView = new SoulseekSettingsView(downloads.Session);
                Render(settingsView, Path.Combine(output, language + "-settings.png"), 780, 800);
                var import = new SoulseekImportWindow(new SoulseekImportedSettings("example-user", "not-a-real-password", @"D:\Downloads", [@"E:\Music", @"F:\Albums"], false));
                RenderWindow(import, Path.Combine(output, language + "-import.png"), 680, 580);
                var album = new ReleaseRecommendation("Test artist", "A long album title for layout review", "album", new(2026, 11, 1), "Test label", null, 1, null, null, Region.International, null, null, null, [], [], false, 0);
                var search = new SoulseekSearchView(album, downloads, albumTrackCount: 10);
                var files = Enumerable.Range(1, 10).Select(i => new SoulseekFile($"Music\\Album\\{i:00} Track.flac", 32000000, 950, 44100, 16)).ToArray();
                var peer = new SoulseekPeer("example-peer", true, 4, 1000000, files);
                ((ItemsControl)search.FindName("Groups")).ItemsSource = new[] { new SoulseekFolder(peer, "Music\\Album", files), new SoulseekFolder(peer with { HasFreeUploadSlot = false, QueueLength = 3 }, "Music\\Other edition", files) };
                ((ComboBox)search.FindName("FormatBox")).SelectedIndex = 1;
                ((ComboBox)search.FindName("FormatBox")).SelectedIndex = 0;
                Render(search, Path.Combine(output, language + "-search.png"), 390, 800);
                search.Stop();
                using var transfers = new SoulseekTransfersView(downloads);
                ((TextBlock)transfers.FindName("SummaryText")).Text = L.F("다운로드 {0}/{1}슬롯 · {2:0} KiB/s / {3}", 2, 3, 1800, L.T("무제한")) + "\n" + L.F("업로드 {0}/{1}슬롯 · {2:0} KiB/s / {3}", 1, 2, 420, "1024 KiB/s");
                var job = new AlbumDownload { Artist = album.Artist, Album = album.Album, State = "downloading", Files = files.Take(2).ToList() };
                var downloadRows = new[] { new SoulseekTransfer(Guid.NewGuid(), false, "example-peer", files[0].Filename, L.T("전송 중"), 20000000, 32000000, 1000000, null)
                    { AlbumJobId = job.Id, IsActive = true, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-20), LocalPath = @"D:\Downloads\01 Track.flac", Endpoint = "127.0.0.1:50001" } };
                ((ListBox)transfers.FindName("JobsList")).ItemsSource = new[] { new AlbumDownloadStatus(job, downloadRows), new AlbumDownloadStatus(new AlbumDownload { Artist = "Another artist", Album = "Another album", State = "packing", Files = files.Take(2).ToList() }, []) };
                ((ListBox)transfers.FindName("JobsList")).SelectedIndex = 0;
                Render(transfers, Path.Combine(output, language + "-transfers.png"), 900, 720);
                var queued = downloadRows[0] with { Bytes = 0, Speed = 0, State = L.T("대기 중"), IsActive = false, IsRemoteQueued = true, QueuePosition = 17, QueueCheckedAt = DateTimeOffset.UtcNow, StartedAt = null };
                ((ListBox)transfers.FindName("JobsList")).ItemsSource = new[] { new AlbumDownloadStatus(job, [queued]) };
                ((ListBox)transfers.FindName("JobsList")).SelectedIndex = 0;
                Render(transfers, Path.Combine(output, language + "-download-queue.png"), 900, 720);
                CheckDownloadQueue(queued);
                var requeued = queued with { Bytes = 20000000, HasTransferred = true, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), WaitingSince = DateTimeOffset.UtcNow.AddSeconds(-30) };
                var requeuedAlbum = new AlbumDownloadStatus(job, [requeued]);
                if (!requeuedAlbum.Display.Contains(L.T("다운로드 재대기 중…")) || !requeuedAlbum.ProgressText.Contains(L.F("재대기 {0}곡", 1))) throw new Exception("Album requeue summary is missing.");
                ((ListBox)transfers.FindName("JobsList")).ItemsSource = new[] { requeuedAlbum };
                ((ListBox)transfers.FindName("JobsList")).SelectedIndex = 0;
                Render(transfers, Path.Combine(output, language + "-download-requeue.png"), 900, 720);
                using var uploads = new SoulseekTransfersView(downloads);
                uploads.ShowUploads(true);
                ((TextBlock)uploads.FindName("UploadInfoText")).Text = L.F("공유 파일 {0:N0}개 · 대기 {1}개 · 전송 {2}개 · 종료 {3}개", 4200, 1, 1, 0);
                ((TextBlock)uploads.FindName("UploadEmptyText")).Visibility = Visibility.Collapsed;
                ((ListBox)uploads.FindName("UploadsList")).ItemsSource = new[]
                {
                    new SoulseekTransfer(Guid.NewGuid(), true, "example-peer", @"Music\Artist\Album\01 Track.flac", L.T("전송 중"), 4000000, 10000000, 420 * 1024, null)
                    { LocalPath = @"D:\Music\Artist\Album\01 Track.flac", Endpoint = "127.0.0.1:50001", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), IsActive = true },
                    new SoulseekTransfer(Guid.NewGuid(), true, "another-peer", @"Music\Artist\Album\02 Track.flac", L.T("대기 중"), 0, 10000000, 0, null)
                    { LocalPath = @"D:\Music\Artist\Album\02 Track.flac", QueuePosition = 2 },
                };
                Render(uploads, Path.Combine(output, language + "-uploads.png"), 650, 600);
                CheckWorkspace(downloads, album, output, language);
                CheckExtensionFilter(downloads, album, output, language);
                CheckTrackCountFilter(downloads, album, output, language);
                CheckArtistLibrary(output, language);
                CheckSearchOrder(downloads, album, output, language);
                CheckDownloadProgress(Path.Combine(output, language + "-progress-data"));
            }
            finally { OnUi(() => downloads.DisposeAsync().AsTask()); }
        }
        Console.WriteLine("한국어·영어 설정/앨범 검색/전송함 렌더 완료: " + output);
        return 0;
    }

    static void CheckWorkspace(AlbumDownloads downloads, ReleaseRecommendation album, string output, string language)
    {
        using var workspace = new SoulseekWorkspaceView(downloads);
        workspace.SetReturnTarget(L.T("아티스트 목록으로"));
        var shell = new TabControl();
        shell.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RecipeSong.Wpf;component/WorkspaceStyles.xaml", UriKind.Relative) });
        shell.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTabs");
        var recommended = new TabItem { Header = L.T("추천"), Content = new TextBlock { Text = "Recommendation view" } };
        var soulseek = new TabItem { Header = L.T("Soulseek"), Content = workspace };
        recommended.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTab");
        soulseek.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTab");
        shell.Items.Add(recommended); shell.Items.Add(soulseek); shell.SelectedIndex = 1;
        Render(shell, Path.Combine(output, language + "-workspace-empty.png"), 1040, 680);
        workspace.ShowAlbum(album);
        var host = (ContentControl)workspace.FindName("SearchHost");
        var search = (SoulseekSearchView)host.Content;
        if (!Equals(((Button)search.FindName("BackButton")).ToolTip, L.T("아티스트 목록으로")))
            throw new Exception("New searches lost their artist return label.");
        workspace.SetReturnTarget(L.T("추천으로"));
        if (!Equals(((Button)search.FindName("BackButton")).ToolTip, L.T("추천으로")))
            throw new Exception("Reused search views retained a stale return label.");
        ((TextBox)search.FindName("QueryBox")).Text = "custom query";
        var files = new[] { new SoulseekFile(@"Music\Album\01 Track.flac", 32000000, 950, 44100, 16) };
        var groups = new[] { new SoulseekFolder(new SoulseekPeer("example-peer", true, 0, 1000000, files), @"Music\Album", files) };
        ((ItemsControl)search.FindName("Groups")).ItemsSource = groups;
        ((ComboBox)search.FindName("FormatBox")).SelectedIndex = 1;
        ((ComboBox)search.FindName("FormatBox")).SelectedIndex = 0;
        shell.SelectedIndex = 0; shell.SelectedIndex = 1;
        ((Button)search.FindName("DownloadsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var sections = (TabControl)workspace.FindName("Sections");
        if (sections.SelectedIndex != 1) throw new Exception("Download status navigation failed.");
        var transferHost = ((TabItem)sections.Items[1]).Content;
        var transferView = (SoulseekTransfersView)((ContentControl)transferHost).Content;
        var job = new AlbumDownload { Artist = album.Artist, Album = album.Album, State = "downloading", Files = files.ToList() };
        var rows = new[] { new SoulseekTransfer(Guid.NewGuid(), false, "example-peer", files[0].Filename, L.T("전송 중"), 16000000, 32000000, 1000000, null) { AlbumJobId = job.Id, IsActive = true } };
        ((ListBox)transferView.FindName("JobsList")).ItemsSource = new[] { new AlbumDownloadStatus(job, rows) };
        ((ListBox)transferView.FindName("JobsList")).SelectedIndex = 0;
        Render(shell, Path.Combine(output, language + "-workspace-downloads.png"), 1040, 680);
        workspace.ShowSection("uploads");
        if (!ReferenceEquals(transferHost, ((TabItem)sections.Items[2]).Content)) throw new Exception("Transfer view was recreated.");
        workspace.ShowSection("settings");
        var settings = (SoulseekSettingsView)((TabItem)sections.Items[3]).Content;
        var username = (TextBox)settings.FindName("UsernameBox");
        username.Text = "unsaved-user";
        workspace.ShowAlbum(album);
        if (!ReferenceEquals(search, host.Content) || ((TextBox)search.FindName("QueryBox")).Text != "custom query"
            || !ReferenceEquals(groups, ((ItemsControl)search.FindName("Groups")).ItemsSource)) throw new Exception("Search state was lost.");
        Render(shell, Path.Combine(output, language + "-workspace-search.png"), 1040, 680);
        // 로그인 전 검색은 팝업 대신 같은 탭의 설정으로 안내한다.
        ((Button)search.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (sections.SelectedIndex != 3 || username.Text != "unsaved-user") throw new Exception("Inline settings navigation failed.");
        Render(shell, Path.Combine(output, language + "-workspace-settings.png"), 1040, 680);
        workspace.ShowSection("uploads");
        Render(shell, Path.Combine(output, language + "-workspace-uploads.png"), 1040, 680);
    }

    static void CheckAlbumActions(ReleaseRecommendation album, string output, string language)
    {
        var oldCache = CacheLocation.Directory;
        CacheLocation.Directory = Path.Combine(output, language + "-album-actions-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(CacheLocation.Directory);
        new SoulseekSettings { DownloadsDirectory = Path.Combine(output, "test-originals"), ArchiveDirectory = Path.Combine(output, "test-archives") }.Save();
        var first = new AlbumDownload { Artist = album.Artist, Album = album.Album, Username = "old-peer", State = "failed", Recommendation = album,
            DownloadsDirectory = Path.Combine(output, "test-originals"), ArchiveDirectory = Path.Combine(output, "test-archives"), Files = [new(@"First\01.flac", 32000000), new(@"First\02.flac", 32000000)] };
        var second = new AlbumDownload { Artist = "Other artist", Album = "Other album", State = "done", FilesRemoved = true, Files = [new(@"Other\01.mp3", 8000000)] };
        File.WriteAllText(Path.Combine(CacheLocation.Directory, "soulseek-native-downloads.json"), System.Text.Json.JsonSerializer.Serialize(new[] { first, second }));
        var downloads = new AlbumDownloads();
        var saved = new SavedReleases(Path.Combine(CacheLocation.Directory, "saved.json"));
        Guid? toggledAlbum = null;
        try
        {
            using var workspace = new SoulseekWorkspaceView(downloads, j => j.Recommendation ?? album,
                j => j.Recommendation is { } rec && saved.Contains(rec.DeezerAlbumId),
                j => { toggledAlbum = j.Id; if (saved.Contains(album.DeezerAlbumId)) saved.Remove(album.DeezerAlbumId); else saved.Add(album); return Task.CompletedTask; });
            workspace.ShowSection("downloads");
            var sections = (TabControl)workspace.FindName("Sections");
            var view = (SoulseekTransfersView)((ContentControl)((TabItem)sections.Items[1]).Content).Content;
            OnUi(view.RefreshAsync);
            var jobs = (ListBox)view.FindName("JobsList");
            var tracks = (ListBox)view.FindName("DownloadsList");
            jobs.SelectedItem = jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == first.Id);
            if (tracks.Items.Count != 2 || tracks.Items.Cast<SoulseekTransfer>().Any(t => t.AlbumJobId != first.Id)) throw new Exception("Album selection did not filter tracks.");
            OnUi(view.RefreshAsync);
            if (((AlbumDownloadStatus)jobs.SelectedItem).Id != first.Id || tracks.Items.Count != 2) throw new Exception("Album selection was lost during refresh.");
            OnUi(view.ToggleFavoriteAsync);
            var menu = ((Button)view.FindName("MoreButton")).ContextMenu;
            MenuItem Item(string name) => menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
            if (!saved.Contains(album.DeezerAlbumId) || Item("FavoriteMenu").Header?.ToString() != L.T("즐겨찾기 해제")) throw new Exception("Favorite menu registration failed.");
            if (((Button)view.FindName("RetryButton")).Visibility != Visibility.Visible || Item("ArtworkMenu").Visibility != Visibility.Visible
                || Item("CleanupMenu").Visibility != Visibility.Collapsed) throw new Exception("Failed album actions are incorrect.");
            Render(workspace, Path.Combine(output, language + "-album-actions.png"), 1040, 680);
            RenderMenu(menu, Path.Combine(output, language + "-album-menu.png"));
            OnUi(view.ToggleFavoriteAsync);
            if (saved.Contains(album.DeezerAlbumId)) throw new Exception("Favorite removal failed.");
            Render(workspace, Path.Combine(output, language + "-album-context-target.png"), 1040, 680);
            var firstRow = (ListBoxItem)jobs.ItemContainerGenerator.ContainerFromItem(jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == first.Id));
            firstRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var context = firstRow.ContextMenu;
            var favorite = (MenuItem)context.Items[0];
            jobs.SelectedItem = jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == second.Id);
            context.PlacementTarget = firstRow;
            context.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            if (((AlbumDownloadStatus)jobs.SelectedItem).Id != first.Id || favorite.Header?.ToString() != L.T("즐겨찾기 등록"))
                throw new Exception("Row context menu did not target the right-clicked album.");
            OnUi(async () =>
            {
                await view.RefreshAsync();
                jobs.SelectedItem = jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == second.Id);
                favorite.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitFor(() => saved.Contains(album.DeezerAlbumId));
                await Task.Delay(30);
            });
            if (toggledAlbum != first.Id) throw new Exception("Context favorite changed target after list refresh.");
            Render(workspace, Path.Combine(output, language + "-album-context-target.png"), 1040, 680);
            firstRow = (ListBoxItem)jobs.ItemContainerGenerator.ContainerFromItem(jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == first.Id));
            firstRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            context = firstRow.ContextMenu;
            context.PlacementTarget = firstRow;
            context.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            favorite = (MenuItem)context.Items[0];
            if (favorite.Header?.ToString() != L.T("즐겨찾기 해제")) throw new Exception("Row context menu favorite header was stale.");
            RenderMenu(context, Path.Combine(output, language + "-album-context-menu.png"));
            OnUi(async () =>
            {
                favorite.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitFor(() => !saved.Contains(album.DeezerAlbumId));
                await Task.Delay(30);
            });
            jobs.SelectedItem = jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == second.Id);
            if (tracks.Items.Count != 1 || tracks.Items.Cast<SoulseekTransfer>().Single().State != L.T("ZIP에 저장됨")) throw new Exception("Archived album track manifest is missing.");
            if (((Button)view.FindName("RetryButton")).Visibility != Visibility.Collapsed || Item("StopAlbumMenu").Visibility != Visibility.Collapsed
                || Item("ArtworkMenu").Visibility != Visibility.Collapsed) throw new Exception("Completed album must hide irrelevant actions.");
            jobs.SelectedItem = jobs.Items.Cast<AlbumDownloadStatus>().Single(j => j.Id == first.Id);
            ((Button)view.FindName("SearchAgainButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var search = (SoulseekSearchView)((ContentControl)workspace.FindName("SearchHost")).Content;
            if (((TextBox)search.FindName("QueryBox")).Text != $"{album.Artist} {album.Album}" || ((TextBlock)search.FindName("ExcludedPeerText")).Visibility != Visibility.Visible)
                throw new Exception("Replacement peer search did not keep album context.");
            if (sections.SelectedIndex != 3 || !search.WantsLogin) throw new Exception("Replacement search must request inline login while offline.");
            if (downloads.SnapshotAsync().GetAwaiter().GetResult().Single(j => j.Id == first.Id).State != "failed") throw new Exception("Browsing replacement peers must not cancel the current job.");
        }
        finally
        {
            OnUi(() => downloads.DisposeAsync().AsTask());
            CacheLocation.Directory = oldCache;
        }
    }

    static void RenderMenu(ContextMenu menu, string path)
    {
        // 팝업 창을 띄우지 않고 같은 메뉴 스타일과 표시 상태만 렌더한다.
        var panel = new StackPanel();
        foreach (var entry in menu.Items.OfType<Control>().Where(c => c.Visibility == Visibility.Visible))
        {
            if (entry is MenuItem item) panel.Children.Add(new MenuItem { Header = item.Header, IsEnabled = item.IsEnabled });
            else if (entry is Separator) panel.Children.Add(new Separator { Style = (Style)Application.Current.FindResource(MenuItem.SeparatorStyleKey) });
        }
        Render(new Border { Background = (Brush)Application.Current.FindResource("Surface2"), BorderBrush = (Brush)Application.Current.FindResource("Line3"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Child = panel, VerticalAlignment = VerticalAlignment.Top }, path, 300, 340);
    }

    sealed class TestUiContext : SynchronizationContext
    {
        public readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> Calls = new();
        public override void Post(SendOrPostCallback callback, object? state) => Calls.Add((callback, state));
        public override SynchronizationContext CreateCopy() => this;
    }

    static void OnUi(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        var context = new TestUiContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = action();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("UI task did not finish.");
                if (context.Calls.TryTake(out var call, 100)) call.Callback(call.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    static void CheckExtensionFilter(AlbumDownloads downloads, ReleaseRecommendation album, string output, string language)
    {
        var search = new SoulseekSearchView(album, downloads, albumTrackCount: 1);
        try
        {
            var files = new[] { new SoulseekFile(@"Mixed\01.FLAC", 100), new SoulseekFile(@"Mixed\02.mp3", 200), new SoulseekFile(@"Mixed\03.OgG", 300) };
            var peer = new SoulseekPeer("mixed-peer", true, 0, 1000000, files);
            var mixed = new SoulseekFolder(peer, "Mixed", files);
            var mp3 = mixed.Files[1];
            mp3.Selected = false;
            var results = (IList<SoulseekFolder>)((ItemsControl)search.FindName("Groups")).ItemsSource;
            results.Add(mixed);
            results.Add(new SoulseekFolder(peer, "MP3 only", [new SoulseekFile(@"MP3 only\01.mp3", 400)]));
            var format = (ComboBox)search.FindName("FormatBox");
            var groups = (ItemsControl)search.FindName("Groups");
            void Select(string extension) => format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == extension);
            Select("FLAC");
            Render(search, Path.Combine(output, language + "-flac-filter.png"), 390, 800);
            if (groups.Items.Count != 1 || mixed.Files.Count != 1 || mixed.Files[0].File != files[0]) throw new Exception($"FLAC filtering failed: groups={groups.Items.Count}, files={mixed.Files.Count}, selected={format.SelectedItem}, source={groups.Items.SourceCollection.GetType()}, filter={groups.Items.Filter != null}.");
            Select("MP3");
            if (groups.Items.Count != 2 || mixed.Files.Count != 1 || !ReferenceEquals(mp3, mixed.Files[0]) || mp3.Selected) throw new Exception("Mixed-folder filtering or selection preservation failed.");
            Render(search, Path.Combine(output, language + "-extension-filter.png"), 390, 800);
            Select("OGG");
            if (groups.Items.Count != 1 || mixed.Files.Single().File != files[2]) throw new Exception("Case-insensitive extension filtering failed.");
            Select("WAV");
            if (groups.Items.Count != 0 || ((TextBlock)search.FindName("FilterEmptyText")).Visibility != Visibility.Visible) throw new Exception("Empty filter state failed.");
            Select("");
            if (groups.Items.Count != 1 || mixed.Files.Count != 3 || mixed.Files[1].Selected) throw new Exception("All formats did not restore choices or hide mismatched track counts.");
        }
        finally { search.Stop(); }
    }

    static void CheckTrackCountFilter(AlbumDownloads downloads, ReleaseRecommendation album, string output, string language)
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<RecipeSong.Core.Providers.DzAlbum>("{\"id\":1,\"title\":\"Album\",\"nb_tracks\":23,\"tracks\":{\"data\":[]}}");
        if (metadata?.TrackCount != 23) throw new Exception("Album count must use nb_tracks, not a partial track list.");
        var search = new SoulseekSearchView(album, downloads, albumTrackCount: 2);
        var unknown = new SoulseekSearchView(album, downloads);
        try
        {
            SoulseekFolder Folder(string name, params string[] extensions)
            {
                var files = extensions.Select((ext, i) => new SoulseekFile($"{name}\\{i + 1:00}.{ext}", 100)).ToArray();
                return new(new(name, true, 0, 1000, files), name, files);
            }
            var shortAlbum = Folder("Short", "flac");
            var exact = Folder("Exact", "flac", "flac");
            var extra = Folder("Bonus", "flac", "flac", "flac");
            var mixed = Folder("Mixed", "flac", "flac", "mp3", "mp3");
            var groups = (ItemsControl)search.FindName("Groups");
            var source = (IList<SoulseekFolder>)groups.ItemsSource;
            foreach (var item in new[] { shortAlbum, exact, extra, mixed }) source.Add(item);
            if (groups.Items.Count != 1 || !ReferenceEquals(groups.Items[0], exact)) throw new Exception("Missing/extra tracks were not hidden.");
            var format = (ComboBox)search.FindName("FormatBox");
            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "FLAC");
            if (groups.Items.Count != 2 || !groups.Items.Contains(mixed)) throw new Exception("Count must follow the selected extension.");
            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "MP3");
            if (groups.Items.Count != 1 || !ReferenceEquals(groups.Items[0], mixed)) throw new Exception("Extension change did not recompute matching counts.");
            Render(search, Path.Combine(output, language + "-track-count-filter.png"), 700, 850);
            var unknownGroups = (ItemsControl)unknown.FindName("Groups");
            ((IList<SoulseekFolder>)unknownGroups.ItemsSource).Add(exact);
            if (unknownGroups.Items.Count != 0) throw new Exception("Unknown album count must not expose unverified results.");
        }
        finally { search.Stop(); unknown.Stop(); }
    }

    static void CheckSearchOrder(AlbumDownloads downloads, ReleaseRecommendation album, string output, string language)
    {
        var search = new SoulseekSearchView(album, downloads, albumTrackCount: 1);
        try
        {
            var files = new[] { new SoulseekFile(@"Album\01.flac", 10000000) };
            SoulseekFolder Folder(string user, long queue, int speed, bool free) => new(new(user, free, queue, speed, files), "Album", files);
            var queued = Folder("queued-fast", 3, 20 * 1048576, true);
            var slow = Folder("zero-slow", 0, 512 * 1024, false);
            var unknown = Folder("zero-unknown", 0, 0, true);
            var fast = Folder("zero-fast", 0, 2 * 1048576, true);
            var source = (IList<SoulseekFolder>)((ItemsControl)search.FindName("Groups")).ItemsSource;
            foreach (var group in new[] { queued, slow, unknown, fast }) source.Add(group);
            var results = (ItemsControl)search.FindName("Groups");
            if (!results.Items.Cast<SoulseekFolder>().SequenceEqual(new[] { fast, slow, unknown, queued })) throw new Exception("Queue/speed ordering failed.");
            fast.Files[0].Selected = false;
            var newest = Folder("new-zero-fastest", 0, 4 * 1048576, true);
            source.Add(newest);
            if (!ReferenceEquals(results.Items[0], newest)) throw new Exception("Incoming result did not follow queue/speed order.");
            var formats = (ComboBox)search.FindName("FormatBox");
            formats.SelectedItem = formats.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "MP3");
            formats.SelectedIndex = 0;
            if (!results.Items.Cast<SoulseekFolder>().SequenceEqual(new[] { newest, fast, slow, unknown, queued }) || fast.Files[0].Selected)
                throw new Exception("Filtering lost queue/speed order or selection.");
            if (unknown.SpeedText != L.T("상대 속도: 정보 없음") || fast.SpeedText != L.F("상대 속도: {0:0.0} MiB/s", 2d)) throw new Exception("Reported speed display failed.");
            Render(search, Path.Combine(output, language + "-search-order.png"), 700, 1000);
        }
        finally { search.Stop(); }
    }

    static void CheckDownloadQueue(SoulseekTransfer queued)
    {
        if (!queued.QueueStatus.Contains(L.F("상대 대기열 {0:N0}번째 · 앞에 {1:N0}개", 17, 16))) throw new Exception("Queue position or ahead count is incorrect.");
        if (!(queued with { QueuePosition = 1 }).QueueStatus.Contains(L.F("상대 대기열 {0:N0}번째 · 앞에 {1:N0}개", 1, 0))) throw new Exception("First queue position is incorrect.");
        if ((queued with { QueuePosition = 0 }).QueueStatus != L.T("상대 대기열 순번 확인 중…")) throw new Exception("Unknown queue position must not imply readiness.");
        if ((queued with { QueuePosition = null, QueueUnavailable = true }).QueueStatus != L.T("상대방이 대기 순번을 알려주지 않았어요.")) throw new Exception("Unavailable queue position is incorrect.");
        if ((queued with { IsRemoteQueued = false, IsLocalQueued = true }).QueueStatus != L.T("내 다운로드 슬롯 대기")) throw new Exception("Local and remote queues must be distinct.");
        if ((queued with { IsActive = true }).QueueStatus != "" || (queued with { FinishedAt = DateTimeOffset.UtcNow }).QueueStatus != "") throw new Exception("Active or finished transfers must not display a queue position.");
    }

    static void CheckDownloadProgress(string root)
    {
        const long mib = 1048576;
        var job = new AlbumDownload { State = "downloading", DownloadsDirectory = root, Files = [new("Album\\One.flac", 10 * mib, 950, 44100, 16), new("Album\\Two.flac", 10 * mib, 950, 44100, 16)] };
        var folder = Path.Combine(root, job.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        using (var complete = File.Create(Path.Combine(folder, "01-One.flac"))) complete.SetLength(10 * mib);
        var partial = Path.Combine(folder, "02-Two.flac.partial");
        using (var resumed = File.Create(partial)) resumed.SetLength(3 * mib);
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new SoulseekTransfer(Guid.NewGuid(), false, "peer", job.Files[1].Filename, "failed", 6 * mib, 10 * mib, 0, null) { AlbumJobId = job.Id, CreatedAt = now.AddMinutes(-1), FinishedAt = now.AddSeconds(-10) },
            new SoulseekTransfer(Guid.NewGuid(), false, "peer", job.Files[1].Filename, "active", mib, 10 * mib, mib, null) { AlbumJobId = job.Id, CreatedAt = now, IsActive = true },
            new SoulseekTransfer(Guid.NewGuid(), true, "peer", job.Files[0].Filename, "active", 10 * mib, 10 * mib, 100 * mib, null) { AlbumJobId = job.Id, IsActive = true },
        };
        var status = new AlbumDownloadStatus(job, rows);
        if (status.Percent != 65 || !status.ProgressText.Contains(L.F("예상 {0:N0}초 남음", 7))) throw new Exception("Resumed download progress or ETA is incorrect.");
        File.Delete(partial);
        if (new AlbumDownloadStatus(job, rows).Percent != 55) throw new Exception("Retry attempts were counted twice.");
        job.State = "packing";
        if (new AlbumDownloadStatus(job, rows).Percent != 100) throw new Exception("Packing must follow complete audio download.");
    }

    static void RenderWindow(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        if (content.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
            content.DataContext = window.DataContext;
        window.Content = null;
        content.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RecipeSong.Wpf;component/SoulseekStyles.xaml", UriKind.Relative) });
        Render(content, path, width, height);
        window.Close();
    }

    static void Render(FrameworkElement content, string path, int width, int height)
    {
        var canvas = new Border { Background = (Brush)Application.Current.FindResource("Bg"), Child = content, Width = width, Height = height };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        void Invalidate(DependencyObject visual)
        {
            if (visual is UIElement element) element.InvalidateVisual();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++) Invalidate(VisualTreeHelper.GetChild(visual, i));
        }
        Invalidate(canvas);
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
        canvas.Child = null;
    }
}
