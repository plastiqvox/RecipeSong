namespace RecipeSong.Core;

public enum TasteDecision { Recommended, NeedsReview, BelowThreshold }

/// <summary>출처·팔로우 여부와 관계없이 계산 당시의 곡별 근거로 통과 여부를 정한다.</summary>
public static class TasteGate
{
    public static TasteDecision Assess(ReleaseRecommendation album, Func<long, bool>? dislikedTrack = null)
    {
        if (album.Taste is not { } taste || !double.IsFinite(taste) || album.Evidence?.Tracks is not { Count: > 0 } tracks)
            return TasteDecision.NeedsReview;
        var scored = tracks.DistinctBy(t => t.Id).Where(t => t.Id > 0 && double.IsFinite(t.Taste) && t.MeetsThreshold != null).ToArray();
        bool single = string.Equals(album.RecordType, "single", StringComparison.OrdinalIgnoreCase);
        int total = album.Evidence.TotalTracks is > 0 ? album.Evidence.TotalTracks.Value : int.MaxValue;
        if (scored.Length < (single ? 1 : Math.Min(3, total)) || album.AnalyzedTracks != scored.Length || scored.Length > total)
            return TasteDecision.NeedsReview;
        int fits = scored.Count(t => t.MeetsThreshold == true && t.MatchesDislike != true && dislikedTrack?.Invoke(t.Id) != true);
        return fits >= (single ? 1 : Math.Min(2, total)) ? TasteDecision.Recommended : TasteDecision.BelowThreshold;
    }
}
