using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RecipeSong.Wpf.ViewModels;

namespace RecipeSong.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = _vm = vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        _vm.ScrollToRequested += item => Dispatcher.BeginInvoke(() => ScrollTo(item), System.Windows.Threading.DispatcherPriority.Loaded);
        _vm.ScrollTopRequested += () => listScroll.ScrollToTop();
        // 창이 닫히면 카드 폭이 다시 계산되니(레이아웃 두 번) 그 뒤에 본다
        _vm.RevealRequested += item => Dispatcher.BeginInvoke(() => Reveal(item), System.Windows.Threading.DispatcherPriority.ContextIdle);
        // 형식·지역 필터 팝업: 버튼이 오른쪽 끝에 있어서 오른쪽 끝을 맞춰 연다 (왼쪽 맞춤이면 창 밖으로 나갔다)
        filterPop.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6),
                PopupPrimaryAxis.Horizontal)];
        PreviewKeyDown += Shortcut_KeyDown;
    }

    /// <summary>
    /// 단축키: Space 재생/일시정지, ←→ 이전·다음 곡, 수록곡 창이 열려 있으면 ↑↓ 이전·다음 앨범, Esc 창 닫기.
    /// 글을 쓰는 칸·슬라이더에 포커스가 있으면 그쪽에 맡긴다. Space 는 마지막에 누른 버튼을 다시 누르지 않게 여기서 먹는다.
    /// </summary>
    private void Shortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var focused = Keyboard.FocusedElement;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or System.Windows.Controls.ComboBox) return;
        bool slider = focused is System.Windows.Controls.Slider;
        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePlay();
                break;
            case Key.Left or Key.Right when !slider:
                _vm.StepTrack(e.Key == Key.Right ? 1 : -1);
                break;
            case Key.Up or Key.Down when !slider && _vm.HasDetail:
                _vm.StepDetail(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Escape when _vm.HasDetail:
                _vm.CloseDetailCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 카드 폭: 본문에 들어가는 칸 수(최소 380)를 정하고 남는 폭을 나눠 준다. 여러 칸이면 520 에서 멈춘다.
    /// 한 칸뿐이면(수록곡 창을 열었거나 창이 좁을 때) 본문 폭을 채운다 — 520 에서 멈추면 오른쪽이 비어 어긋나 보였다.
    /// 본문 좌우 여백 72, 카드 오른쪽 간격 16.
    /// </summary>
    private void Body_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        listFade.Visibility = e.VerticalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (e.ViewportWidthChange == 0) return;
        var w = e.ViewportWidth - 72;
        int cols = Math.Max(1, (int)(w / (380 + 16)));
        var width = cols == 1 ? Math.Max(300, Math.Floor(w) - 16) : Math.Clamp(Math.Floor(w / cols) - 16, 300, 520);
        if (Resources["CardWidth"] is not double old || Math.Abs(old - width) >= 1) Resources["CardWidth"] = width;
    }

    /// <summary>그 카드가 화면 위쪽에 오도록 스크롤 (수록곡이 펼쳐진 채로)</summary>
    private void ScrollTo(ReleaseItem item)
    {
        var card = Find(this, e => e is System.Windows.Controls.Border { Tag: "card" } b && b.DataContext == item);
        if (card == null) return;
        var sv = FindAncestor<System.Windows.Controls.ScrollViewer>(card);
        if (sv == null) { card.BringIntoView(); return; }
        var top = card.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + top - 24);
    }

    /// <summary>그 카드가 다 보이면 그대로, 화면 밖이거나 걸쳐 있으면 위쪽으로 스크롤</summary>
    private void Reveal(ReleaseItem item)
    {
        var card = Find(this, e => e is System.Windows.Controls.Border { Tag: "card" } b && b.DataContext == item);
        if (card == null || FindAncestor<System.Windows.Controls.ScrollViewer>(card) is not { } sv) return;
        var top = card.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        if (top >= 0 && top + card.ActualHeight <= sv.ViewportHeight) return;
        ScrollTo(item);
    }

    private static FrameworkElement? Find(DependencyObject root, Func<FrameworkElement, bool> match)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && match(fe)) return fe;
            if (Find(child, match) is { } hit) return hit;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = System.Windows.Media.VisualTreeHelper.GetParent(d); p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    // ---- 카드 우클릭: 차단 (ContextMenu는 카드의 DataContext를 물려받는다) ----
    /// <summary>우클릭 메뉴: Tag = "artist" / "label" → 사유 고르는 창</summary>
    private void Block_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReleaseItem item, Tag: string tag }) _vm.Block(item, tag == "label");
    }

    /// <summary>수록곡 창의 ⋯ 버튼: 왼쪽 클릭으로도 메뉴를 버튼 아래에 연다</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } btn) return;
        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- 크레이트 칩 드래그로 순서 바꾸기 ----

    private const string CrateFormat = "Crate.CrateKey";
    private Point _dragStart;
    private FilterChip? _dragChip;

    private void Chip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragChip = (sender as FrameworkElement)?.DataContext as FilterChip;
    }

    private void Chip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragChip is not { CanMove: true } chip) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragChip = null;
        // 끌기 시작하면 버튼 클릭(크레이트 선택)은 일어나지 않는다
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(CrateFormat, chip.Key), DragDropEffects.Move);
        foreach (var c in _vm.CrateChips) c.DropSide = "";
    }

    private void Chip_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FilterChip { CanMove: true } target } el
            || e.Data.GetData(CrateFormat) is not string key || key == target.Key)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        var side = e.GetPosition(el).X > el.ActualWidth / 2 ? "right" : "left";
        foreach (var c in _vm.CrateChips) c.DropSide = c == target ? side : "";
    }

    private void Chip_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterChip chip }) chip.DropSide = "";
    }

    private void Chip_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        foreach (var c in _vm.CrateChips) c.DropSide = "";
        if (sender is not FrameworkElement { DataContext: FilterChip { CanMove: true } target } el
            || e.Data.GetData(CrateFormat) is not string key) return;
        _vm.MoveCrateTo(key, target.Key, after: e.GetPosition(el).X > el.ActualWidth / 2);
    }

    // 재생 위치 슬라이더: 끄는 동안에는 타이머가 값을 덮어쓰지 않게
    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _vm.Player.BeginSeek();

    // 수록곡 창을 열면 레시피 칩이 한 줄 가로 스크롤이 된다: 휠을 가로로
    private void Chips_Wheel(object sender, MouseWheelEventArgs e)
    {
        // 칩 줄이 한 줄일 때만 템플릿에 ScrollViewer 가 있다 (여러 줄일 때는 본문 스크롤로 넘긴다)
        if (Find((DependencyObject)sender, el => el is System.Windows.Controls.ScrollViewer) is not System.Windows.Controls.ScrollViewer sv
            || sv.ScrollableWidth <= 0) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // 수록곡 창의 스피커 아이콘: 휠 한 칸에 5%
    private void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        _vm.Player.Volume = Math.Clamp(_vm.Player.Volume + Math.Sign(e.Delta) * 0.05, 0, 1);
        e.Handled = true;
    }

    // 하단 바와 오른쪽 수록곡 창 두 곳에 있어서 보낸 슬라이더 값을 쓴다
    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e) => _vm.Player.EndSeek(((System.Windows.Controls.Slider)sender).Value);

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.Player.BeginSeek();
        _vm.Player.EndSeek(((System.Windows.Controls.Slider)sender).Value);
    }
}
