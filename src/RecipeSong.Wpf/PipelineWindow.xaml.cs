using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using RecipeSong.Core;
using RecipeSong.Wpf.ViewModels;

namespace RecipeSong.Wpf;

/// <summary>
/// 디버그: 추천 계산의 순서도와 지금 단계 (설정 → 디버그). 창은 하나만 두고, 닫았으면 다음 계산 때 다시 연다.
/// 칸·화살표는 PipelineMonitor.Stages·Edges 에서 그리고, 칸 상태가 바뀌면 색만 바꾼다.
/// </summary>
public partial class PipelineWindow : Window
{
    private static PipelineWindow? _shared;

    /// <summary>창이 떠 있으면 기간을 바꿀 때 바로 새 기간으로 바꿔 보여 준다 (MainViewModel.LoadAsync)</summary>
    public static bool IsOpen => _shared != null;

    // 원래 크기로 보여 주고 사용자가 확대·스크롤한다. 네 줄 배치는 PipelineMonitor.Stages 설명을 따른다.
    private static readonly double[] ColX = [140, 420, 690, 950];
    private static readonly double[] ColW = [230, 260, 230, 230];
    private const double RowTop = 8, RowStep = 102, NodeH = 72, ChartW = 1090;
    private const int Rows = 9;

    // 소리 서비스 칸: 맨 오른쪽 줄 6~8행 (요청을 하나씩 처리 — 미리듣기·분석·옆 작업이 여기서 줄을 선다)
    private readonly StackPanel _queuePanel = new();

    private readonly Dictionary<string, Node> _nodes = [];
    private readonly List<(StageEdge Edge, Shape[] Parts)> _edges = [];
    private string? _followedNode;
    private string? _selectedKey;
    private string? _announcementKey;
    private readonly Func<CancellationToken, Task>? _retryArchive;
    private readonly Action? _openSettings;
    private CancellationTokenSource? _retryCancellation;
    private bool _recoveryBusy, _closed;
    private string? _recoveryError;
    private DateTime _recoveryErrorScan;

    public PipelineWindow() : this(null, null) { }

