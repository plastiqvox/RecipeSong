namespace RecipeSong.Core;

/// <summary>
/// 소리 모델별 곡 취향 기준선 (소리 서비스가 정한 값을 저장해 둔다). 저장된 추천의 곡 점수는 계산한 소리 모델의 척도라,
/// 모델을 바꾼 뒤에도 지난 결과는 그 모델의 기준선으로 판정해야 한다 — 기준선 하나만 두면 CLAP 점수를 EffNet 기준선으로 재게 된다.
/// </summary>
public static class TasteThresholds
{
    /// <summary>모델 이름을 기록하기 전에 저장한 추천의 소리 모델 (train.DEFAULT_EMB)</summary>
    public const string LegacyModel = "emb_larger_clap_general";

    private static readonly JsonCache<double> Store = new("fit-threshold", TimeSpan.FromDays(36500));

    public static string ModelOf(ReleaseRecommendation album) => album.Evidence?.SoundModel ?? LegacyModel;

    /// <summary>이 추천을 계산한 소리 모델의 지금 기준선 (서비스를 한 번도 띄우지 않았으면 null → 계산 당시 판정)</summary>
    public static double? For(ReleaseRecommendation album) => Store.TryGet(ModelOf(album), out var t) ? t : null;

    /// <summary>바뀌었으면 저장하고 true</summary>
    public static bool Set(string model, double threshold)
    {
        if (Store.TryGet(model, out var old) && Math.Abs(old - threshold) < 1e-6) return false;
        Store.Set(model, threshold);
        Store.Save();
        return true;
    }
}
