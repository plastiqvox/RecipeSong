using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Polyline = System.Windows.Shapes.Polyline;
using RecipeSong.Core;
using RecipeSong.Wpf;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckPipeline(string output)
    {
        var monitor = PipelineMonitor.Instance;
        StageItem Node(string key) => monitor.Stages.Single(s => s.Key == key);
        var onlyArchive = monitor.Begin(L.F("ZIP 경로 {0}개", 1), RunKind.Archive, primary: false);
        var completed = L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 5);
        monitor.CompleteArchive(onlyArchive, completed);
        if (monitor.Status != completed) throw new Exception("A standalone ZIP scan must retain its completion status.");
        if (!monitor.Edges.Any(e => e.From == "archive" && e.To == "identity")
            || !monitor.Edges.Any(e => e.From == "archive" && e.To == "releases")
            || monitor.Edges.Any(e => e.From == "archive" && e.To is "base" or "seed" or "score"))
            throw new Exception("ZIP ownership must feed owned/identity checks, without affecting taste.");

        var recommendation = monitor.Begin("2026.11 · " + L.T("보통 계산"), RunKind.Load);
        monitor.Stage(recommendation, RecStage.Listen);
        var reading = monitor.Begin(L.F("ZIP 경로 {0}개", 2), RunKind.Archive, primary: false);
        var detail = L.F("ZIP {0}개 처리 · 앨범 {1}개 · 재사용 {2}개 · 경고 {3}개", 125, 121, 100, 4)
            + "\n" + L.T("ZIP 메타데이터를 읽는 중…") + "\nD:\\Archive\\[2026.02.03] (Album) Artist - Album.zip";
        monitor.Message(reading, detail);
        if (monitor.Primary?.Id != recommendation || Node("listen").State != StageState.Current
            || Node("archive").State != StageState.Current || Node("archive").Detail != detail)
            throw new Exception("ZIP scanning must not replace the recommendation or reset its stage.");

        var window = new PipelineWindow();
        var chart = (Canvas)window.FindName("Chart");
        // 선이 입력 데이터 칸을 관통하지 않는지 실제 Canvas 좌표로 확인한다.
        var boxes = chart.Children.OfType<Border>().Select(b => new Rect(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height)).ToArray();
        foreach (var line in chart.Children.OfType<Polyline>())
            for (var i = 1; i < line.Points.Count; i++)
            {
                var a = line.Points[i - 1];
                var b = line.Points[i];
                foreach (var box in boxes)
                    if (a.X == b.X && a.X > box.Left && a.X < box.Right && Math.Max(a.Y, b.Y) > box.Top && Math.Min(a.Y, b.Y) < box.Bottom
                        || a.Y == b.Y && a.Y > box.Top && a.Y < box.Bottom && Math.Max(a.X, b.X) > box.Left && Math.Min(a.X, b.X) < box.Right)
                        throw new Exception("A pipeline edge crosses a node: " + box);
            }
        var segments = chart.Children.OfType<Polyline>().SelectMany(line => line.Points.Zip(line.Points.Skip(1))).ToArray();
        foreach (var (a, b) in segments)
            foreach (var (c, d) in segments)
            {
                bool horizontal = a.Y == b.Y && c.Y == d.Y && a.Y == c.Y && (b.X - a.X) * (d.X - c.X) < 0
                    && Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) < Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
                bool vertical = a.X == b.X && c.X == d.X && a.X == c.X && (b.Y - a.Y) * (d.Y - c.Y) < 0
                    && Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) < Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
                if (horizontal || vertical) throw new Exception("Opposing edges must not share a segment and imply a bidirectional relationship.");
            }
        var baseline = chart.Children.OfType<TextBlock>().Single(t => t.Text == L.T("비교 기준"));
        var labelBox = new Rect(Canvas.GetLeft(baseline), Canvas.GetTop(baseline), baseline.DesiredSize.Width, baseline.DesiredSize.Height);
        foreach (var (a, b) in segments)
            if (a.X == b.X && a.X > labelBox.Left && a.X < labelBox.Right && Math.Max(a.Y, b.Y) > labelBox.Top && Math.Min(a.Y, b.Y) < labelBox.Bottom)
                throw new Exception("The sound baseline label must not cross the ZIP-to-ID connection.");
        RenderPipeline(window, Path.Combine(output, "pipeline-active.png"), 900, 1000, checkControls: true);
        RenderPipeline(new PipelineWindow(), Path.Combine(output, "pipeline-narrow.png"), 480, 700);

        monitor.Stage(recommendation, RecStage.Score);
        if (Node("archive").State != StageState.Current || Node("archive").Detail != detail)
            throw new Exception("Recommendation progress must not reset an independent ZIP scan.");
        monitor.CompleteArchive(reading, L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 121));
        monitor.Message(reading, "late update");
        if (Node("archive").State != StageState.Done || Node("archive").Detail == "late update" || monitor.Runs.Any(r => r.Id == reading))
            throw new Exception("Completed ZIP scans must ignore late reports and leave the active run list.");
        foreach (var (note, canceled, state) in new[]
        {
            (L.T("ZIP 읽기 취소"), true, StageState.Cancelled),
            (L.F("ZIP 읽기 실패: {0}", "test"), false, StageState.Failed),
        })
        {
            var stopped = monitor.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
            monitor.Finish(stopped, note, canceled);
            var status = L.F(canceled ? "{0} · ZIP 읽기 취소" : "{0} · ZIP 읽기 실패", L.T("진행 중"));
            if (Node("archive").State != state || Node("archive").Detail != note || monitor.Status != status)
                throw new Exception("Canceled and failed ZIP scans must have distinct states and remain visible in the summary.");
            if (canceled) RenderPipeline(new PipelineWindow(), Path.Combine(output, "pipeline-cancelled.png"), 900, 1000);
        }
        monitor.Finish(recommendation, L.T("멈춤"));
        var cached = monitor.Begin("2026.11 · " + L.T("보통 계산"), RunKind.Load);
        monitor.SetSavedResult(cached, L.T("저장된 추천을 보여 줌 · 새로 계산하지 않음"), reused: true);
        monitor.Finish(cached, L.T("끝"));
        monitor.SetSavedResult(recommendation, "stale report", reused: false);
        if (Node("saved").State != StageState.Reused || Node("saved").Detail == "stale report" || Node("screen").State != StageState.Done)
            throw new Exception("Cached recommendations must reach the screen and ignore older run reports.");
        if (monitor.Status != L.F("{0} · ZIP 읽기 실패", L.T("끝")))
            throw new Exception("A completed recommendation must not hide an earlier ZIP failure.");
        RenderPipeline(new PipelineWindow(), Path.Combine(output, "pipeline-reused.png"), 900, 1000);
        var retry = monitor.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
        monitor.CompleteArchive(retry, completed);
        if (monitor.Status != L.T("끝")) throw new Exception("A successful ZIP retry must clear the failure summary.");
    }

    static void RenderPipeline(PipelineWindow window, string path, int width, int height, bool checkControls = false)
    {
        var content = (FrameworkElement)window.Content;
        content.DataContext = window.DataContext;
        window.Content = null;
        var viewport = (ScrollViewer)window.FindName("ChartViewport");
        var scale = (ScaleTransform)window.FindName("ChartScale");
        var follow = (CheckBox)window.FindName("FollowStep");
        var log = (ToggleButton)window.FindName("LogToggle");
        Render(content, path, width, height);
        if (scale.ScaleX != 1 || viewport.ScrollableWidth <= 0 || log.IsChecked == true)
            throw new Exception("The chart must retain readable size with scrolling and a collapsed log, even in narrow windows.");
        if (checkControls)
        {
            var zoomIn = (Button)window.FindName("ZoomInButton");
            zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scale.ScaleX != 1.25) throw new Exception("Zoom in must enlarge the chart.");
            for (int i = 0; i < 10; i++) zoomIn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scale.ScaleX != 2 || zoomIn.IsEnabled) throw new Exception("Zoom must stop at its upper limit.");
            var zoomOut = (Button)window.FindName("ZoomOutButton");
            for (int i = 0; i < 10; i++) zoomOut.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scale.ScaleX != 0.75 || zoomOut.IsEnabled) throw new Exception("Zoom must stop at its lower limit.");
            ((Button)window.FindName("ZoomResetButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scale.ScaleX != 1) throw new Exception("Actual size must restore 100%.");
            var heightBefore = viewport.ViewportHeight;
            log.IsChecked = true;
            Render(content, Path.Combine(Path.GetDirectoryName(path)!, "pipeline-log.png"), width, height);
            if (viewport.ViewportHeight >= heightBefore) throw new Exception("Expanding the log must leave a scrollable chart above it.");
            log.IsChecked = false;
            follow.IsChecked = false;
            viewport.ScrollToVerticalOffset(0);
            follow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(content, path, width, height);
            if (viewport.VerticalOffset != 0) throw new Exception("Turning follow off must allow manual inspection.");
            follow.IsChecked = true;
        }
        follow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Render(content, path, width, height);
        if (viewport.VerticalOffset <= 0) throw new Exception("Follow active step must reveal progress without shrinking the chart.");
        window.Close();
    }
}
