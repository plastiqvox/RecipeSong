using System.IO;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckDownloadStorage(string output)
    {
        var previous = Environment.GetEnvironmentVariable("RECIPESONG_DATA");
        Environment.SetEnvironmentVariable("RECIPESONG_DATA", Path.Combine(output, "isolated-data"));
        try
        {
            foreach (var language in new[] { "ko", "en" })
            {
                L.Init(language);
                var settings = new AppSettings { PendingDownloadCacheDirectory = Path.Combine(output, "new-location"), ClearDownloadCacheOnNextStart = true };
                var cache = new DownloadCache(DownloadCache.Location(settings, null));
                cache.EnsureOwned();
                Directory.CreateDirectory(Path.Combine(cache.DirectoryPath, "uv"));
                File.WriteAllText(Path.Combine(cache.DirectoryPath, "uv", "fixture.bin"), "fixture");
                var view = new DownloadCacheView();
                bool restart = false;
                view.RestartRequired += () => restart = true;
                OnUi(async () =>
                {
                    view.Configure(settings, null);
                    var actions = (WrapPanel)view.FindName("Actions");
                    await WaitFor(() => actions.IsEnabled);
                    if (!restart || ((TextBlock)view.FindName("Result")).Text.Length == 0)
                        throw new Exception("Pending cache maintenance was not visible.");
                    Render(view, Path.Combine(output, language + "-download-storage.png"), 740, 340);
                    Render(view, Path.Combine(output, language + "-download-storage-narrow.png"), 350, 460);
                    ((Button)view.FindName("CancelPendingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var restored = AppSettings.Load();
                    if (restored.ClearDownloadCacheOnNextStart || restored.PendingDownloadCacheDirectory != null)
                        throw new Exception("Pending maintenance cancellation was not saved.");
                    if (!File.Exists(Path.Combine(cache.DirectoryPath, "uv", "fixture.bin")))
                        throw new Exception("Settings display or cancellation deleted a cache.");
                });
            }
        }
        finally { Environment.SetEnvironmentVariable("RECIPESONG_DATA", previous); }
    }
}
