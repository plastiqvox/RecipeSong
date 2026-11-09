namespace RecipeSong.Core;

public enum TasteDecision { Recommended, NeedsReview, BelowThreshold }

/// <summary>
/// 계산 당시의 곡별 근거로 통과 여부를 정한다. 일반·AI·웹진·팔로우는 같은 기준을 쓴다.
/// 내 라이브러리 뮤지션의 신보는 기준을 건너뛴다: 이미 듣는 사람의 신보는 미리듣기 한두 곡 점수보다 '나왔다'는 사실이 중요하고,
/// 싱글은 한 곡으로 갈려 기준선 바로 아래에서 절반 넘게 숨겨졌다. 앨범 별로·뮤지션 차단은 화면에서 따로 거른다.
/// </summary>
public static class TasteGate
{
    public const double SingleMargin = 0.005;

    public static TasteDecision Assess(ReleaseRecommendation album, Func<long, bool>? dislikedTrack = null)
    {
        if (album.FromPlaylist == TasteProfile.LibraryTag) return TasteDecision.Recommended;
        if (album.Taste is not { } taste || !double.IsFinite(taste) || album.Evidence?.Tracks is not { Count: > 0 } tracks)
            return TasteDecision.NeedsReview;
        var scored = tracks.DistinctBy(t => t.Id).Where(t => t.Id > 0 && double.IsFinite(t.Taste) && t.MeetsThreshold != null).ToArray();
        bool single = string.Equals(album.RecordType, "single", StringComparison.OrdinalIgnoreCase);
        int total = album.Evidence.TotalTracks is > 0 ? album.Evidence.TotalTracks.Value : int.MaxValue;
        if (scored.Length < (single ? 1 : Math.Min(3, total)) || album.AnalyzedTracks != scored.Length || scored.Length > total)
            return TasteDecision.NeedsReview;
        // 싱글은 한 곡으로 갈려 기준선 0.003 아래(오차 수준)에서도 빠졌다 → 기준선 아래 SingleMargin 까지는 맞는 곡으로 본다
        double? near = single && album.Evidence.FitThreshold is { } th ? th - SingleMargin : null;
        int fits = scored.Count(t => (t.MeetsThreshold == true || t.Taste >= near) && t.MatchesDislike != true && dislikedTrack?.Invoke(t.Id) != true);
        return fits >= (single ? 1 : Math.Min(2, total)) ? TasteDecision.Recommended : TasteDecision.BelowThreshold;
    }
}
