using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using RecipeSong.Core;

static partial class Program
{
    static void CheckSharedWorkspace(string output, string language)
    {
        // 실제 XAML에서 코드 비하인드 연결만 제거한다. MainWindow/MainViewModel 생성자는 실행하지 않는다.
        var xml = XDocument.Load("src/RecipeSong.Wpf/MainWindow.xaml");
        var root = xml.Root!;
        root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        foreach (var ns in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value.StartsWith("clr-namespace:RecipeSong.")))
            ns.Value += ";assembly=RecipeSong.Wpf";
        foreach (var node in root.Descendants().Where(n => n.Name.NamespaceName.StartsWith("clr-namespace:RecipeSong.")))
            node.Name = XName.Get(node.Name.LocalName, node.Name.NamespaceName + ";assembly=RecipeSong.Wpf");
        var events = new[] { typeof(Window), typeof(Control), typeof(Button), typeof(TextBox), typeof(TabControl),
            typeof(ComboBox), typeof(Slider), typeof(ScrollViewer), typeof(System.Windows.Controls.Primitives.Popup), typeof(System.Windows.Controls.Primitives.Thumb) }
            .SelectMany(t => t.GetEvents()).Select(e => e.Name).ToHashSet();
        foreach (var node in root.DescendantsAndSelf())
            foreach (var a in node.Attributes().Where(a => !a.IsNamespaceDeclaration && events.Contains(a.Name.LocalName.Split('.').Last())).ToArray()) a.Remove();
        foreach (var source in root.Descendants().Attributes("Source").Where(a => a.Parent!.Name.LocalName == "ResourceDictionary"))
            if (!source.Value.StartsWith('/')) source.Value = "/RecipeSong.Wpf;component/" + source.Value;
        root.SetAttributeValue("Icon", "/RecipeSong.Wpf;component/Assets/logo.png");
        var window = (Window)XamlReader.Parse(xml.ToString());
        var fixture = new SharedWorkspaceFixture();
        window.DataContext = fixture;
        var tabs = (TabControl)window.FindName("WorkspaceTabs");
        var player = (Border)window.FindName("GlobalPlayer");
        if (tabs.Items.Count != 3 || player.Parent != tabs.Parent)
            throw new Exception("The common player must be outside all three tab contents.");
        fixture.HasDetail = true;
        tabs.SelectedIndex = 0;
        ((FrameworkElement)window.Content).Measure(new Size(1040, 680));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (player.Visibility != Visibility.Collapsed) throw new Exception("Recommendation detail has duplicate players.");
        tabs.SelectedIndex = 1;
        if (player.Visibility != Visibility.Visible) throw new Exception("Switching to artists hid the active player.");
        tabs.SelectedIndex = 2;
        if (player.Visibility != Visibility.Visible) throw new Exception("Switching to Soulseek hid the active player.");
        RenderWindow(window, Path.Combine(output, language + "-shared-workspace.png"), 1040, 680);
    }
}

public sealed class SharedWorkspaceFixture : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _hasDetail;
    public bool HasDetail { get => _hasDetail; set { _hasDetail = value; PropertyChanged?.Invoke(this, new(nameof(HasDetail))); } }
    public string SoulseekTabTitle => "Soulseek · 2";
    public string WorkspaceTaskText => L.T("추천 계산 준비 중…");
    public SharedPlayerFixture Player { get; } = new();
}

public sealed class SharedPlayerFixture
{
    public string NowCaption => "Artist – Album";
    public string Title => "Sample track";
    public string Subtitle => "Artist – Album";
    public bool HasNowRelease => true;
    public double Volume { get; set; } = 0.65;
}