    public PipelineWindow(Func<CancellationToken, Task>? retryArchive, Action? openSettings)
    {
        InitializeComponent();
        _retryArchive = retryArchive;
        _openSettings = openSettings;
        DarkTitleBar.Apply(this);
        var m = PipelineMonitor.Instance;
        DataContext = m;
        Chart.Width = ChartW;
        Chart.Height = RowTop + (Rows - 1) * RowStep + NodeH + 8;
        foreach (var e in m.Edges) DrawEdge(e, m.Stages.First(s => s.Key == e.From), m.Stages.First(s => s.Key == e.To));
        foreach (var s in m.Stages) DrawNode(s);
        DrawQueue();
        OverviewList.ItemsSource = m.Stages;
        Paint();
        // 다른 계산의 위치·시간은 칸 상태가 아니라서 따로 알린다
        m.Changed += Paint;
        // 도는 계산이 없어도 소리 서비스 줄(멈춘 요청 마무리 등)과 '소식 없음'은 바뀐다 → 창이 열려 있는 동안 1초마다 다시 그린다
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Paint();
        clock.Start();
        Closed += (_, _) => { _closed = true; _retryCancellation?.Cancel(); m.Changed -= Paint; clock.Stop(); };
        Loaded += (_, _) => { FollowCurrent(force: true); AnnounceStatus(force: true); };
        ChartViewport.SizeChanged += (_, _) => { if (IsLoaded) FollowCurrent(force: true); };
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(ChartScale.ScaleX - 0.25);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(ChartScale.ScaleX + 0.25);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1);
    private void FollowStep_Click(object sender, RoutedEventArgs e) => FollowCurrent(force: true);

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        bool overview = OverviewToggle.IsChecked == true;
        var label = overview ? L.T("상세 흐름도") : L.T("전체 개요");
        OverviewToggle.Content = label;
        AutomationProperties.SetName(OverviewToggle, label);
        OverviewList.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        ChartViewport.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        UpdateZoomButtons();
        if (overview)
        {
            if (_selectedKey != null)
            {
                OverviewList.SelectedItem = PipelineMonitor.Instance.Stages.First(s => s.Key == _selectedKey);
                OverviewList.ScrollIntoView(OverviewList.SelectedItem);
            }
            OverviewList.Focus();
        }
        else if (_selectedKey != null)
        {
            ChartViewport.UpdateLayout();
            _nodes[_selectedKey].Body.BringIntoView();
        }
        else FollowCurrent(force: true);
    }

    private void Overview_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewList.SelectedItem is StageItem item) SelectNode(item.Key);
    }

    private void Overview_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OverviewList.SelectedItem != null)
        {
            NodeDetailText.Focus();
            e.Handled = true;
        }
    }

    private void SelectNode(string key)
    {
        _selectedKey = key;
        FollowStep.IsChecked = false;
        Paint();
        if (ChartViewport.Visibility == Visibility.Visible)
        {
            ChartViewport.UpdateLayout();
            _nodes[key].Body.BringIntoView();
        }
    }

    private void CloseNodeDetail_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedKey;
        _selectedKey = null;
        OverviewList.SelectedItem = null;
        Paint();
        if (OverviewToggle.IsChecked == true) OverviewList.Focus();
        else if (selected != null) ChartViewport.Focus();
    }

    private async void RetryArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_recoveryBusy || _retryArchive == null) return;
        using var cancel = new CancellationTokenSource();
        _retryCancellation = cancel;
        _recoveryBusy = true;
        _recoveryError = null;
        Paint();
        try { await _retryArchive(cancel.Token); }
        catch (OperationCanceledException) { SetRecoveryError(L.T("ZIP 읽기 취소")); }
        catch (Exception ex) { SetRecoveryError(L.F("ZIP 읽기 실패: {0}", ex.Message)); }
        finally
        {
            _recoveryBusy = false;
            _retryCancellation = null;
            if (!_closed) Paint();
        }
    }

    private void ArchiveSettings_Click(object sender, RoutedEventArgs e) => _openSettings?.Invoke();

    private void SetRecoveryError(string message)
    {
        _recoveryError = message;
        _recoveryErrorScan = PipelineMonitor.Instance.Stages.First(s => s.Key == "archive").LastNews;
    }

    private void UpdateZoomButtons()
    {
        bool detail = OverviewToggle.IsChecked != true;
        ZoomOutButton.IsEnabled = detail && ChartScale.ScaleX > 0.75;
        ZoomInButton.IsEnabled = detail && ChartScale.ScaleX < 2;
        ZoomResetButton.IsEnabled = detail;
        FollowStep.IsEnabled = detail;
        FollowStep.Opacity = detail ? 1 : 0.45;
        ZoomOutButton.Opacity = ZoomOutButton.IsEnabled ? 1 : 0.45;
        ZoomInButton.Opacity = ZoomInButton.IsEnabled ? 1 : 0.45;
    }

    private void SetZoom(double value)
    {
        var old = ChartScale.ScaleX;
        var x = (ChartViewport.HorizontalOffset + ChartViewport.ViewportWidth / 2) / old;
        var y = (ChartViewport.VerticalOffset + ChartViewport.ViewportHeight / 2) / old;
        var scale = Math.Clamp(Math.Round(value, 2), 0.75, 2);
        ChartScale.ScaleX = ChartScale.ScaleY = scale;
        ZoomResetButton.Content = (scale * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
        UpdateZoomButtons();
        ChartViewport.UpdateLayout();
        ChartViewport.ScrollToHorizontalOffset(x * scale - ChartViewport.ViewportWidth / 2);
        ChartViewport.ScrollToVerticalOffset(y * scale - ChartViewport.ViewportHeight / 2);
        FollowCurrent(force: true);
    }

    private void FollowCurrent(bool force = false)
    {
        if (OverviewToggle.IsChecked == true || FollowStep.IsChecked != true || (!force && !IsLoaded) || ChartViewport.ViewportWidth <= 0) return;
        var m = PipelineMonitor.Instance;
        var node = _nodes.Values.FirstOrDefault(n => n.Item.State == StageState.Current && n.Item.Stage == m.Primary?.Current)
            ?? _nodes.Values.FirstOrDefault(n => n.Item.State == StageState.Current)
            ?? _nodes.Values.FirstOrDefault(n => n.Item.Key == "archive" && n.Item.State is StageState.Failed or StageState.Cancelled)
            ?? _nodes.Values.FirstOrDefault(n => n.Item.Key == "screen" && n.Item.State == StageState.Done)
            ?? _nodes.Values.FirstOrDefault(n => n.Item.Key == "archive" && n.Item.State == StageState.Done);
        if (node == null) return;
        var run = node.Item.Key == "archive" ? m.Runs.FirstOrDefault(r => r.Kind == RunKind.Archive && !r.Ended)
            : node.Item.Stage == RecStage.Analyze ? m.Runs.FirstOrDefault(r => r.Kind == RunKind.Analysis && !r.Ended) : m.Primary;
        var key = run?.Id + ":" + node.Item.Key + ":" + node.Item.State;
        if (!force && key == _followedNode) return;
        _followedNode = key;
        var box = Box(node.Item);
        ChartViewport.ScrollToHorizontalOffset((box.Left + box.Width / 2) * ChartScale.ScaleX - ChartViewport.ViewportWidth / 2);
        ChartViewport.ScrollToVerticalOffset((box.Top + box.Height / 2) * ChartScale.ScaleY - ChartViewport.ViewportHeight / 2);
    }

    /// <summary>순서도 버튼: 열려 있으면 닫고, 닫혀 있으면 연다 (닫아도 계산 상태는 PipelineMonitor 에 남아 다시 열면 그대로 보인다)</summary>
    public static void Toggle()
    {
        if (_shared != null) _shared.Close();
        else ShowShared(activate: true);
    }

    /// <summary>열려 있으면 그대로, 아니면 메인 창 오른쪽 옆에 연다. 계산이 띄울 때는 포커스를 가져가지 않고, 버튼으로 열면(activate) 앞으로</summary>
    public static void ShowShared(bool activate = false)
    {
        if (_shared != null)
        {
            if (activate) { _shared.WindowState = WindowState.Normal; _shared.Activate(); }
            return;
        }
        var main = Application.Current.MainWindow;
        var vm = main?.DataContext as MainViewModel;
        _shared = new PipelineWindow(vm == null ? null : async ct => { await vm.RefreshArchiveAsync(ct, trackInPipeline: true); },
            vm == null ? null : () => vm.OpenSettingsCommand.Execute(null)) { Owner = main, ShowActivated = activate };
        if (main != null)
        {
            _shared.WindowStartupLocation = WindowStartupLocation.Manual;
            _shared.Left = Math.Min(main.Left + main.ActualWidth - _shared.Width - 24, SystemParameters.WorkArea.Right - _shared.Width);
            _shared.Top = Math.Max(SystemParameters.WorkArea.Top, main.Top + 40);
        }
        _shared.Closed += (_, _) => _shared = null;
        _shared.Show();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // 데이터·규칙 도형은 보조 입력이므로 처리 단계보다 폭을 좁게 잡는다.
    private const double DataRuleWidth = 200;

    private static Rect Box(StageItem s)
    {
        double w = s.Kind == NodeKind.Step ? ColW[s.Col] : Math.Min(ColW[s.Col], DataRuleWidth);
        return new(ColX[s.Col] - w / 2, RowTop + s.Row * RowStep, w, NodeH);
    }

    /// <summary>
    /// 칸 하나에 그리는 것: 테두리(점선이 되므로 Rectangle) · 바탕 · 제목 · 상태 알약 · 설명 · 진행 문구,
    /// 그리고 주 계산이 아닌 계산이 이 칸에 있을 때의 바깥 고리(Ring)와 위쪽 알약 줄(Markers).
    /// 데이터·규칙은 각 도형 안에 이름을 넣고 모든 칸은 키보드로 선택할 수 있다.
    /// </summary>
    private sealed record Node(StageItem Item, Rectangle Frame, Button Body, TextBlock Title, Border Chip, TextBlock ChipText, TextBlock Detail,
        Rectangle Ring, StackPanel Markers, Rectangle Bar, Path? Contour);

    // 단계는 사각형, 데이터는 원통, 규칙은 접힌 메모. 이름·설명을 기호 안에 넣는다.
    private static readonly Geometry DataContour = Geometry.Parse("M1,9 A99,8 0 0 1 199,9 L199,63 A99,8 0 0 1 1,63 Z M1,9 A99,8 0 0 0 199,9");
    private static readonly Geometry RuleContour = Geometry.Parse("M1,1 L183,1 L199,17 L199,71 L1,71 Z M183,1 L183,17 L199,17");

    private void DrawNode(StageItem s)
    {
        var r = Box(s);
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        var chipText = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold };
        var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = chipText };
        var sub = new TextBlock { Text = s.Sub, FontSize = 11, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        var detail = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        Path? contour = null;
        StackPanel content;
        if (s.Kind == NodeKind.Step)
        {
            var head = new DockPanel();
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
            head.Children.Add(title);
            content = new StackPanel { Children = { head, sub, detail } };
        }
        else
        {
            contour = new Path
            {
                Data = s.Kind == NodeKind.Data ? DataContour : RuleContour,
                Width = r.Width, Height = r.Height, Stretch = Stretch.Fill, StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
            };
            title.FontSize = 12.5;
            sub.FontSize = detail.FontSize = 10.5;
            chip.VerticalAlignment = VerticalAlignment.Center;
            sub.HorizontalAlignment = detail.HorizontalAlignment = HorizontalAlignment.Center;
            var name = new DockPanel();
            DockPanel.SetDock(chip, Dock.Right);
            title.TextAlignment = TextAlignment.Center;
            name.Children.Add(chip);
            name.Children.Add(title);
            content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub, detail } };
        }
        var body = new Button
        {
            Width = r.Width, Height = r.Height, Style = (Style)Resources["NodeButton"], Tag = s.Key,
            Padding = s.Kind switch
            {
                NodeKind.Step => new Thickness(13, 8, 10, 6),
                NodeKind.Data => new Thickness(10, 18, 10, 7),
                _ => new Thickness(12, 10, 18, 8),
            },
            Content = content, ToolTip = s.Sub,
        };
        AutomationProperties.SetName(body, s.Label);
        AutomationProperties.SetHelpText(body, L.T("Enter 키로 상세 정보 보기"));
        System.Windows.Input.KeyboardNavigation.SetTabIndex(body, s.Row * 4 + s.Col + 10);
        body.GotKeyboardFocus += (_, _) => SelectNode(s.Key);
        body.Click += (_, _) => { SelectNode(s.Key); NodeDetailText.Focus(); };
        var frame = new Rectangle { Width = r.Width, Height = r.Height, RadiusX = 10, RadiusY = 10, IsHitTestVisible = false };
        var ring = new Rectangle { Width = r.Width + 12, Height = r.Height + 12, RadiusX = 14, RadiusY = 14, StrokeThickness = 2.5, StrokeDashArray = [3, 2], IsHitTestVisible = false };
        Canvas.SetLeft(ring, r.Left - 6);
        Canvas.SetTop(ring, r.Top - 6);
        var markers = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        Canvas.SetLeft(markers, r.Left + 8);
        Canvas.SetTop(markers, r.Top - 15);
        foreach (UIElement el in new UIElement[] { body, frame })
        {
            Canvas.SetLeft(el, r.Left);
            Canvas.SetTop(el, r.Top);
        }
        Chart.Children.Add(ring);
        if (contour != null)
        {
            Canvas.SetLeft(contour, r.Left);
            Canvas.SetTop(contour, r.Top);
            Chart.Children.Add(contour);
        }
        Chart.Children.Add(body);
        Chart.Children.Add(frame);
        Chart.Children.Add(markers);
        Panel.SetZIndex(markers, 10);
        // 진행 막대: 진행 문구의 n/m (미리듣기 32/83 · 디스코그래피 2500/2523 …) — 칸 아래쪽 안
        var bar = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(bar, r.Left + 3);
        Canvas.SetTop(bar, r.Bottom - 6);
        Chart.Children.Add(bar);
        _nodes[s.Key] = new Node(s, frame, body, title, chip, chipText, detail, ring, markers, bar, contour);
        // 칸이 일부만 갱신된 상태를 따라가지 않도록 모니터의 Changed에서 한 번에 그린다.
    }

    private static readonly Brush DoneDeep = Frozen("#15261C");
    private static readonly Brush Quiet = Frozen("#E3C25B");
    private static readonly Brush Failed = Frozen("#EC8088");
    private static readonly Brush FailedDeep = Frozen("#361F26");

    /// <summary>진행 문구의 "32/83"·"2,500/2,523" → 0~1</summary>
    private static double? Fraction(string text) =>
        System.Text.RegularExpressions.Regex.Match(text, @"([\d,]+)\s*/\s*([\d,]+)") is { Success: true } m
        && double.TryParse(m.Groups[1].Value.Replace(",", ""), out var a) && double.TryParse(m.Groups[2].Value.Replace(",", ""), out var b) && b > 0
            ? Math.Clamp(a / b, 0, 1) : null;

    /// <summary>소리 서비스 칸 (맨 오른쪽 줄 6~8행)</summary>
    private void DrawQueue()
    {
        double x = ColX[3] - ColW[3] / 2, y = RowTop + 6 * RowStep, h = 2 * RowStep + NodeH;
        var box = new Border
        {
            Width = ColW[3], Height = h, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Padding = new Thickness(12, 8, 10, 8),
            BorderBrush = Res("Line3"), Background = Res("Surface2"),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = L.T("소리 서비스"), FontSize = 14, FontWeight = FontWeights.Bold },
                    new TextBlock { Text = L.T("요청을 한 번에 하나씩 처리 · 미리듣기·분석이 여기서 줄을 서요"), FontSize = 11, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 6) },
                    _queuePanel,
                },
            },
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        Chart.Children.Add(box);
    }

    /// <summary>요청 이름 (SoundService 의 cmd)</summary>
    private static string JobName(string cmd) => cmd switch
    {
        "score_albums" => L.T("미리듣기 소리"),
        "update" => L.T("내 곡 분석"),
        "favorites" => L.T("좋아요·별로 소리"),
        "track_near" => L.T("수록곡 비슷한 내 곡"),
        "album_pairs" => L.T("카드 비슷한 내 곡"),
        "similar_albums" or "dislike_matches" => L.T("닮은 앨범 확인"),
        "genre_neighbors" => L.T("장르 투표 (닮은 앨범)"),
        "status" => L.T("상태 확인"),
        _ => cmd,
    };

    private void PaintQueue()
    {
        _queuePanel.Children.Clear();
        var jobs = PipelineMonitor.Instance.SoundQueue?.Invoke() ?? [];
        if (jobs.Count == 0)
        {
            _queuePanel.Children.Add(new TextBlock { Text = L.T("쉬는 중"), FontSize = 12, Foreground = Res("Muted") });
            return;
        }
        foreach (var j in jobs.Take(6))
        {
            var (mark, color) = j.State switch
            {
                SoundJobState.Running => ("▶ ", Res("AccentText")),
                SoundJobState.Orphan => ("⏹ ", Quiet),
                _ => ("… ", Res("Muted")),
            };
            var state = j.State switch
            {
                SoundJobState.Running => "",
                SoundJobState.Orphan => L.T("멈춘 요청 마무리") + " · ",
                _ => L.T("대기") + " · ",
            };
            _queuePanel.Children.Add(new TextBlock
            {
                Text = mark + state + JobName(j.Cmd) + (j.Message.Length > 0 && j.State != SoundJobState.Waiting ? " · " + j.Message : ""),
                FontSize = 11.5, Foreground = color, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1),
                ToolTip = j.Message,
            });
        }
        if (jobs.Count > 6) _queuePanel.Children.Add(new TextBlock { Text = L.F("그 밖 {0}개", jobs.Count - 6), FontSize = 11, Foreground = Res("Muted") });
    }
    // 재사용: 완료(초록)와 다른 청록 — 이번 계산이 한 일이 아니라 앞 계산이 만든 것
    private static readonly Brush Reuse = Frozen("#5FA8A3");
    private static readonly Brush ReuseText = Frozen("#9CCFCB");
    private static readonly Brush ReuseDeep = Frozen("#132322");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 칸 모양은 주 계산(가장 나중에 시작한 추천 계산) 기준: 완료 = 초록 + ✓ / 진행 중 = 그 계산 색(보통 주황 · 정밀 보라 …) + ▶ /
    /// 대기 = 점선 / 이번엔 안 거침 = 흐린 점선. 내 곡 분석은 '내 곡 소리 기준' 칸에 파랑.
    /// 다른 계산이 지금 있는 칸에는 그 계산 색의 점선 고리와 위쪽 알약("보통 계산 12.3s")을 붙인다.
    /// </summary>
    private void Paint()
    {
        if (_closed) return;
        var m = PipelineMonitor.Instance;
        var primary = m.Primary;
        var analysis = m.Runs.FirstOrDefault(r => r.Kind == RunKind.Analysis && !r.Ended);
        var archive = m.Runs.FirstOrDefault(r => r.Kind == RunKind.Archive && !r.Ended);
        bool idle = primary == null && m.Runs.Count == 0;  // 이번 실행에서 아직 계산 없음 → '대기' 알약을 붙이지 않는다
        // 소리 서비스만 쓰는 옆 작업(좋아요 소리·수록곡·닮은 앨범)은 칸 고리 대신 '소리 서비스' 칸에만 보인다
        var others = m.Runs.Where(r => !r.Ended && r != primary && r.Kind is not (RunKind.Analysis or RunKind.Taste or RunKind.Tracks or RunKind.Similar)
            && r.Current is { } c && c != RecStage.Done).ToList();
        foreach (var n in _nodes.Values)
        {
            var st = n.Item.State;
            bool isAnalysis = n.Item.Stage == RecStage.Analyze;
            // 진행 중 색: 분석 칸은 분석 색, 나머지는 주 계산 색
            var run = isAnalysis ? analysis : n.Item.Key == "archive" ? archive : primary;
            Brush line = run?.Brush ?? Res("Accent"), text = run?.TextBrush ?? Res("AccentText"), deep = run?.Deep ?? Res("AccentDeep");
            n.Frame.Stroke = st switch
            {
                StageState.Current => line,
                StageState.Done => Res("Ok"),
                StageState.Reused => Reuse,
                StageState.Failed => Failed,
                _ => Res("Line3"),
            };
            n.Frame.StrokeThickness = st == StageState.Current ? 3 : 2;
            n.Frame.StrokeDashArray = st is StageState.Todo or StageState.Skipped ? [4, 3] : null;
            n.Body.Background = st switch
            {
                StageState.Current => deep,
                StageState.Done => DoneDeep,
                StageState.Reused => ReuseDeep,
                StageState.Fixed => Res("Surface2"),
                _ => Brushes.Transparent,
            };
            n.Body.Opacity = 1;
            n.Frame.Opacity = st == StageState.Skipped ? 0.35 : 1;
            n.Title.Foreground = st switch
            {
                StageState.Current => text,
                StageState.Done => Res("Ok"),
                StageState.Reused => ReuseText,
                StageState.Failed => Failed,
                StageState.Cancelled => Res("Muted"),
                StageState.Todo or StageState.Skipped => Res("Muted"),
                _ => Res("Text"),
            };
            n.Title.Text = (n.Item.Kind == NodeKind.Step
                ? st switch { StageState.Done => "✓ ", StageState.Current => "▶ ", StageState.Reused => "↺ ", _ => "" } : "") + n.Item.Label;
            if (n.Contour is { } contour)
            {
                // 상태 색은 유지하되 단계의 사각형 대신 데이터·규칙의 외곽선을 칠한다.
                n.Frame.Visibility = Visibility.Collapsed;
                n.Body.Background = Brushes.Transparent;
                contour.Stroke = st switch { StageState.Current => line, StageState.Done => Res("Ok"), StageState.Reused => Reuse, StageState.Failed => Failed, _ => Res("Muted") };
                contour.Fill = st switch { StageState.Current => deep, StageState.Done => DoneDeep, StageState.Reused => ReuseDeep, StageState.Failed => FailedDeep, _ => Res("Surface2") };
                contour.StrokeThickness = st == StageState.Current ? 2 : 1.4;
            }
            n.Detail.Visibility = st == StageState.Current || (n.Item.Key == "archive" && n.Item.Detail.Length > 0)
                ? Visibility.Visible : Visibility.Collapsed;
            // 진행 상태: 오래 조용하면(멈춘 건지 느린 건지) 문구 끝에 '소식 없음 n초'를 붙이고 노랗게
            var quiet = st == StageState.Current && n.Item.LastNews != default ? (DateTime.Now - n.Item.LastNews).TotalSeconds : 0;
            var detail = n.Item.Key == "archive" ? n.Item.Detail.Split('\n')[0] : n.Item.Detail;
            n.Detail.Text = detail + (quiet >= 15 ? (detail.Length > 0 ? " · " : "") + L.F("{0:0}초째 소식 없음", quiet) : "");
            n.Body.ToolTip = n.Item.Label + "\n" + n.Item.Sub + (n.Item.Time.Length > 0 ? "\n" + n.Item.Time : "")
                + (n.Item.Detail.Length > 0 ? "\n" + n.Item.Detail : "");
            n.Detail.Foreground = st == StageState.Failed ? Failed : st == StageState.Cancelled ? Res("Muted") : quiet >= 45 ? Quiet : text;
            var frac = st == StageState.Current ? Fraction(n.Item.Detail) : null;
            n.Bar.Visibility = frac != null ? Visibility.Visible : Visibility.Collapsed;
            if (frac is { } f) { n.Bar.Width = Math.Max(2, (n.Body.Width - 6) * f); n.Bar.Fill = line; }
            // 데이터 칸은 제목·상태를 우선한다. 소요 시간은 작업 목록과 툴팁에서 본다.
            var time = n.Item.Kind == NodeKind.Step ? n.Item.Time : "";
            (string text, Brush bg, Brush fg) chip = st switch
            {
                StageState.Current => ((isAnalysis ? L.T("분석 중") : L.T("진행 중")) + (time.Length > 0 ? " " + time : ""), line, Res("OnAccent")),
                StageState.Done => (L.T("완료") + (time.Length > 0 ? " " + time : ""), Res("Ok"), Res("OnAccent")),
                StageState.Todo when idle => ("", Brushes.Transparent, Brushes.Transparent),
                StageState.Todo => (L.T("대기"), Res("Line2"), Res("Muted")),
                StageState.Reused => (L.T("재사용") + (time.Length > 0 ? " · " + time : ""), Reuse, Res("OnAccent")),
                StageState.Skipped => (L.T("안 함"), Brushes.Transparent, Res("Muted")),
                StageState.Failed => (L.T("실패"), Failed, Res("OnAccent")),
                StageState.Cancelled => (L.T("취소됨"), Res("Line2"), Res("Text")),
                _ => ("", Brushes.Transparent, Brushes.Transparent),
            };
            if (st == StageState.Current && n.Item.Detail == RecipeSong.Core.Sound.SoundService.WaitingText)
            {
                // 소리 서비스가 앞 요청(다른 계산·멈춘 계산·옆 작업)을 처리하는 동안 차례를 기다린다
                chip = (L.T("차례 기다림"), Res("Line3"), Res("Text"));
                n.Frame.StrokeDashArray = [5, 3];
            }
            if (analysis != null && n.Item.Stage == RecStage.Listen && st == StageState.Current)
            {
                // 소리 서비스가 분석을 처리하는 동안 미리듣기는 차례를 기다린다: 분석 색 알약
                chip = (L.T("분석 기다리는 중"), analysis.Brush, Res("OnAccent"));
                n.Frame.StrokeDashArray = [5, 3];
            }
            n.ChipText.Text = chip.text;
            n.Chip.Background = chip.bg;
            n.ChipText.Foreground = chip.fg;
            n.Chip.Visibility = chip.text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 다른 계산이 이 칸에 있다: 바깥 고리 + 알약
            var here = n.Item.Stage is { } stage ? others.Where(r => r.Current == stage).ToList() : [];
            bool selected = _selectedKey == n.Item.Key;
            n.Ring.Visibility = selected || here.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            n.Ring.StrokeDashArray = selected ? null : [3, 2];
            if (selected) n.Ring.Stroke = Res("Accent");
            else if (here.Count > 0) n.Ring.Stroke = here[0].Brush;
            n.Markers.Children.Clear();
            foreach (var r in here)
                n.Markers.Children.Add(new Border
                {
                    Background = r.Brush, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = r.KindLabel + " " + r.StageTime, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("OnAccent") },
                });
        }
        PaintQueue();
        RefreshDetailsAndRecovery();
        AnnounceStatus();
        FollowCurrent();
        foreach (var (e, parts) in _edges)
        {
            var fromNode = _nodes[e.From].Item;
            var toNode = _nodes[e.To].Item;
            bool analysisEdge = toNode.Stage == RecStage.Analyze || fromNode.Stage == RecStage.Analyze;
            var brush = analysisEdge && analysis != null ? analysis.Brush
                : toNode.State == StageState.Current ? primary?.Brush ?? Res("Accent")
                : toNode.State == StageState.Done && fromNode.State is StageState.Done or StageState.Fixed ? Res("Ok")
                : toNode.State is StageState.Done or StageState.Reused && fromNode.State == StageState.Reused ? Reuse
                : Res("Line3");
            foreach (var p in parts)
            {
                p.Stroke = brush;
                p.Opacity = toNode.State == StageState.Skipped || fromNode.State == StageState.Skipped ? 0.35 : 1;
            }
        }
    }

    private void RefreshDetailsAndRecovery()
    {
        var m = PipelineMonitor.Instance;
        var selected = _selectedKey == null ? null : m.Stages.FirstOrDefault(s => s.Key == _selectedKey);
        NodeDetailPanel.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        if (selected != null)
        {
            NodeDetailTitle.Text = selected.Label + " · " + selected.StateLabel;
            var text = selected.Sub + (selected.Detail.Length > 0 ? "\n" + selected.Detail : "");
            if (NodeDetailText.Text != text && (!NodeDetailText.IsKeyboardFocusWithin || NodeDetailText.SelectionLength == 0))
                NodeDetailText.Text = text;
        }
        var archive = m.Stages.First(s => s.Key == "archive");
        if (_recoveryError != null && archive.State == StageState.Done && archive.LastNews != _recoveryErrorScan)
            _recoveryError = null;
        bool recovery = _recoveryBusy || _recoveryError != null || archive.State is StageState.Failed or StageState.Cancelled;
        RecoveryPanel.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        RecoveryMessage.Text = _recoveryBusy ? L.T("ZIP 보유 정보를 다시 읽는 중…")
            : _recoveryError ?? L.T("ZIP 읽기를 완료하지 못했어요. 다시 읽거나 설정에서 경로를 확인하세요.");
        RetryArchiveButton.IsEnabled = !_recoveryBusy && _retryArchive != null;
        ArchiveSettingsButton.IsEnabled = !_recoveryBusy && _openSettings != null;
        RetryArchiveButton.Opacity = RetryArchiveButton.IsEnabled ? 1 : 0.45;
        ArchiveSettingsButton.Opacity = ArchiveSettingsButton.IsEnabled ? 1 : 0.45;
    }

    private void AnnounceStatus(bool force = false)
    {
        var m = PipelineMonitor.Instance;
        var archive = m.Stages.First(s => s.Key == "archive");
        var result = archive.State is StageState.Failed or StageState.Cancelled ? archive.Detail : "";
        var key = m.Status + "|" + _recoveryBusy + "|" + _recoveryError + "|" + result + "|" + string.Join("|", m.Runs.Select(r => $"{r.Id}:{r.Current}:{r.Ended}"));
        if (!force && key == _announcementKey) return;
        _announcementKey = key;
        var text = m.Status + " " + string.Join(" · ", m.Runs.Where(r => !r.Ended).Select(r => r.KindLabel + ": " + r.StageLabel));
        if (_recoveryError != null) text += " " + _recoveryError;
        else if (result.Length > 0) text += " " + result;
        if (_recoveryBusy) text += " " + L.T("ZIP 보유 정보를 다시 읽는 중…");
        StatusSummary.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        AutomationProperties.SetName(StatusSummary, text.Trim());
        if (IsLoaded)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusSummary) ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusSummary);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>
    /// 화살표 길: 같은 줄이면 마주 보는 옆면끼리, 같은 칸 줄이면 아래 → 위,
    /// 가운데 → 옆 칸(아래 줄)은 옆으로 나가 내려가고, 옆 칸 → 가운데(아래 줄)는 내려가 옆으로 들어간다.
    /// </summary>
    private void DrawEdge(StageEdge e, StageItem from, StageItem to)
    {
        Rect a = Box(from), b = Box(to);
        const double gap = 6;
        var pts = new List<Point>();
        // 추가된 입력 칸을 지나가지 않도록 소리 기준·ZIP→ID·저장 결과의 선은 바깥 여백으로 돌린다.
        if (from.Key == "base" && to.Key == "listen")
        {
            pts.Add(new(a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(6, a.Top + NodeH / 2));
            pts.Add(new(6, b.Top + NodeH / 2));
            pts.Add(new(b.Left - gap, b.Top + NodeH / 2));
        }
        else if (from.Key == "archive" && to.Key == "identity")
        {
            pts.Add(new(a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(23, a.Top + NodeH / 2));
            pts.Add(new(23, b.Top + NodeH / 2));
            pts.Add(new(b.Left - gap, b.Top + NodeH / 2));
        }
        else if (from.Key == "lib" && to.Key == "identity")
        {
            const double gutter = 267;
            pts.Add(new(a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(gutter, a.Top + NodeH / 2));
            pts.Add(new(gutter, b.Top - 16));
            pts.Add(new(ColX[to.Col], b.Top - 16));
            pts.Add(new(ColX[to.Col], b.Top - gap));
        }
        else if (from.Key == "saved" && to.Key == "screen")
        {
            const double gutter = 570;
            pts.Add(new(a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(gutter, a.Top + NodeH / 2));
            pts.Add(new(gutter, b.Top + NodeH / 2));
            pts.Add(new(b.Right + gap, b.Top + NodeH / 2));
        }
        else if (from.Row == to.Row)
        {
            bool right = to.Col > from.Col;
            pts.Add(new(right ? a.Right + gap : a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(right ? b.Left - gap : b.Right + gap, b.Top + NodeH / 2));
        }
        else if (from.Col == to.Col)
        {
            pts.Add(new(ColX[from.Col], a.Bottom + gap));
            pts.Add(new(ColX[to.Col], b.Top - gap));
        }
        else if (from.Col == 1)
        {
            bool right = to.Col > from.Col;
            pts.Add(new(right ? a.Right + gap : a.Left - gap, a.Top + NodeH / 2));
            pts.Add(new(ColX[to.Col], a.Top + NodeH / 2));
            pts.Add(new(ColX[to.Col], b.Top - gap));
        }
        else
        {
            bool right = to.Col > from.Col;
            pts.Add(new(ColX[from.Col], a.Bottom + gap));
            pts.Add(new(ColX[from.Col], b.Top + NodeH / 2));
            pts.Add(new(right ? b.Left - gap : b.Right + gap, b.Top + NodeH / 2));
        }
        var line = new Polyline { Points = [.. pts], StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        if (e.Dashed) line.StrokeDashArray = [3, 3];
        // 열린 화살촉 (흐름도와 같이 채우지 않는다)
        Point p1 = pts[^2], p2 = pts[^1];
        double ang = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X), L = 9;
        var head = new Polyline
        {
            Points = [new(p2.X + L * Math.Cos(ang + Math.PI * 0.8), p2.Y + L * Math.Sin(ang + Math.PI * 0.8)), p2,
                      new(p2.X + L * Math.Cos(ang - Math.PI * 0.8), p2.Y + L * Math.Sin(ang - Math.PI * 0.8))],
            StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        Chart.Children.Add(line);
        Chart.Children.Add(head);
        _edges.Add((e, [line, head]));
        if (e.Label.Length == 0) return;
        // 라벨: 가장 긴 구간 옆 (세로면 오른쪽, 가로면 위)
        int bi = 0;
        for (int i = 1; i < pts.Count - 1; i++)
            if ((pts[i + 1] - pts[i]).Length > (pts[bi + 1] - pts[bi]).Length) bi = i;
        var mid = new Point((pts[bi].X + pts[bi + 1].X) / 2, (pts[bi].Y + pts[bi + 1].Y) / 2);
        bool vertical = Math.Abs(pts[bi + 1].Y - pts[bi].Y) > Math.Abs(pts[bi + 1].X - pts[bi].X);
        var label = new TextBlock { Text = e.Label, FontSize = 10.5, Foreground = Res("Muted") };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelX = from.Key == "base" && to.Key == "listen" ? 40 : vertical ? mid.X + 10 : mid.X - label.DesiredSize.Width / 2;
        Canvas.SetLeft(label, labelX);
        Canvas.SetTop(label, vertical ? mid.Y - label.DesiredSize.Height / 2 : mid.Y - label.DesiredSize.Height - 2);
        Chart.Children.Add(label);
    }
}
