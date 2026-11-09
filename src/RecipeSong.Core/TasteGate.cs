namespace RecipeSong.Core;

public enum TasteDecision { Recommended, NeedsReview, BelowThreshold }

/// <summary>
/// 곡별 근거로 통과 여부를 정한다. 일반·AI·웹진·팔로우는 같은 기준을 쓴다.
/// 내 라이브러리 뮤지션의 신보는 기준을 건너뛴다: 이미 듣는 사람의 신보는 미리듣기 한두 곡 점수보다 '나왔다'는 사실이 중요하고,
/// 싱글은 한 곡으로 갈려 기준선 바로 아래에서 절반 넘게 숨겨졌다. 앨범 별로·뮤지션 차단은 화면에서 따로 거른다.
/// threshold: 지금 기준선. 주면 저장된 곡 점수를 이 값으로 다시 판정한다 — 기준선은 라이브러리·피드백이 바뀔 때마다 움직이는데,
/// 저장된 통과 표시는 계산 당시 값이라 같은 점수가 주마다 다르게 판정됐다. 없으면 계산 당시 판정을 쓴다.
/// </summary>
public static class TasteGate
{
    public const double SingleMargin = 0.005;

    public static TasteDecision Assess(ReleaseRecommendation album, Func<long, bool>? dislikedTrack = null, double? threshold = null)
    {
        if (album.FromPlaylist == TasteProfile.LibraryTag) return TasteDecision.Recommended;
        if (album.Taste is not { } taste || !double.IsFinite(taste) || album.Evidence?.Tracks is not { Count: > 0 } tracks)
            return TasteDecision.NeedsReview;
        var scored = Scored(tracks);
        bool single = IsSingle(album);
        int total = album.Evidence.TotalTracks is > 0 ? album.Evidence.TotalTracks.Value : int.MaxValue;
        if (scored.Length < (single ? 1 : Math.Min(3, total)) || album.AnalyzedTracks != scored.Length || scored.Length > total)
            return TasteDecision.NeedsReview;
        return FitCount(album, threshold, dislikedTrack) >= (single ? 1 : Math.Min(2, total)) ? TasteDecision.Recommended : TasteDecision.BelowThreshold;
    }

    /// <summary>기준선을 넘은 곡 수 (기준 미달 카드 문구에도 쓴다)</summary>
    public static int FitCount(ReleaseRecommendation album, double? threshold = null, Func<long, bool>? dislikedTrack = null)
    {
        if (album.Evidence?.Tracks is not { } tracks) return 0;
        // 싱글은 한 곡으로 갈려 기준선 0.003 아래(오차 수준)에서도 빠졌다 → 기준선 아래 SingleMargin 까지는 맞는 곡으로 본다
        double margin = IsSingle(album) ? SingleMargin : 0;
        double? stored = album.Evidence.FitThreshold;
        bool Fits(ScoredTrack t) => threshold is { } th ? t.Taste >= th - margin
            : t.MeetsThreshold == true || (margin > 0 && t.Taste >= stored - margin);
        return Scored(tracks).Count(t => Fits(t) && t.MatchesDislike != true && dislikedTrack?.Invoke(t.Id) != true);
    }

    /// <summary>판정에 쓰는 곡: 점수와 계산 당시 판정이 있는 곡</summary>
    public static ScoredTrack[] Scored(IEnumerable<ScoredTrack> tracks) =>
        [.. tracks.DistinctBy(t => t.Id).Where(t => t.Id > 0 && double.IsFinite(t.Taste) && t.MeetsThreshold != null)];

    private static bool IsSingle(ReleaseRecommendation album) => string.Equals(album.RecordType, "single", StringComparison.OrdinalIgnoreCase);
}
