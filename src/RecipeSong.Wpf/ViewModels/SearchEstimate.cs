using RecipeSong.Core;

namespace RecipeSong.Wpf.ViewModels;

/// <summary>실측 이력이 없는 상태의 넓은 참고 범위. 서버·소리 작업 대기시간은 예측하지 않는다.</summary>
public sealed record SearchEstimate(TimeSpan Minimum, TimeSpan Maximum)
{
    public static SearchEstimate Calculate(int amount, int workUnits, bool hitsOnly = false)
    {
        if (workUnits <= 0) return new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        var scale = (Math.Clamp(amount, 0, 2) + 1) * (hitsOnly ? 0.6 : 1);
        return new(TimeSpan.FromMinutes(workUnits * scale), TimeSpan.FromMinutes(workUnits * scale * 5));
    }

    public static SearchEstimate Ai(int searches) =>
        new(TimeSpan.FromMinutes(searches * 2), TimeSpan.FromMinutes(searches * 8));

    public string Describe(int soundJobs)
    {
        var text = L.F("예상 소요시간: 약 {0}~{1}", Duration(Minimum, false), Duration(Maximum, true))
            + "\n" + L.T("대략적인 참고 범위예요. 분석 캐시·PC 성능·연결 상태에 따라 더 빨라지거나 오래 걸릴 수 있어요.");
        if (soundJobs > 0)
            text += "\n\n" + L.F("현재 소리 작업 {0}개가 처리 중이거나 대기 중이에요. 이 작업을 기다리는 시간은 위 범위에 포함하지 않았어요.", soundJobs);
        return text;
    }

    private static string Duration(TimeSpan time, bool upper)
    {
        int Round(double value, int step) => Math.Max(step, (int)(upper ? Math.Ceiling(value / step) : Math.Floor(value / step)) * step);
        if (time.TotalMinutes < 1) return L.F("{0}초", Round(time.TotalSeconds, 10));
        if (time.TotalMinutes < 60) return L.F("{0}분", Round(time.TotalMinutes, time.TotalMinutes < 10 ? 1 : 5));
        return L.F("{0}시간", Round(time.TotalHours, 1));
    }
}
