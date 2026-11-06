using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RecipeSong.Core;
using RecipeSong.Wpf;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckPipelineAccessibility(string output, int recommendation, int reading, string detail)
    {
        var m = PipelineMonitor.Instance;
        var window = new PipelineWindow();
        var chart = (Canvas)window.FindName("Chart");
        var buttons = chart.Children.OfType<Button>().ToArray();
        if (buttons.Length != m.Stages.Count || buttons.Any(b => !b.Focusable || !KeyboardNavigation.GetIsTabStop(b)
            || string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))))
            throw new Exception("Every diagram node must be a named keyboard-accessible button.");
        var archive = buttons.Single(b => (string)b.Tag == "archive");
        archive.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, archive)
            { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
        var details = (TextBox)window.FindName("NodeDetailText");
        if (!details.IsReadOnly || !details.Text.Contains(detail) || ((CheckBox)window.FindName("FollowStep")).IsChecked == true)
            throw new Exception("Keyboard selection must expose full details and pause automatic following.");

        var summary = (TextBlock)window.FindName("StatusSummary");
        var announcement = AutomationProperties.GetName(summary);
        m.Message(reading, detail + "\nD:\\Archive\\very-long-path\\untruncated-album-name.zip");
        if (AutomationProperties.GetName(summary) != announcement || !details.Text.Contains("untruncated-album-name.zip"))
            throw new Exception("Progress details must update without repeatedly announcing counts or paths.");
        m.Stage(recommendation, RecStage.Score);
        if (!AutomationProperties.GetName(summary).Contains(m.Stages.Single(s => s.Stage == RecStage.Score).Label)
            || AutomationProperties.GetLiveSetting(summary) != AutomationLiveSetting.Polite)
            throw new Exception("Stage changes must provide a contextual polite announcement.");
        m.Stage(recommendation, RecStage.Listen);
        m.Message(reading, detail);

        var toggle = (ToggleButton)window.FindName("OverviewToggle");
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var overview = (ListBox)window.FindName("OverviewList");
        if (overview.Visibility != Visibility.Visible || overview.Items.Count != m.Stages.Count
            || ((ScrollViewer)window.FindName("ChartViewport")).Visibility != Visibility.Collapsed)
            throw new Exception("Overview must show every node and state without requiring horizontal chart navigation.");
        overview.SelectedItem = m.Stages.Single(s => s.Key == "archive");
        if (!details.Text.Contains(detail)) throw new Exception("Overview selection must share the same complete details.");
        RenderWindow(window, Path.Combine(output, "pipeline-overview.png"), 480, 700);
    }

    static void CheckPipelineRecovery(string output)
    {
        // Application.Run을 쓰지 않는 시험이므로 완료 콜백도 같은 시험 스레드에서 처리한다.
        static void Retry(Button button)
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        var m = PipelineMonitor.Instance;
        var failure = m.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
        m.Finish(failure, L.F("ZIP 읽기 실패: {0}", "test"));
        int settingsOpened = 0, retryCount = 0, retryRun = 0;
        var pending = new TaskCompletionSource<bool>();
        var window = new PipelineWindow(ct =>
        {
            retryCount++;
            retryRun = m.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
            return pending.Task;
        }, () => settingsOpened++);
        var retry = (Button)window.FindName("RetryArchiveButton");
        var settings = (Button)window.FindName("ArchiveSettingsButton");
        if (!retry.IsEnabled || !settings.IsEnabled) throw new Exception("Failed scans must offer working recovery actions.");
        settings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Retry(retry);
        Retry(retry);
        if (settingsOpened != 1 || retryCount != 1 || retry.IsEnabled || settings.IsEnabled)
            throw new Exception("Recovery must invoke its configured actions and prevent duplicate reads.");
        m.CompleteArchive(retryRun, L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 5));
        pending.SetResult(true);
        if (((Border)window.FindName("RecoveryPanel")).Visibility != Visibility.Collapsed)
            throw new Exception("A successful retry must clear recovery UI.");
        window.Close();

        var failedRetry = m.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
        m.Finish(failedRetry, L.F("ZIP 읽기 실패: {0}", "test"));
        var failedWindow = new PipelineWindow(_ => Task.FromException(new IOException("retry error")), () => { });
        Retry((Button)failedWindow.FindName("RetryArchiveButton"));
        if (!((TextBlock)failedWindow.FindName("RecoveryMessage")).Text.Contains("retry error"))
            throw new Exception("Retry exceptions must provide visible recovery feedback.");
        var fixedElsewhere = m.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
        m.CompleteArchive(fixedElsewhere, L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 5));
        if (((Border)failedWindow.FindName("RecoveryPanel")).Visibility != Visibility.Collapsed)
            throw new Exception("A later successful scan must clear stale local retry errors too.");
        failedWindow.Close();

        var again = m.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
        m.Finish(again, L.F("ZIP 읽기 실패: {0}", "test"));
        CancellationToken cancellation = default;
        var waiting = new TaskCompletionSource<bool>();
        var closing = new PipelineWindow(ct =>
        {
            cancellation = ct;
            ct.Register(() => waiting.TrySetCanceled(ct));
            return waiting.Task;
        }, () => { });
        RenderWindow(new PipelineWindow(_ => Task.CompletedTask, () => { }), Path.Combine(output, "pipeline-recovery.png"), 900, 1000);
        Retry((Button)closing.FindName("RetryArchiveButton"));
        closing.Close();
        if (!cancellation.IsCancellationRequested) throw new Exception("Closing the recovery window must cancel only its own retry.");
    }
}
