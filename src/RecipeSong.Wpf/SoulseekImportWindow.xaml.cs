using System.Windows;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public partial class SoulseekImportWindow : Window
{
    private readonly IReadOnlyList<FolderChoice> _folders;
    public bool ImportUsername => AccountBox.IsChecked == true;
    public bool ImportPassword => PasswordBox.IsChecked == true;
    public bool ImportDownloads => DownloadsBox.IsChecked == true;
    public IReadOnlyList<string> SharedFolders => _folders.Where(f => f.Selected).Select(f => f.Path).ToArray();

    public SoulseekImportWindow(SoulseekImportedSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        AccountBox.Content = L.F("Soulseek ID · {0}", settings.Username);
        PasswordBox.IsEnabled = settings.Password.Length > 0;
        PasswordBox.IsChecked = settings.Password.Length > 0;
        DownloadPathText.Text = settings.DownloadsDirectory;
        _folders = settings.SharedDirectories.Select(p => new FolderChoice(p, !settings.RequiresShareReview)).ToArray();
        FolderChoices.ItemsSource = _folders;
        ReviewHint.Visibility = settings.RequiresShareReview ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    public sealed class FolderChoice(string path, bool selected)
    {
        public string Path { get; } = path;
        public bool Selected { get; set; } = selected;
    }
}
