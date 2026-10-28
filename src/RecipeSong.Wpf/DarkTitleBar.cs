using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RecipeSong.Wpf;

/// <summary>
/// 창 제목 표시줄을 어둡게 (Windows 기본은 밝은 색이라 어두운 본문 위에 흰 띠가 생긴다).
/// Windows 10 20H1+ 는 어두운 모드, Windows 11 은 제목 표시줄 색을 본문 배경(Bg)과 맞춘다. 지원하지 않으면 그대로 둔다.
/// 창 생성자에서 부른다 — 창 핸들을 먼저 만들어 두므로 보이기 전에 적용돼 흰 제목 표시줄이 깜박이지 않는다.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20, CaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
        // COLORREF = 0x00BBGGRR, Bg #0F1012
        int bg = 0x12 << 16 | 0x10 << 8 | 0x0F;
        DwmSetWindowAttribute(hwnd, CaptionColor, ref bg, sizeof(int));
    }
}
