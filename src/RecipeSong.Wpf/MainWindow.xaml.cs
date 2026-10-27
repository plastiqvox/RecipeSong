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
    }

    /// <summary>
    /// 카드 폭: 본문에 들어가는 칸 수(최소 380)를 정하고 남는 폭을 나눠 준다. 너무 넓으면 520 에서 멈춘다.
    /// 본문 좌우 여백 72, 카드 오른쪽 간격 16.
    /// </summary>
    private void Body_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0) return;
        var w = e.ViewportWidth - 72;
        int cols = Math.Max(1, (int)(w / (380 + 16)));
        var width = Math.Clamp(Math.Floor(w / cols) - 16, 300, 520);
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
