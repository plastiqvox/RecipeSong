using System.IO;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckWeekRange(string output, string language)
    {
        var navigation = new WorkspaceNavigation();
        navigation.BeginSearch(WorkspacePage.Artists);
        navigation.BeginSearch(WorkspacePage.Soulseek);
        if (navigation.SearchOrigin != WorkspacePage.Artists || navigation.BackLabel != L.T("아티스트 목록으로"))
            throw new Exception("Retrying a Soulseek search lost the original artist tab.");
        navigation.BeginSearch(WorkspacePage.Recommendations);
        if (navigation.SearchOrigin != WorkspacePage.Recommendations || navigation.BackLabel != L.T("추천으로"))
            throw new Exception("A new recommendation search did not update its return destination.");
        var range = Periods.WeekRange(new(2020, 12, 31), new(2021, 1, 10));
        var dialog = new PeriodRangeWindow(range, new(2026, 11, 4));
        if (dialog.Result != range) throw new Exception("Reopening a custom range lost its ISO year boundary.");
        var start = (ComboBox)dialog.FindName("StartWeek");
        var end = (ComboBox)dialog.FindName("EndWeek");
        if (start.Items.Count != 53 || end.Items.Count != 52) throw new Exception("Week 53 was not selectable.");
        ((ComboBox)dialog.FindName("StartYear")).SelectedItem = 2026;
        if (((Button)dialog.FindName("ApplyButton")).IsEnabled || dialog.Result != null)
            throw new Exception("A reversed year range can be applied.");
        var recent = DescendantButtons((DependencyObject)dialog.Content).First(b => (string?)b.Tag == "12");
        recent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (dialog.Result is not { } selected || (selected.End - selected.Start).Days != 84
            || selected.Start.DayOfWeek != DayOfWeek.Monday || selected.End != new DateTime(2026, 11, 9))
            throw new Exception("The last-12-weeks preset does not include exactly 12 whole weeks.");
        if (!DescendantButtons((DependencyObject)dialog.Content).Any(b => b.IsCancel))
            throw new Exception("Range selection needs a cancel action.");
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(580, double.PositiveInfinity));
        if (content.DesiredSize.Height > 600) throw new Exception("The range dialog exceeds a compact screen.");
        RenderWindow(dialog, Path.Combine(output, language + "-week-range.png"), 580, (int)Math.Ceiling(content.DesiredSize.Height));
        new SoulseekSettings { Username = "example-user", DownloadsDirectory = Path.Combine(output, "originals"), ArchiveDirectory = Path.Combine(output, "albums") }.Save();
        var downloads = new AlbumDownloads();
        try
        {
            var album = new ReleaseRecommendation("Artist", "Album", "album", new(2026, 11, 1), null, null, 1, null, null, Region.International, null, null, null, [], [], false, 0);
            CheckWorkspace(downloads, album, output, language);
        }
        finally { downloads.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
