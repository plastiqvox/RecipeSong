using System.IO;
using System.Windows;
using RecipeSong.Core;
using RecipeSong.Wpf.ViewModels;
using Microsoft.Win32;

namespace RecipeSong.Wpf;

public partial class App : Application
{
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 내장 + 외장 그래픽카드 조합에서 하드웨어 렌더링 창이 하얗게만 나오는 경우가 있어 소프트웨어 렌더링이 기본.
        // 하드웨어 렌더링을 쓰려면 CRATE_HW_RENDER=1
        if (Environment.GetEnvironmentVariable("CRATE_HW_RENDER") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "RecipeSong 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        // iTunes 라이브러리는 자동으로 찾고, 못 찾을 때만 한 번 묻는다
        var settings = AppSettings.Load();
        L.Init(settings.Language);
        // 캐시 폴더는 캐시를 처음 만들기 전에 정한다
        if (settings.CacheDirectory is { Length: > 0 } cacheDir) CacheLocation.Directory = cacheDir;
        if (settings.ITunesLibraryPath is not { } xml || !File.Exists(xml))
        {
            var dlg = new OpenFileDialog
            {
                Title = "iTunes 라이브러리 XML을 선택하세요 (iTunes Music Library.xml)",
                Filter = "iTunes 라이브러리 (*.xml)|*.xml",
            };
            if (dlg.ShowDialog() != true) { Shutdown(); return; }
            settings.ITunesLibraryPath = dlg.FileName;
            settings.Save();
        }

        _vm = new MainViewModel(settings);
        new MainWindow(_vm).Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_vm != null) await _vm.DisposeAsync();
        base.OnExit(e);
    }
}
