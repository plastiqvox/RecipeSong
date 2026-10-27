using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>카드 우클릭 → 차단: 사유를 여러 개 고른다. 소리 때문인 사유가 있으면 학습. 레이블 차단에는 '이름만 같은 다른 아티스트'가 없다.</summary>
public partial class BlockWindow : Window
{
    private static readonly (BlockReason Reason, string Text)[] Sound =
    [
        (BlockReason.Taste, "전반적으로 취향이 아니에요"),
        (BlockReason.Vocal, "보컬·목소리가 안 맞아요"),
        (BlockReason.Genre, "장르·스타일이 안 맞아요"),
        (BlockReason.Production, "사운드·프로덕션이 별로예요"),
    ];

    private static readonly (BlockReason Reason, string Text)[] Others =
    [
        (BlockReason.SameName, "이름만 같은 다른 아티스트예요"),
        (BlockReason.Known, "이미 충분히 알아요"),
        (BlockReason.Mainstream, "너무 유명해서 디깅감이 아니에요"),
        (BlockReason.TooMany, "발매가 너무 잦아 목록을 채워요"),
        (BlockReason.Reissues, "재발매·컴필레이션 위주예요"),
        (BlockReason.Language, "가사·언어가 안 맞아요"),
        (BlockReason.Personal, "음악 외적인 이유예요"),
        (BlockReason.Other, "다른 이유"),
    ];

    private readonly List<(CheckBox Box, BlockReason Reason)> _boxes = [];

    public BlockReason Reasons { get; private set; }

    public BlockWindow(string name, string album, bool label)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Heading.Text = label ? L.F("레이블 차단 · {0}", name) : L.F("아티스트 차단 · {0}", name);
        Sub.Text = L.F("{0}에서 차단해요. 추천 설정에서 풀 수 있어요.", album);
        Add(SoundPanel, Sound);
        Add(OtherPanel, label ? Others.Where(o => o.Reason != BlockReason.SameName) : Others);
    }

    private void Add(Panel panel, IEnumerable<(BlockReason Reason, string Text)> items)
    {
        foreach (var (reason, text) in items)
        {
            var box = new CheckBox { Content = L.T(text), Foreground = (System.Windows.Media.Brush)FindResource("Text"), FontSize = 13,
                Margin = new Thickness(0, 0, 18, 8), MinWidth = 210 };
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
            panel.Children.Add(box);
            _boxes.Add((box, reason));
        }
    }

    private BlockReason Selected => _boxes.Where(b => b.Box.IsChecked == true).Aggregate((BlockReason)0, (r, b) => r | b.Reason);

    private void Refresh()
    {
        OkButton.IsEnabled = Selected != 0;
        SameNameHint.Visibility = Selected.HasFlag(BlockReason.SameName) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Reasons = Selected;
        DialogResult = true;
    }
}
