using System.Globalization;

namespace Crate.Core;

public static class Periods
{
    public static Period Create(PeriodKind kind, DateTime date)
    {
        date = date.Date;
        switch (kind)
        {
            case PeriodKind.Year:
                var y = new DateTime(date.Year, 1, 1);
                return new Period(kind, y, y.AddYears(1), $"{date.Year}년");
            case PeriodKind.Month:
                var m = new DateTime(date.Year, date.Month, 1);
                return new Period(kind, m, m.AddMonths(1), $"{date.Year}년 {date.Month}월");
            default:
                var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
                int week = ISOWeek.GetWeekOfYear(date);
                int weekYear = ISOWeek.GetYear(date);
                return new Period(kind, monday, monday.AddDays(7),
                    $"{weekYear}년 {week}주차 ({monday:M.d} – {monday.AddDays(6):M.d})");
        }
    }

    /// <summary>"2026", "2026-10", "2026-W43", "2026-10-21"(+kind) 형식을 해석한다.</summary>
    public static Period Parse(string text, PeriodKind? kind = null)
    {
        text = text.Trim();
        if (text.Contains("-W", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split("-W", StringSplitOptions.None);
            var d = ISOWeek.ToDateTime(int.Parse(parts[0]), int.Parse(parts[1]), DayOfWeek.Monday);
            return Create(PeriodKind.Week, d);
        }
        if (text.Length == 4 && int.TryParse(text, out var year))
            return Create(PeriodKind.Year, new DateTime(year, 1, 1));
        if (DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ym))
            return Create(PeriodKind.Month, ym);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return Create(kind ?? PeriodKind.Week, day);
        throw new FormatException($"기간 형식을 알 수 없습니다: '{text}' (예: 2026, 2026-10, 2026-W43, 2026-10-21)");
    }
}
