using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RecipeSong.Core;

namespace RecipeSong.Wpf;

public partial class PeriodRangeWindow : Window
{
    private bool _updating = true;
    private readonly DateTime _today;
    public Period? Result { get; private set; }
    private sealed record WeekChoice(Period Period) { public override string ToString() => Period.Display; }

    public PeriodRangeWindow(Period? previous = null, DateTime? today = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _today = (today ?? DateTime.Today).Date;
        var years = Enumerable.Range(1900, ISOWeek.GetYear(_today) - 1900 + 1).Reverse().ToArray();
        StartYear.ItemsSource = years;
        EndYear.ItemsSource = years;
        var range = previous is { Kind: PeriodKind.Range } p && p.Start.Year >= 1900 && p.End > p.Start && p.Start <= _today && p.End <= Periods.Create(PeriodKind.Week, _today).End
            ? p : Periods.WeekRange(_today.AddDays(-21), _today);
        SetRange(range);
    }

    private void SetRange(Period range)
    {
        _updating = true;
        StartYear.SelectedItem = ISOWeek.GetYear(range.Start);
        EndYear.SelectedItem = ISOWeek.GetYear(range.End.AddDays(-1));
        FillWeeks(StartYear, StartWeek, range.Start);
        FillWeeks(EndYear, EndWeek, range.End.AddDays(-1));
        _updating = false;
        UpdatePreview();
    }
    private void FillWeeks(ComboBox year, ComboBox weeks, DateTime? selected = null)
    {
        if (year.SelectedItem is not int y) return;
        weeks.ItemsSource = Enumerable.Range(1, ISOWeek.GetWeeksInYear(y))
            .Select(w => new WeekChoice(Periods.Create(PeriodKind.Week, ISOWeek.ToDateTime(y, w, DayOfWeek.Monday))))
            .Where(w => w.Period.Start <= _today).ToArray();
        weeks.SelectedItem = weeks.Items.Cast<WeekChoice>().FirstOrDefault(w => selected is { } day && w.Period.Contains(day));
        if (weeks.SelectedItem == null) weeks.SelectedIndex = 0;
    }
    private void Year_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || StartWeek == null) return;
        _updating = true;
        if (ReferenceEquals(sender, StartYear)) FillWeeks(StartYear, StartWeek);
        else FillWeeks(EndYear, EndWeek);
        _updating = false;
        UpdatePreview();
    }
    private void Week_Changed(object sender, SelectionChangedEventArgs e) { if (!_updating) UpdatePreview(); }
    private void UpdatePreview()
    {
        Result = null;
        if (StartWeek.SelectedItem is WeekChoice start && EndWeek.SelectedItem is WeekChoice end)
        {
            try { Result = Periods.WeekRange(start.Period.Start, end.Period.Start); }
            catch (ArgumentException ex) { Preview.Text = ex.Message; }
        }
        ApplyButton.IsEnabled = Result != null;
        if (Result is { } result) Preview.Text = result.Display;
    }
    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        int weeks = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        SetRange(Periods.WeekRange(_today.AddDays(-7 * (weeks - 1)), _today));
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (Result != null) DialogResult = true; }
}
