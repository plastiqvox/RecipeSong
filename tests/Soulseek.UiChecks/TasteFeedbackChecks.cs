using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using RecipeSong.Core;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckTasteFeedback(string output)
    {
        // 소스는 실행 위치 대신 명시적인 저장소 경로로 찾는다. 사용자 앱 생성자는 호출하지 않는다.
        var source = Environment.GetEnvironmentVariable("RECIPESONG_TEST_SOURCE") ?? @"G:\Workspace\MusicRecs";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(source, "src/RecipeSong.Wpf/MainWindow.xaml"));
        var node = new XElement(doc.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "TrackRow"));
        node.Attribute(x + "Key")!.Remove();
        node.SetAttributeValue(XNamespace.Xmlns + "x", x);
        node.SetAttributeValue(XNamespace.Xmlns + "vm", "clr-namespace:RecipeSong.Wpf.ViewModels;assembly=RecipeSong.Wpf");
        node.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
        foreach (var lang in new[] { "ko", "en" })
        {
            L.Init(lang);
            long target = 0;
            var album = new ReleaseRecommendation("Fixture", "Track feedback fixture", "album", new(2026, 11, 4), null,
                null, 88, null, null, Region.International, null, null, null, [], [], false, 0);
            var item = new ReleaseItem(album, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, false)
            {
                IsTrackLiked = id => id == 11,
                IsTrackDisliked = id => id == 12,
                ToggleTrackDislikeAction = t => target = t.Id
            };
            item.SetTracks([new(1, "Liked track", null, TimeSpan.FromMinutes(3), null, 100, 11),
                            new(2, "Disliked track with a long title", null, TimeSpan.FromMinutes(4), null, 80, 12),
                            new(3, "Neutral track", null, TimeSpan.FromMinutes(2), null, 30, 13)]);
            if (!item.Tracks[1].IsDisliked || item.Tracks[1].IsLiked || !item.Tracks[0].IsLiked)
                throw new Exception("Saved per-track feedback state was not restored.");
            var template = (DataTemplate)XamlReader.Parse(node.ToString());
            var list = new ItemsControl { ItemsSource = item.Tracks, ItemTemplate = template, Foreground = (Brush)Application.Current.FindResource("Text") };
            Render(list, Path.Combine(output, lang + "-track-feedback.png"), 350, 180);
            var presenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(1);
            var button = (Button)template.FindName("trackDislikeButton", presenter);
            if (button.Command == null || !button.Command.CanExecute(null)) throw new Exception("Track feedback button was disabled for an unavailable preview.");
            button.Command.Execute(null);
            if (target != 12) throw new Exception("Dislike command targeted the wrong track.");
            var text = new TextBlock { Text = L.F("취향 기준 통과 {0}장 · 확인 필요 {1}장 · 기준 미달 {2}장", 4, 12, 7),
                Foreground = (Brush)Application.Current.FindResource("Text2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
            Render(text, Path.Combine(output, lang + "-taste-summary.png"), 350, 80);
        }
    }
}
