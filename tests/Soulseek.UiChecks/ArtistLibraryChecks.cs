using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Core.Providers;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckArtistLibrary(string output, string language)
    {
        var filters = new JsonCache<ArtistAlbumFilters>("artist-album-filters", TimeSpan.FromDays(36500));
        filters.Set("filters", new(IncludeOwned: false));
        filters.Save();
        var coverPath = Path.Combine(output, language + "-fixture-cover.png");
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new Rect(0, 0, 128, 128));
            drawing.DrawEllipse(System.Windows.Media.Brushes.LightGoldenrodYellow, null, new Point(64, 64), 35, 35);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(128, 128, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(coverPath)) encoder.Save(file);
        using var http = new HttpClient(new ArtistCatalogHandler(new Uri(coverPath).AbsoluteUri));
        var song = new TrackRecord { Artist = "Test artist", Album = "Partial album", Title = "Favorite song", Loved = true, PlayCount = 24 };
        var playlists = new[]
        {
            new Playlist("Favorite songs", "1", null, PlaylistKind.User, [song]),
            new Playlist("Quiet songs", "2", null, PlaylistKind.User, [song]),
        };
        var mostPlayed = song with { Album = "Most played album", Title = "Frequently played song", PlayCount = 120 };
        var secondPlayed = mostPlayed with { Album = "Second most played album", PlayCount = 80 };
        var fourthPlayed = mostPlayed with { Album = "Fourth album", PlayCount = 10 };
        var discovery = new ArtistAlbumDiscovery(new DeezerClient(http), [song, mostPlayed, secondPlayed, fourthPlayed], playlists, [],
            (album, _) => album.Album != "Fourth album" ? Task.FromResult<string?>(new Uri(coverPath).AbsoluteUri)
                : throw new Exception("Artist thumbnail requested an album outside the top three."), identityStore: FreshIdentityStore());
        using var view = new ArtistLibraryView(_ => Task.FromResult(discovery));
        ReleaseRecommendation? requested = null;
        view.SearchRequested += album => requested = album;
        OnUi(async () =>
        {
            await view.InitializeAsync();
            var artists = (ListBox)view.FindName("Artists");
            if (artists.Items.Count != 1 || ((PlaylistMusician)artists.Items[0]).Tracks.Count != 1) throw new Exception("Curated artist list or playlist deduplication failed.");
            artists.SelectedIndex = 0;
            Render(view, Path.Combine(output, language + "-artist-thumbnail-loading.png"), 1040, 850);
            var artistImages = AlbumImages(artists).ToArray();
            if (artistImages.Length != 3) throw new Exception("Artist row did not display three album thumbnails.");
            foreach (var image in artistImages) image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await WaitFor(() => ((PlaylistMusician)artists.SelectedItem).TopAlbums.All(a => a.CoverUrl != null));
            if (((PlaylistMusician)artists.SelectedItem).TopAlbums.First().Album.Album != "Most played album")
                throw new Exception("Artist row did not expose the most played album.");
            var candidates = (ComboBox)view.FindName("CandidateBox");
            await WaitFor(() => candidates.Items.Count == 2 && candidates.SelectedItem != null);
            if (!candidates.SelectedItem.ToString()!.Contains("ID 7")) throw new Exception("Owned albums did not correct the first homonymous artist ID.");
            ((Button)view.FindName("FindButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var albums = (ListBox)view.FindName("Albums");
            await WaitFor(() => albums.Items.Count == 2);
            if (!albums.Items.Cast<DiscoveredAlbum>().Any(a => a.Ownership == CatalogOwnership.Partial)) throw new Exception("Partial album was hidden.");
            albums.SelectedItem = albums.Items.Cast<DiscoveredAlbum>().Single(a => a.Title == "Missing album");
            var search = (Button)view.FindName("SearchButton");
            await WaitFor(() => search.IsEnabled);
            if (((ListBox)view.FindName("AlbumSongs")).Items.Count != 2) throw new Exception("Selected album tracks not displayed.");
            search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (requested?.DeezerAlbumId != 2 || requested.Artist != "Test artist") throw new Exception("Soulseek navigation lost selected album metadata.");
            Render(view, Path.Combine(output, language + "-artist-library.png"), 1040, 850);
            Render(view, Path.Combine(output, language + "-artist-library-narrow.png"), 700, 850);
            if (artistImages.Any(image => image.Source == null)) throw new Exception("Most-played album thumbnails were not displayed.");
            var covers = AlbumImages(albums).ToArray();
            if (covers.Length != 2 || covers.Count(image => image.Source != null) != 1)
                throw new Exception("Album cover or missing-cover fallback failed.");
            ((CheckBox)view.FindName("SingleBox")).IsChecked = true;
            if (albums.Items.Count != 3 || !search.IsEnabled || (albums.SelectedItem as DiscoveredAlbum)?.Album.Id != 2)
                throw new Exception("Type filter must recompute results and preserve a still-visible selection.");
            ((CheckBox)view.FindName("AlbumBox")).IsChecked = false;
            ((CheckBox)view.FindName("EpBox")).IsChecked = false;
            ((CheckBox)view.FindName("SingleBox")).IsChecked = false;
            if (albums.Items.Count != 0 || search.IsEnabled) throw new Exception("Empty filter state retained downloadable selection.");
            ((TextBox)view.FindName("ArtistQuery")).Text = "no match";
            if (artists.Items.Count != 0 || albums.Items.Count != 0 || candidates.Items.Count != 0) throw new Exception("Artist filter left stale catalog results.");
            ((TextBox)view.FindName("ArtistQuery")).Text = "";
            ((ComboBox)view.FindName("PlaylistBox")).SelectedIndex = 1;
            if (artists.Items.Count != 1) throw new Exception("Specific playlist selection failed.");
            ((CheckBox)view.FindName("AlbumBox")).IsChecked = true;
            ((CheckBox)view.FindName("EpBox")).IsChecked = true;
            ((CheckBox)view.FindName("SingleBox")).IsChecked = false;
            artists.SelectedIndex = 0;
            await WaitFor(() => candidates.SelectedItem != null && !view.IsSearching);
            var originalMusician = (PlaylistMusician)artists.SelectedItem;
            ((CheckBox)view.FindName("IncludeOwnedBox")).IsChecked = true;
            ((TextBox)view.FindName("DirectArtistQuery")).Text = "Other artist";
            var directSearch = (Button)view.FindName("DirectArtistSearchButton");
            directSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => directSearch.IsEnabled && candidates.Items.Count == 2 && artists.SelectedItem == null);
            candidates.SelectedIndex = 0; // 플리에서 확정했던 ID 7과 다른 동명이인
            var keptIdentity = await discovery.ResolveArtistAsync(originalMusician, CancellationToken.None);
            if (keptIdentity.SelectedId != 7 || ((Button)view.FindName("ResetIdentityButton")).IsEnabled)
                throw new Exception("Direct search overwrote the playlist artist's identity.");
            candidates.SelectedIndex = 1;
            ((Button)view.FindName("FindButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var includeOwned = (CheckBox)view.FindName("IncludeOwnedBox");
            await WaitFor(() => !view.IsSearching && ((Button)view.FindName("FindButton")).IsEnabled);
            if (albums.Items.Count != 4) throw new Exception("Direct album count: " + albums.Items.Count + " · " + ((TextBlock)view.FindName("StatusText")).Text);
            if (includeOwned.IsChecked != true || !albums.Items.Cast<DiscoveredAlbum>().Any(a => a.Ownership == CatalogOwnership.InLibrary))
                throw new Exception("Direct artist search must include owned albums by default.");
            includeOwned.IsChecked = false;
            if (albums.Items.Count != 2) throw new Exception("Owned album filter did not restore missing albums only.");
            includeOwned.IsChecked = true;
            albums.SelectedItem = albums.Items.Cast<DiscoveredAlbum>().Single(a => a.Title == "Missing album");
            await WaitFor(() => search.IsEnabled);
            requested = null;
            search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (requested?.DeezerAlbumId != 2 || requested.Artist != "Test artist")
                throw new Exception("Direct artist lookup did not forward the selected album to Soulseek.");
            if (discovery.Musicians().Count != 1 || discovery.Musicians()[0].Tracks.Count != 1)
                throw new Exception("Direct search modified the curated playlist artist list.");
            Render(view, Path.Combine(output, language + "-direct-artist.png"), 1040, 950);
            Render(view, Path.Combine(output, language + "-direct-artist-narrow.png"), 700, 950);
            artists.SelectedIndex = 0;
            await WaitFor(() => candidates.SelectedItem != null && !view.IsSearching);
            if (includeOwned.IsChecked != true) throw new Exception("Changing artist reset the saved include-owned preference.");
        });
        using var fallbackHttp = new HttpClient(new ArtistCatalogHandler(new Uri(coverPath).AbsoluteUri, ambiguous: true));
        var fallbackDiscovery = new ArtistAlbumDiscovery(new DeezerClient(fallbackHttp), [song, mostPlayed, secondPlayed], playlists, [], identityStore: FreshIdentityStore());
        using var fallbackView = new ArtistLibraryView(_ => Task.FromResult(fallbackDiscovery));
        OnUi(async () =>
        {
            var includeOwned = (CheckBox)fallbackView.FindName("IncludeOwnedBox");
            if (includeOwned.IsChecked != true) throw new Exception("A new artist view lost the saved include-owned preference.");
            includeOwned.IsChecked = false;
            await fallbackView.InitializeAsync();
            var artists = (ListBox)fallbackView.FindName("Artists");
            artists.SelectedIndex = 0;
            var choices = (StackPanel)fallbackView.FindName("CandidateChoices");
            var list = (ListBox)fallbackView.FindName("CandidateList");
            var selected = (ComboBox)fallbackView.FindName("CandidateBox");
            var find = (Button)fallbackView.FindName("FindButton");
            await WaitFor(() => choices.Visibility == Visibility.Visible && list.Items.Count == 2);
            if (find.IsEnabled || selected.SelectedItem != null || list.SelectedItem != null)
                throw new Exception("Ambiguous IDs must show candidates without automatically selecting one.");
            Render(fallbackView, Path.Combine(output, language + "-artist-id-candidates.png"), 700, 850);
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.ToString()!.Contains("ID 7"));
            if (!find.IsEnabled || !ReferenceEquals(selected.SelectedItem, list.SelectedItem)) throw new Exception("Manual ID row selection failed.");
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var albums = (ListBox)fallbackView.FindName("Albums");
            await WaitFor(() => albums.Items.Count == 2);
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.ToString()!.Contains("ID 6"));
            if (albums.Items.Count != 0 || ((Button)fallbackView.FindName("SearchButton")).IsEnabled)
                throw new Exception("Changing a manual artist ID left stale albums available.");
            var storedIdentity = await fallbackDiscovery.ResolveArtistAsync((PlaylistMusician)artists.SelectedItem, CancellationToken.None);
            if (!storedIdentity.FromStore || storedIdentity.SelectedId != 6) throw new Exception("Manual selection was not persisted.");
            ((Button)fallbackView.FindName("ResetIdentityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => choices.Visibility == Visibility.Visible && list.Items.Count == 2 && selected.SelectedItem == null);
            if (find.IsEnabled) throw new Exception("ID reset left an old confirmed selection enabled.");
            Render(fallbackView, Path.Combine(output, language + "-artist-id-reset.png"), 700, 850);
            ((TextBox)fallbackView.FindName("ArtistQuery")).Text = "no match";
            if (list.Items.Count != 0 || choices.Visibility != Visibility.Collapsed || find.IsEnabled)
                throw new Exception("Artist filtering left stale candidate IDs.");
        });
        using var emptyHttp = new HttpClient(new ArtistCatalogHandler("", empty: true));
        var emptyDiscovery = new ArtistAlbumDiscovery(new DeezerClient(emptyHttp), [song], playlists, [], identityStore: FreshIdentityStore());
        using var emptyView = new ArtistLibraryView(_ => Task.FromResult(emptyDiscovery));
        OnUi(async () =>
        {
            await emptyView.InitializeAsync();
            ((ListBox)emptyView.FindName("Artists")).SelectedIndex = 0;
            var status = (TextBlock)emptyView.FindName("StatusText");
            await WaitFor(() => status.Text == L.T("카탈로그에서 아티스트를 찾지 못했어요."));
            if (((StackPanel)emptyView.FindName("CandidateChoices")).Visibility != Visibility.Collapsed
                || ((Button)emptyView.FindName("FindButton")).IsEnabled) throw new Exception("Empty ID search must not expose invalid candidates.");
            ((TextBox)emptyView.FindName("DirectArtistQuery")).Text = "No catalog match";
            var direct = (Button)emptyView.FindName("DirectArtistSearchButton");
            direct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => direct.IsEnabled && !emptyView.IsSearching);
            if (((ComboBox)emptyView.FindName("CandidateBox")).Items.Count != 0
                || ((Button)emptyView.FindName("FindButton")).IsEnabled || ((Button)emptyView.FindName("SearchButton")).IsEnabled)
                throw new Exception("Empty direct search retained a downloadable selection.");
            Render(emptyView, Path.Combine(output, language + "-direct-artist-empty.png"), 700, 850);
        });
    }

    static ArtistIdentityStore FreshIdentityStore() => new(Path.Combine(CacheLocation.Directory, "identity-ui-" + Guid.NewGuid().ToString("N") + ".sqlite3"));

    static IEnumerable<Image> AlbumImages(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Image image) yield return image;
            foreach (var nested in AlbumImages(child)) yield return nested;
        }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Artist library UI did not settle.");
            await Task.Delay(10);
        }
    }

    sealed class ArtistCatalogHandler(string cover, bool ambiguous = false, bool empty = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (empty) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            if (ambiguous && path == "/artist/6/albums") path = "/artist/7/albums";
            var json = path switch
            {
                "/search/artist" => """{"data":[{"id":6,"name":"Test artist","nb_fan":999999},{"id":7,"name":"Test artist","nb_fan":12345}]}""",
                "/search/album" => """{"data":[]}""",
                "/artist/6/albums" => """{"data":[{"id":60,"title":"Another person's album"}]}""",
                "/artist/7/albums" => """{"data":[{"id":1,"title":"Partial album","release_date":"2020-01-01","record_type":"album","nb_tracks":3},{"id":2,"title":"Missing album","release_date":"2021-02-03","record_type":"ep","nb_tracks":2},{"id":3,"title":"A single","release_date":"2019-01-01","record_type":"single","nb_tracks":1}]}""",
                "/album/2" => """{"id":2,"title":"Missing album","release_date":"2021-02-03","record_type":"ep","nb_tracks":2}""",
                "/album/2/tracks" => """{"data":[{"id":20,"title":"Track one"},{"id":21,"title":"Track two"}]}""",
                _ => throw new Exception("Unexpected UI catalog request: " + path),
            };
            if (path == "/artist/7/albums")
            {
                json = json.Replace("\"id\":2,", "\"cover_big\":" + System.Text.Json.JsonSerializer.Serialize(cover) + ",\"id\":2,");
                json = json.Replace("\"data\":[", "\"data\":[{\"id\":8,\"title\":\"Most played album\",\"nb_tracks\":1,\"record_type\":\"album\",\"release_date\":\"2018-01-01\"},{\"id\":9,\"title\":\"Second most played album\",\"nb_tracks\":1,\"record_type\":\"album\",\"release_date\":\"2019-01-01\"},");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
