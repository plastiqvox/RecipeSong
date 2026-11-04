using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using RecipeSong.Core;

static partial class Program
{
    static void CheckCardActions(string output)
    {
        var document = XDocument.Load("src/RecipeSong.Wpf/MainWindow.xaml");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = document.Descendants().Single(e => e.Elements(ns + "Button")
            .Any(b => (string?)b.Attribute("Command") == "{Binding ToggleTracksCommand}"));
        foreach (var language in new[] { "ko", "en" })
        foreach (var width in new[] { 138, 218, 238, 330 })
        {
            L.Init(language);
            var node = new XElement(source);
            node.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            node.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
            var panel = (Panel)XamlReader.Parse(node.ToString());
            panel.Measure(new Size(width, double.PositiveInfinity));
            var height = (int)Math.Ceiling(panel.DesiredSize.Height);
            Render(panel, Path.Combine(output, $"{language}-card-actions-{width}.png"), width, height);
            foreach (var button in panel.Children.OfType<Button>())
            {
                var point = button.TranslatePoint(new Point(), panel);
                if (point.X < 0 || point.X + button.ActualWidth > width + .5)
                    throw new Exception($"Card action overflow at width {width}.");
            }
            if (width == 238 && panel.Children.OfType<Button>().Select(b => b.TranslatePoint(new Point(), panel).Y).Distinct().Count() < 2)
                throw new Exception("Narrow card actions did not wrap.");
        }
    }
}
