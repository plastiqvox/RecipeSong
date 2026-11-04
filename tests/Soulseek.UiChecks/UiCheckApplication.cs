using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

// Dispatcher를 처리하는 화면 시험에서도 사용자 설정·MainViewModel·소리 서비스를 시작하지 않는다.
sealed class UiCheckApplication : Application
{
    protected override void OnStartup(StartupEventArgs e) { }
    public void InitializeComponent()
    {
        var app = XDocument.Load("src/RecipeSong.Wpf/App.xaml").Root!;
        var dictionary = new XElement(app.Name.Namespace + "ResourceDictionary",
            app.Attributes().Where(a => a.IsNamespaceDeclaration), app.Elements().Single().Elements());
        foreach (var node in dictionary.DescendantsAndSelf())
        {
            foreach (var ns in node.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value.StartsWith("clr-namespace:RecipeSong.")))
                ns.Value += ";assembly=RecipeSong.Wpf";
            if (node.Name.NamespaceName.StartsWith("clr-namespace:RecipeSong."))
                node.Name = XName.Get(node.Name.LocalName, node.Name.NamespaceName + ";assembly=RecipeSong.Wpf");
            if (node.Name.LocalName == "ResourceDictionary" && node.Attribute("Source") is { } source && !source.Value.StartsWith('/'))
                source.Value = "/RecipeSong.Wpf;component/" + source.Value;
        }
        Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
}
