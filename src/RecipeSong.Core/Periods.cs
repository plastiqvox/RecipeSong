using System.Globalization;

namespace RecipeSong.Core;

public static class Periods
{
    /// <summary>양 끝 날짜가 속한 주 전체. 끝은 마지막 주의 다음 월요일(미포함)이다.</summary>
    public static Period WeekRange(DateTime start, DateTime end)
    {
        var first = Create(PeriodKind.Week, start).Start;
        var last = Create(PeriodKind.Week, end).End;
        if (first >= last) throw new ArgumentException(L.T("종료 주는 시작 주보다 앞설 수 없어요."));
        return new(PeriodKind.Range, first, last,
            string.Create(CultureInfo.InvariantCulture, $"range:{first:yyyy-MM-dd}:{last:yyyy-MM-dd}"));
    }
    /// <summary>월·연·직접 선택 범위와 겹치는 주 전체. 앞뒤 경계는 자르지 않으며 아직 시작하지 않은 주는 제외한다.</summary>
    public static List<Period> IntersectingWeeks(Period period, DateTime today)
    {
        if (period.Kind == PeriodKind.Week) return [];
        var weeks = new List<Period>();
        var first = Create(PeriodKind.Week, period.Start);
        for (var start = first.Start; start < period.End && start <= today.Date; start = start.AddDays(7))
            weeks.Add(Create(PeriodKind.Week, start));
        return weeks;
    }

    public static Period Create(PeriodKind kind, DateTime date)
    {
        date = date.Date;
        switch (kind)
        {
            case PeriodKind.Range: throw new ArgumentException(L.T("직접 선택 기간은 시작 주와 종료 주가 필요해요."));
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
        throw new FormatException(L.F("기간 형식을 알 수 없습니다: '{0}' (예: 2026, 2026-10, 2026-W43, 2026-10-21)", text));
    }
}
