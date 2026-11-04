using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckArtistFilters()
    {
        var store = new JsonCache<ArtistAlbumFilters>("artist-album-filters", TimeSpan.FromDays(36500));
        store.Clear();
        store.Save();
        ArtistLibraryView Open() => new(_ => throw new Exception("Filter restore must not load playlists or search."));
        string[] names = ["IncludeOwnedBox", "AlbumBox", "EpBox", "SingleBox", "LiveBox", "CompilationBox"];
        using (var initial = Open())
        {
            var actual = names.Select(n => ((CheckBox)initial.FindName(n)).IsChecked == true).ToArray();
            if (!actual.SequenceEqual(new[] { true, true, true, false, false, false }))
                throw new Exception("Default album filters changed.");
        }
        foreach (var expected in new[]
        {
            new[] { true, false, false, true, true, true },
            new[] { false, false, false, false, false, false },
            new[] { false, true, true, false, false, false }
        })
        {
            using (var edited = Open())
                for (int i = 0; i < names.Length; i++) ((CheckBox)edited.FindName(names[i])).IsChecked = expected[i];
            using var restored = Open();
            var actual = names.Select(n => ((CheckBox)restored.FindName(n)).IsChecked == true);
            if (!actual.SequenceEqual(expected)) throw new Exception("Album filter choices did not survive reopening the view.");
        }
    }
}
