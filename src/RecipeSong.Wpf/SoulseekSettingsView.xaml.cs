using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public partial class SoulseekSettingsView : UserControl, IDisposable
{
    private readonly SoulseekSession _session;
    private readonly ObservableCollection<string> _shares = [];
    private readonly CancellationTokenSource _life = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public SoulseekSettingsView(SoulseekSession session)
    {
        InitializeComponent();
        _session = session;
        var settings = SoulseekSettings.Load();
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = settings.GetPassword();
        RememberBox.IsChecked = settings.RememberPassword;
        AutoBox.IsChecked = settings.AutoConnect;
        foreach (var path in settings.SharedDirectories) _shares.Add(path);
        SharesList.ItemsSource = _shares;
        DownSlotsBox.Text = settings.DownloadSlots.ToString(); UpSlotsBox.Text = settings.UploadSlots.ToString();
        DownUserBox.Text = settings.DownloadsPerUser.ToString(); UpUserBox.Text = settings.UploadsPerUser.ToString();
        DownSpeedBox.Text = settings.DownloadKiB.ToString(); UpSpeedBox.Text = settings.UploadKiB.ToString();
        DownUnlimited.IsChecked = settings.DownloadKiB == 0; UpUnlimited.IsChecked = settings.UploadKiB == 0;
        PortBox.Text = settings.ListenPort.ToString();
        ArchiveBox.Text = settings.ArchiveDirectory; DownloadsBox.Text = settings.DownloadsDirectory; FfmpegBox.Text = settings.FfmpegPath;
        _timer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) => { RefreshStatus(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public event Action? LoginSucceeded;
    public void Dispose() { _timer.Stop(); _life.Cancel(); }

    private void RefreshStatus()
    {
        ConnectionText.Text = _session.Status;
        ShareStatus.Text = _session.Shares.Status;
    }

    private SoulseekSettings ReadSettings()
    {
        int Number(TextBox box) => int.TryParse(box.Text, out var n) ? n : throw new ArgumentException(L.T("숫자로 입력하세요."));
        var settings = new SoulseekSettings
        {
            Username = UsernameBox.Text.Trim(), RememberPassword = RememberBox.IsChecked == true, AutoConnect = AutoBox.IsChecked == true,
            SharedDirectories = _shares.ToList(), DownloadSlots = Number(DownSlotsBox), UploadSlots = Number(UpSlotsBox),
            DownloadsPerUser = Number(DownUserBox), UploadsPerUser = Number(UpUserBox),
            DownloadKiB = DownUnlimited.IsChecked == true ? 0 : Number(DownSpeedBox), UploadKiB = UpUnlimited.IsChecked == true ? 0 : Number(UpSpeedBox),
            ListenPort = Number(PortBox), ArchiveDirectory = ArchiveBox.Text.Trim(), DownloadsDirectory = DownloadsBox.Text.Trim(), FfmpegPath = FfmpegBox.Text.Trim(),
        };
        settings.SetPassword(PasswordBox.Password);
        settings.Validate();
        return settings;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            settings.Save();
            await _session.ApplyAsync(settings, _life.Token);
            StatusText.Text = L.T("설정을 저장하고 공유 목록을 갱신했어요.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            settings.Save();
            await _session.LoginAsync(settings, PasswordBox.Password, _life.Token);
            StatusText.Text = _session.Status;
            if (_session.IsLoggedIn) LoginSucceeded?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { LoginButton.IsEnabled = true; RefreshStatus(); }
    }

    private void Logout_Click(object sender, RoutedEventArgs e) { _session.Logout(); RefreshStatus(); }
    private void Unlimited_Changed(object sender, RoutedEventArgs e)
    {
        if (DownSpeedBox != null) DownSpeedBox.IsEnabled = DownUnlimited.IsChecked != true;
        if (UpSpeedBox != null) UpSpeedBox.IsEnabled = UpUnlimited.IsChecked != true;
    }

    private void AddShare_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !_shares.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) _shares.Add(dialog.FolderName);
    }
    private void RemoveShare_Click(object sender, RoutedEventArgs e) { if (SharesList.SelectedItem is string path) _shares.Remove(path); }
    private void ArchiveFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(ArchiveBox.Text)) dialog.InitialDirectory = ArchiveBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ArchiveBox.Text = dialog.FolderName;
    }

    private void DownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(DownloadsBox.Text)) dialog.InitialDirectory = DownloadsBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DownloadsBox.Text = dialog.FolderName;
    }

    private async void ImportNs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var imported = await Task.Run(SoulseekNsImport.ReadInstalled);
            if (_life.IsCancellationRequested) return;
            var preview = new SoulseekImportWindow(imported) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) return;
            if (preview.ImportUsername) UsernameBox.Text = imported.Username;
            if (preview.ImportPassword) { PasswordBox.Password = imported.Password; RememberBox.IsChecked = true; AutoBox.IsChecked = false; }
            if (preview.ImportDownloads) DownloadsBox.Text = imported.DownloadsDirectory;
            foreach (var path in preview.SharedFolders)
                if (!_shares.Contains(path, StringComparer.OrdinalIgnoreCase)) _shares.Add(path);
            StatusText.Text = L.T("Soulseek NS 설정을 가져왔어요. 설정 저장을 누르면 보관해요.");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var found = SoulseekQtImport.FindConfiguration();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("SoulseekQt 설정 파일을 선택하세요"),
                Filter = "SoulseekQt (soulseek-client.dat*)|soulseek-client.dat*|All files|*.*",
                InitialDirectory = found == null ? "" : Path.GetDirectoryName(found), FileName = found == null ? "" : Path.GetFileName(found),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var candidates = await Task.Run(() => SoulseekQtImport.ReadFolderCandidates(dialog.FileName));
            if (candidates.Count == 0) { StatusText.Text = L.T("읽을 수 있는 폴더가 없어요. 공유 폴더를 직접 추가하세요."); return; }
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = L.T("공유 폴더 후보예요. 실제로 공개할 폴더만 선택하세요. ID·비밀번호·전송 제한은 직접 입력해야 해요."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var choices = candidates.Select(p => new CheckBox { Content = p, Tag = p, Margin = new Thickness(0, 4, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("Text") }).ToList();
            foreach (var choice in choices) panel.Children.Add(choice);
            var apply = new Button { Content = L.T("선택한 폴더 추가"), Style = (Style)FindResource("Bare"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(apply);
            var preview = new Window { Owner = Window.GetWindow(this), Title = L.T("SoulseekQt 가져오기 미리보기"), Width = 660, Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            DarkTitleBar.Apply(preview);
            apply.Click += (_, _) => { foreach (var choice in choices.Where(c => c.IsChecked == true)) if (!_shares.Contains((string)choice.Tag, StringComparer.OrdinalIgnoreCase)) _shares.Add((string)choice.Tag); preview.Close(); };
            preview.ShowDialog();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }
}
