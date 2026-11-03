using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RecipeSong.Core;
using RecipeSong.Wpf;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckEvidence(string output)
    {
        var album = new ReleaseRecommendation("Fixture artist", "An album with different styles", "album", new(2026, 11, 1), "Fixture label", null,
            123, null, null, Region.International, null, null, .58, ["My artist – My track"], ["Seed artist"], false, 1.2,
            MatchedTag: "dream pop", MatchedCrew: "A producer shared with an album you enjoy", FitRatio: .5, AnalyzedTracks: 4,
            Evidence: new([new("sound", 1.5), new("dislikes", -.4), new("metadata", .1)], .51, .64, .53, false,
                [new(11, "A favorite track with a long title for wrapping", .71, true), new(12, "Another track", .57, true), new(13, "Below the threshold", .42, false), new(14, null, .35, false)]));
        var roundTrip = JsonSerializer.Deserialize<ReleaseRecommendation>(JsonSerializer.Serialize(album))!;
        if (roundTrip.Evidence?.Total != album.Score || roundTrip.Evidence.Tracks?.Count != 4)
            throw new Exception("Ranking evidence was not preserved in recommendation serialization.");
        foreach (var lang in new[] { "ko", "en" })
        {
            L.Init(lang);
            var model = new RecommendationEvidence(album);
            if (model.Contributions.Count != 3 || !model.Contributions.Any(c => c.Value.StartsWith('-')) || model.Tracks.Count != 4)
                throw new Exception("Score signs or analyzed tracks were lost.");
            var legacy = album with { Evidence = null };
            var old = new RecommendationEvidence(legacy);
            if (old.HasContributions || old.HasTracks || string.IsNullOrWhiteSpace(old.Notice))
                throw new Exception("Legacy evidence invented detailed scores.");
            var unheard = album with { Taste = null, Score = -1, FitRatio = null, AnalyzedTracks = null, MatchedTag = null, MatchedCrew = null,
                Evidence = new([new("unheard", -1)], null, null, null, false), NearTracks = [] };
            RenderEvidence(album, Path.Combine(output, lang + "-evidence.png"));
            RenderEvidence(legacy, Path.Combine(output, lang + "-evidence-legacy.png"));
            RenderEvidence(unheard, Path.Combine(output, lang + "-evidence-unheard.png"));
        }
        L.Init("ko");
        var item = new ReleaseItem(album, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, false)
        {
            OpenEvidenceAction = r => r.DetailTabIndex = 1
        };
        item.OpenEvidenceCommand.Execute(null);
        var tabs = new TabControl { DataContext = item };
        tabs.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RecipeSong.Wpf;component/WorkspaceStyles.xaml", UriKind.Relative) });
        tabs.Style = (Style)tabs.Resources["WorkspaceTabs"];
        tabs.SetBinding(TabControl.SelectedIndexProperty, new Binding(nameof(ReleaseItem.DetailTabIndex)) { Mode = BindingMode.TwoWay });
        tabs.Items.Add(new TabItem { Header = L.T("수록곡"), Style = (Style)tabs.Resources["WorkspaceTab"], FontSize = 12 });
        tabs.Items.Add(new TabItem { Header = L.T("추천 근거"), Content = new RecommendationEvidenceView(), Style = (Style)tabs.Resources["WorkspaceTab"], FontSize = 12 });
        Render(tabs, Path.Combine(output, "evidence-tabs.png"), 380, 700);
        if (tabs.SelectedIndex != 1) throw new Exception("Evidence command did not select the evidence tab.");
        tabs.SelectedIndex = 0;
        if (item.DetailTabIndex != 0) throw new Exception("Track tab selection did not update the item.");
    }

    static void RenderEvidence(ReleaseRecommendation album, string path)
    {
        var item = new ReleaseItem(album, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, false);
        var view = new RecommendationEvidenceView { DataContext = item, Foreground = (Brush)Application.Current.FindResource("Text") };
        Render(view, path, 350, 760);
        var viewport = (ScrollViewer)view.Content;
        if (viewport.ScrollableHeight > 0)
        {
            viewport.ScrollToEnd();
            Render(view, Path.ChangeExtension(path, null) + "-bottom.png", 350, 760);
        }
    }
}
