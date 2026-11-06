using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;
using RecipeSong.Wpf;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckSearchConfirmation(string output)
    {
        var week = SearchEstimate.Calculate(0, 1);
        if (SearchEstimate.Calculate(2, 1).Maximum <= week.Maximum
            || SearchEstimate.Calculate(0, 53).Maximum <= week.Maximum
            || SearchEstimate.Calculate(0, 0).Maximum >= week.Maximum
            || SearchEstimate.Calculate(0, 1, true).Maximum >= week.Maximum
            || SearchEstimate.Ai(5).Maximum != SearchEstimate.Ai(1).Maximum * 5)
            throw new Exception("Search estimates must reflect amount, pending weeks, cached weeks and analysis mode.");
        foreach (var lang in new[] { "ko", "en" })
        {
            L.Init(lang);
            var queued = SearchEstimate.Calculate(2, 53).Describe(161);
            if (!queued.Contains("161") || queued == SearchEstimate.Calculate(2, 53).Describe(0)
                || lang == "en" && queued.Any(c => c >= '\uac00' && c <= '\ud7a3'))
                throw new Exception("Queued work or translated duration is missing.");
            var body = "2026 · " + L.T("주별로 촘촘히 계산") + "\n"
                + L.F("선택 기간의 {0}주를 모두 다시 계산해요. 이미 분석한 소리는 재사용해요.", 53) + "\n\n" + queued;
            CheckSearchDialog(L.T("추천을 다시 계산할까요?"), body, L.T("계산 시작"), Path.Combine(output, lang + "-search-confirmation.png"));
            var aiBody = L.F("{0}을(를) 주 {1}번으로 나눠 AI로 찾을까요?\n한 번에 찾을 때보다 촘촘하지만 비용이 주 수만큼 들어요 (예상 약 ${2:0.00}).", "2026", 53, 7.95)
                + "\n\n" + SearchEstimate.Ai(53).Describe(161) + "\n\n" + L.T("저장된 AI 결과를 지우고 새 결과로 바꿔요.");
            CheckSearchDialog(L.T("AI 주마다 나눠 찾기"), aiBody, L.F("약 ${0:0.00} 쓰고 찾기", 7.95), Path.Combine(output, lang + "-ai-search-confirmation.png"));
        }
    }

    static void CheckSearchDialog(string title, string body, string ok, string path)
    {
        // 창 내용만 렌더링한다. 앱 시작·MainViewModel·실제 검색은 실행하지 않는다.
        var window = (ConfirmWindow)Activator.CreateInstance(typeof(ConfirmWindow), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [title, body, ok], null)!;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(440, double.PositiveInfinity));
        if (content.DesiredSize.Height > 680) throw new Exception("Search confirmation exceeds a compact screen.");
        if (!DescendantButtons(content).Any(b => b.IsCancel)) throw new Exception("Search confirmation needs a cancel action.");
        RenderWindow(window, path, 440, (int)Math.Ceiling(content.DesiredSize.Height));
    }

    static IEnumerable<Button> DescendantButtons(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in DescendantButtons(child)) yield return nested;
        }
    }
}
