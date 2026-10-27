using System.Windows;

namespace RecipeSong.Wpf;

public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string body, string ok)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        Heading.Text = title;
        Body.Text = body;
        OkText.Text = ok;
    }

    /// <summary>ok: 확인 버튼에 쓸 동작 이름 (예: "비우기")</summary>
    public static bool Ask(Window? owner, string title, string body, string ok) =>
        new ConfirmWindow(title, body, ok) { Owner = owner }.ShowDialog() == true;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
