using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using RecipeSong.Core;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckRecommendationCalculationSettings(string output, string language)
    {
        var defaults = new RecPrefs();
        var previous = JsonSerializer.Deserialize<RecPrefs>("{\"Amount\":1,\"ExcludeKnown\":true}")!;
        if (!defaults.UsePrecise || !previous.UsePrecise || previous.Amount != 1 || !previous.ExcludeKnown)
            throw new Exception("New and existing preferences must default to precise calculation without losing other preferences.");
        var key = "precise-settings-test-" + Guid.NewGuid().ToString("N");
        var cache = new JsonCache<RecPrefs>(key, TimeSpan.FromDays(30));
        cache.Set("prefs", defaults with { UsePrecise = false });
        cache.Save();
        if (!new JsonCache<RecPrefs>(key, TimeSpan.FromDays(30)).TryGet("prefs", out var reopened) || reopened.UsePrecise)
            throw new Exception("Explicitly disabled precise mode was not preserved on reopening preferences.");

        // 실제 설정 섹션만 읽는다. MainViewModel·사용자 데이터·서비스는 실행하지 않는다.
        var document = XDocument.Load(Path.Combine("src", "RecipeSong.Wpf", "RecommendSettingsWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var section = new XElement(document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "CalculationOptions"));
        section.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        section.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
        var panel = (StackPanel)XamlReader.Parse(section.ToString());
        panel.Margin = new Thickness(16);
        var option = (CheckBox)panel.FindName("UsePreciseBox");
        option.IsChecked = previous.UsePrecise;
        if (option.Visibility != Visibility.Visible || !option.IsEnabled || option.Command != null)
            throw new Exception("Precise mode must be a visible settings option without an immediate execution command.");
        Render(panel, Path.Combine(output, language + "-recommendation-calculation-settings.png"), 390, 230);
        option.IsChecked = false;
        if (previous.UsePrecise != true) throw new Exception("Editing a settings control changed preferences before save.");
        Render(panel, Path.Combine(output, language + "-recommendation-calculation-disabled.png"), 390, 230);
        CheckRecommendationToolbar(output, language);
        RenderRecommendationSettingsTabs(output, language);
        RenderArchiveSettings(output, language);
    }

    static void RenderArchiveSettings(string output, string language)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine("src", "RecipeSong.Wpf", "SettingsWindow.xaml"));
        var root = document.Root!;
        root.Attribute(x + "Class")!.Remove();
        root.SetAttributeValue("Icon", "/RecipeSong.Wpf;component/Assets/logo.png");
        root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
        foreach (var attribute in root.Descendants().Attributes().Where(a => a.Name.LocalName is "Click" or "Checked" or "Unchecked").ToList()) attribute.Remove();
        var window = (Window)XamlReader.Parse(root.ToString());
        ((TextBlock)window.FindName("VersionText")).Text = "RecipeSong 0.3";
        ((TextBlock)window.FindName("ArchiveResult")).Text = L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 42);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        Render(content, Path.Combine(output, language + "-archive-settings.png"), 884, 760);
        Render(content, Path.Combine(output, language + "-archive-settings-small.png"), 784, 560);
        ((TextBlock)window.FindName("ArchiveResult")).Text = L.T("ZIP 메타데이터를 읽는 중…") + "\n"
            + L.F("ZIP {0}개 처리 · 앨범 {1}개 · 재사용 {2}개 · 경고 {3}개", 120, 110, 95, 2)
            + "\n[2026.02.03] (Album) Cory Wong - Lost In The Wonder.zip";
        Render(content, Path.Combine(output, language + "-archive-reading.png"), 884, 760);
        window.Close();
    }

    static void RenderRecommendationSettingsTabs(string output, string language)
    {
        // 실제 XAML과 선택지로 화면만 만든다. 사용자 데이터와 MainViewModel은 생성하지 않는다.
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine("src", "RecipeSong.Wpf", "RecommendSettingsWindow.xaml"));
        var root = document.Root!;
        root.Attribute(x + "Class")!.Remove();
        root.SetAttributeValue("Icon", "/RecipeSong.Wpf;component/Assets/logo.png");
        root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
        foreach (var attribute in root.Descendants().Attributes().Where(a => a.Name.LocalName is "Click" or "TextChanged").ToList()) attribute.Remove();
        root.Descendants(ns + "ResourceDictionary").Single(node => node.Attribute("Source") != null)
            .SetAttributeValue("Source", "/RecipeSong.Wpf;component/WorkspaceStyles.xaml");
        var window = (Window)XamlReader.Parse(root.ToString());
        foreach (var (gridName, hintName, fieldName, selected) in new[] {
            ("AmountGrid", "AmountHint", "Amounts", 0), ("AdventureGrid", "AdventureHint", "Adventures", 1),
            ("TracksGrid", "TracksHint", "TrackModes", 0), ("DropGrid", "DropHint", "DropModes", 0) })
        {
            var options = ((string Label, string Hint)[])typeof(RecommendSettingsWindow)
                .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName(gridName);
            foreach (var (option, i) in options.Select((option, i) => (option, i)))
                grid.Children.Add(new RadioButton { Content = L.T(option.Label), IsChecked = i == selected,
                    GroupName = gridName, Style = (Style)Application.Current.FindResource("Segment") });
            ((TextBlock)window.FindName(hintName)).Text = L.T(options[selected].Hint);
        }
        ((CheckBox)window.FindName("UsePreciseBox")).IsChecked = true;
        ((CheckBox)window.FindName("ExcludeInstrumentalBox")).IsChecked = true;
        foreach (var feed in WebzineFeeds.Defaults)
            ((WrapPanel)window.FindName("WebzinePanel")).Children.Add(new CheckBox { Content = $"{feed.Name} ({L.T(feed.Region)})",
                IsChecked = true, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text"), Margin = new Thickness(0, 0, 16, 6) });
        var tabs = (TabControl)window.FindName("SettingsTabs");
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            Render(content, Path.Combine(output, $"{language}-recommendation-settings-tab-{i}.png"), 784, 640);
            Render(content, Path.Combine(output, $"{language}-recommendation-settings-tab-{i}-small.png"), 704, 440);
        }
        foreach (var prefix in new[] { "Follow", "Blocked" })
        {
            ((FrameworkElement)window.FindName(prefix + "Empty")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName(prefix + "FilterBox")).Visibility = Visibility.Visible;
            ((TextBox)window.FindName(prefix + "Filter")).Text = "no-match";
            ((FrameworkElement)window.FindName(prefix + "FilterHint")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName(prefix + "NoResults")).Visibility = Visibility.Visible;
        }
        Render(content, Path.Combine(output, $"{language}-recommendation-settings-no-results.png"), 784, 640);
        Render(content, Path.Combine(output, $"{language}-recommendation-settings-no-results-small.png"), 704, 440);
        window.Close();
    }

    static void CheckRecommendationToolbar(string output, string language)
    {
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine("src", "RecipeSong.Wpf", "MainWindow.xaml"));
        var section = new XElement(document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "RecommendationActions"));
        section.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        section.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RecipeSong.Wpf;assembly=RecipeSong.Wpf");
        section.SetAttributeValue(XNamespace.Xmlns + "vm", "clr-namespace:RecipeSong.Wpf.ViewModels;assembly=RecipeSong.Wpf");
        var style = document.Descendants(ns + "Style").Single(node => (string?)node.Attribute(x + "Key") == "ToolbarIconButton");
        section.AddFirst(new XElement(ns + "StackPanel.Resources", new XElement(style)));
        var panel = (StackPanel)XamlReader.Parse(section.ToString());
        var recalculate = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var activity = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var settings = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var ai = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        panel.DataContext = new ToolbarPreviewData(recalculate, activity, settings, ai, language == "ko" ? "앨범 · EP" : "Albums · EP");
        panel.Margin = new Thickness(16, 12, 16, 0);
        Render(panel, Path.Combine(output, language + "-recommendation-toolbar.png"), 760, 150);
        foreach (var pair in new[] { ("ReanalyzeButton", recalculate), ("PipelineButton", activity), ("RecommendSettingsButton", settings) })
        {
            var button = (Button)panel.FindName(pair.Item1);
            if (button.Content is not System.Windows.Shapes.Path || button.ToolTip == null
                || string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button))
                || !ReferenceEquals(button.Command, pair.Item2)) throw new Exception("Toolbar icon lost its label or command: " + pair.Item1);
        }
        var aiButton = (Button)panel.FindName("FindWithAiButton");
        if (aiButton.Content is not Grid labels || !labels.Children.OfType<TextBlock>().Any(label => label.Visibility == Visibility.Visible && label.Text == L.T("AI 더 찾기"))
            || !ReferenceEquals(aiButton.Command, ai) || (string)aiButton.CommandParameter != "add")
            throw new Exception("AI action must keep its text and existing command parameter.");
    }

    sealed record ToolbarPreviewData(CommunityToolkit.Mvvm.Input.RelayCommand ReanalyzeCommand,
        CommunityToolkit.Mvvm.Input.RelayCommand OpenPipelineCommand, CommunityToolkit.Mvvm.Input.RelayCommand OpenRecommendSettingsCommand,
        CommunityToolkit.Mvvm.Input.RelayCommand FindWithAiCommand, string FilterSummary)
    {
        public bool IsRecsTab => true;
        public bool DebugPipeline => true;
        public bool HasAiSaved => true;
        public bool CanAiWeekly => false;
        public bool AiBusy => false;
        public bool AiReplace { get; set; }
        public bool AiWeekly { get; set; }
        public string AiText => "";
        public string AiSavedNote => "";
        public object[] TypeChips => [];
    }
}
