using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RecipeSong.Core;
using RecipeSong.Core.Ai;

namespace RecipeSong.Wpf;

/// <summary>AI 커넥터 설정 창 (Claude, GPT). 저장을 누를 때만 ai-connector.json에 쓴다.</summary>
public partial class ConnectorWindow : Window
{
    private static readonly int[] SearchOptions = [4, 8, 16];
    private readonly AiConnectorSettings _settings;
    private readonly AiUsageLedger _ledger;
    private bool _clearKey, _clearGptKey;

    public ConnectorWindow(AiConnectorSettings settings, AiUsageLedger ledger)
    {
        InitializeComponent();
        _settings = settings;
        _ledger = ledger;
        ShowBudget("Claude", settings.ClaudeBudget, ClaudeDailyBox, ClaudeWeeklyBox, ClaudeCreditBox, ClaudeUsageText);
        ShowBudget("GPT", settings.GptBudget, GptDailyBox, GptWeeklyBox, GptCreditBox, GptUsageText);
        BuildKeyDays(settings.ClaudeBudget, ClaudeKeyDaysGrid, ClaudeKeyExpiryText);
        BuildKeyDays(settings.GptBudget, GptKeyDaysGrid, GptKeyExpiryText);

        // ---- Claude ----
        EnabledBox.IsChecked = settings.Enabled;
        foreach (var (id, label) in AiConnectorSettings.Models)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "model", Tag = id,
                Content = label[..label.IndexOf(" (", StringComparison.Ordinal)].Replace("Claude ", ""), IsChecked = id == settings.Model };
            rb.Checked += (_, _) => ModelHint.Text = L.T(label);
            ModelGrid.Children.Add(rb);
        }
        ModelHint.Text = AiConnectorSettings.Models.FirstOrDefault(m => m.Id == settings.Model).Label is { } modelLabel ? L.T(modelLabel) : settings.Model;
        foreach (var n in SearchOptions)
            SearchGrid.Children.Add(new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "searches", Tag = n,
                Content = L.F("{0}회", n), IsChecked = n == settings.MaxSearches });
        if (!SearchOptions.Contains(settings.MaxSearches)) ((RadioButton)SearchGrid.Children[1]).IsChecked = true;

        // ---- GPT ----
        GptEnabledBox.IsChecked = settings.GptEnabled;
        GptModelBox.Text = settings.GptModel;

        UpdateKeyHints();
        // 브라우저에서 키를 복사하고 돌아오면 클립보드에서 가져온다 (sk-ant- → Claude, 그 밖의 sk- → GPT)
        Activated += (_, _) => TryPasteKey();
        KeyBox.PasswordChanged += (_, _) => UpdateKeyHints();
        GptKeyBox.PasswordChanged += (_, _) => UpdateKeyHints();
    }

    private void OpenKeys_Click(object sender, RoutedEventArgs e) => OpenUrl("https://console.anthropic.com/settings/keys");
    private void OpenBilling_Click(object sender, RoutedEventArgs e) => OpenUrl("https://console.anthropic.com/settings/billing");
    private void OpenGptKeys_Click(object sender, RoutedEventArgs e) => OpenUrl("https://platform.openai.com/api-keys");
    private void OpenGptBilling_Click(object sender, RoutedEventArgs e) => OpenUrl("https://platform.openai.com/settings/organization/billing");

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private string? _pastedFrom;

    private void TryPasteKey()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (System.Runtime.InteropServices.COMException) { return; }  // 다른 앱이 클립보드를 잡고 있을 때
        if (!text.StartsWith("sk-", StringComparison.Ordinal) || text.Contains(' ') || text.Length < 20 || text == _pastedFrom) return;
        _pastedFrom = text;
        if (text.StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            if (KeyBox.Password == text) return;
            KeyBox.Password = text;
            EnabledBox.IsChecked = true;
            _clearKey = false;
            KeyHint.Text = L.T("클립보드에서 키를 가져왔어요. 연결을 확인하는 중…");
            Test_Click(this, new RoutedEventArgs());
        }
        else
        {
            if (GptKeyBox.Password == text) return;
            GptKeyBox.Password = text;
            GptEnabledBox.IsChecked = true;
            _clearGptKey = false;
            GptKeyHint.Text = L.T("클립보드에서 키를 가져왔어요. 연결을 확인하는 중…");
            GptTest_Click(this, new RoutedEventArgs());
        }
    }

    private void UpdateKeyHints()
    {
        UpdateKeyHint(KeyBox, KeyHint, ClearKey, Guide, _clearKey, _settings.HasStoredKey, _settings.GetKey() != null,
            "sk-ant-", "Anthropic", "ANTHROPIC_API_KEY", _settings.KeySource);
        UpdateKeyHint(GptKeyBox, GptKeyHint, GptClearKey, GptGuide, _clearGptKey, _settings.HasStoredGptKey, _settings.GetGptKey() != null,
            "sk-", "OpenAI", "OPENAI_API_KEY", _settings.GptKeySource);
    }

    private static void UpdateKeyHint(PasswordBox box, TextBlock hint, Button clear, Border guide, bool clearing, bool stored, bool hasKey,
        string prefix, string vendor, string envName, string source)
    {
        guide.Visibility = !hasKey && box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (box.Password.Length > 0)
        {
            hint.Text = box.Password.StartsWith(prefix, StringComparison.Ordinal)
                ? L.T("새 키를 입력했어요. 저장하면 이 키를 써요.") : L.F("{0} 키는 보통 {1} 로 시작해요. 다시 확인해 주세요.", vendor, prefix);
            clear.Visibility = Visibility.Collapsed;
            return;
        }
        hint.Text = clearing ? L.T("저장하면 키를 지워요")
            : stored ? L.T("저장한 키가 있어요 (Windows 계정으로 암호화). 바꾸려면 새 키를 입력하세요.")
            : L.F("키를 입력하세요. 비워 두면 {0} 환경 변수를 써요. (지금: {1})", envName, L.T(source));
        clear.Visibility = stored && !clearing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        _clearKey = true;
        KeyBox.Clear();
        UpdateKeyHints();
    }

    private void GptClearKey_Click(object sender, RoutedEventArgs e)
    {
        _clearGptKey = true;
        GptKeyBox.Clear();
        UpdateKeyHints();
    }

    /// <summary>창에 입력한 값을 반영한 설정 (저장 전 테스트용 사본 또는 실제 저장)</summary>
    private void Apply(AiConnectorSettings s)
    {
        s.Enabled = EnabledBox.IsChecked == true;
        s.Model = ModelGrid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? s.Model;
        s.MaxSearches = SearchGrid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? s.MaxSearches;
        if (KeyBox.Password.Length > 0) s.SetKey(KeyBox.Password);
        else if (_clearKey) s.SetKey(null);

        ApplyBudget(s.ClaudeBudget, ClaudeDailyBox, ClaudeWeeklyBox, ClaudeCreditBox);
        ApplyBudget(s.GptBudget, GptDailyBox, GptWeeklyBox, GptCreditBox);
        s.ClaudeBudget.KeyDays = SelectedDays(ClaudeKeyDaysGrid);
        s.GptBudget.KeyDays = SelectedDays(GptKeyDaysGrid);

        s.GptEnabled = GptEnabledBox.IsChecked == true;
        if (GptModelBox.Text.Trim() is { Length: > 0 } model) s.GptModel = model;
        if (GptKeyBox.Password.Length > 0) s.SetGptKey(GptKeyBox.Password);
        else if (_clearGptKey) s.SetGptKey(null);
    }

    // ---- 키 유효기간 (콘솔에서 키를 만들 때 고른 기간) ----

    private static readonly int?[] KeyDayOptions = [null, 30, 60, 90];

    private void BuildKeyDays(AiBudget b, UniformGrid grid, TextBlock text)
    {
        void Show(int? days)
        {
            var exp = days is { } d && b.KeySavedAt is { } at ? at.Date.AddDays(d) : (DateTime?)null;
            text.Text = exp is { } e ? L.F("{0:yyyy.M.d} 만료 예정 (키 저장일 {1:M.d} 기준)", e, b.KeySavedAt)
                : days != null ? L.T("키를 저장하면 그날부터 계산해요") : L.T("만료 없음");
        }
        foreach (var days in KeyDayOptions)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), Height = 28, GroupName = grid.Name, Tag = days,
                Content = days is { } d ? L.F("{0}일", d) : L.T("없음"), IsChecked = days == b.KeyDays };
            rb.Checked += (_, _) => Show(days);
            grid.Children.Add(rb);
        }
        Show(b.KeyDays);
    }

    private static int? SelectedDays(UniformGrid grid) =>
        grid.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int?;

    // ---- 한도 / 충전액 ----

    private void ShowBudget(string provider, AiBudget b, TextBox daily, TextBox weekly, TextBox credit, TextBlock usage)
    {
        daily.Text = Format(b.Daily);
        weekly.Text = Format(b.Weekly);
        credit.Text = Format(b.Credit);
        var parts = new List<string>
        {
            L.F("RecipeSong에서 쓴 금액: 오늘 {0}", AiUsageLedger.Money(_ledger.Spent(AiUsageLedger.TodayStart, provider))),
            L.F("이번 주 {0}", AiUsageLedger.Money(_ledger.Spent(AiUsageLedger.WeekStart, provider))),
        };
        if (b.Remaining(_ledger, provider) is { } left)
            parts.Add(L.F("남은 크레딧 약 {0} ({1:yyyy.M.d} 충전 기준, {2:yyyy.M.d} 만료 예정)", AiUsageLedger.Money(Math.Max(0, left)), b.CreditSince, b.CreditExpires));
        usage.Text = string.Join(" · ", parts) + L.T(". 한도에 닿으면 그 커넥터는 다음 날·다음 주까지 쉬어요. 충전할 때마다 충전 후 잔액을 다시 넣어 주세요.");
    }

    private static string Format(double? v) => v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static double? Parse(string text)
    {
        var t = text.Trim().TrimStart('$').Replace(',', '.');
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    private static void ApplyBudget(AiBudget b, TextBox daily, TextBox weekly, TextBox credit)
    {
        b.Daily = Parse(daily.Text);
        b.Weekly = Parse(weekly.Text);
        var c = Parse(credit.Text);
        // 충전액을 새로 넣거나 바꾸면 지금부터 다시 센다
        if (c != b.Credit) b.CreditSince = c == null ? null : DateTime.Now;
        b.Credit = c;
    }

    private void Test_Click(object sender, RoutedEventArgs e) =>
        _ = RunTestAsync(s => new ClaudeScout(s), TestButton, TestResult, L.T("결제 페이지에서 충전해 주세요"));

    private void GptTest_Click(object sender, RoutedEventArgs e) =>
        _ = RunTestAsync(s => new GptScout(s), GptTestButton, GptTestResult, L.T("결제 페이지에서 충전해 주세요"));

    private async Task RunTestAsync(Func<AiConnectorSettings, IAiScout> make, Button button, TextBlock result, string chargeHint)
    {
        var copy = _settings.Clone();
        Apply(copy);
        button.IsEnabled = false;
        result.Foreground = (Brush)FindResource("Muted");
        result.Text = L.T("확인 중…");
        var error = await make(copy).TestAsync();
        button.IsEnabled = true;
        result.Foreground = (Brush)FindResource(error == null ? "Ok" : "AccentText");
        result.Text = error == null ? L.T("연결됐어요. 저장을 누르면 끝이에요.") : L.F("연결 실패: {0}", Explain(error, chargeHint));
    }

    // 흔한 실패를 알아듣기 쉽게
    private static string Explain(string error, string chargeHint) =>
        error.Contains("credit balance", StringComparison.OrdinalIgnoreCase) || error.Contains("quota", StringComparison.OrdinalIgnoreCase)
            ? L.F("크레딧이 없어요. {0}.", chargeHint)
        : error.Contains("401") || error.Contains("authentication", StringComparison.OrdinalIgnoreCase) || error.Contains("api key", StringComparison.OrdinalIgnoreCase)
            ? L.T("키가 맞지 않아요. 다시 복사해 주세요.")
        : error.Contains("404") || error.Contains("model", StringComparison.OrdinalIgnoreCase) && error.Contains("not", StringComparison.OrdinalIgnoreCase)
            ? L.T("이 키로는 고른 모델을 쓸 수 없어요. 다른 모델을 골라 보세요.")
        : error;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Apply(_settings);
        _settings.Save();
        DialogResult = true;
    }
}
