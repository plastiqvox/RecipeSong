using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        RenderWindow(window, Path.Combine(output, "pipeline-active.png"), 900, 1000);
        RenderWindow(new PipelineWindow(), Path.Combine(output, "pipeline-narrow.png"), 480, 700);

        monitor.Stage(recommendation, RecStage.Score);
        if (Node("archive").State != StageState.Current || Node("archive").Detail != detail)
            throw new Exception("Recommendation progress must not reset an independent ZIP scan.");
        monitor.CompleteArchive(reading, L.F("ZIP 보유 앨범 {0}개 · 취향 평가는 중립", 121));
        monitor.Message(reading, "late update");
        if (Node("archive").State != StageState.Done || Node("archive").Detail == "late update" || monitor.Runs.Any(r => r.Id == reading))
            throw new Exception("Completed ZIP scans must ignore late reports and leave the active run list.");
        foreach (var note in new[] { L.T("ZIP 읽기 취소"), L.F("ZIP 읽기 실패: {0}", "test") })
        {
            var stopped = monitor.Begin(L.T("ZIP 보유 읽기"), RunKind.Archive, primary: false);
            monitor.Finish(stopped, note);
            if (Node("archive").State == StageState.Done || Node("archive").Detail != note)
                throw new Exception("Canceled/failed ZIP scans must not appear completed.");
        }
        monitor.Finish(recommendation, L.T("멈춤"));
        var cached = monitor.Begin("2026.11 · " + L.T("보통 계산"), RunKind.Load);
        monitor.SetSavedResult(cached, L.T("저장된 추천을 보여 줌 · 새로 계산하지 않음"), reused: true);
        monitor.Finish(cached, L.T("끝"));
        monitor.SetSavedResult(recommendation, "stale report", reused: false);
        if (Node("saved").State != StageState.Reused || Node("saved").Detail == "stale report" || Node("screen").State != StageState.Done)
            throw new Exception("Cached recommendations must reach the screen and ignore older run reports.");
        RenderWindow(new PipelineWindow(), Path.Combine(output, "pipeline-reused.png"), 900, 1000);
    }
}
