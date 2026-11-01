using RecipeSong.Core;

namespace RecipeSong.Wpf;

/// <summary>설정 창의 목록 변경은 임시로 보관하고 저장할 때만 반영한다.</summary>
public sealed class RecommendationSettingsEdits(BlockList blocked, FollowList follows, IEnumerable<string> disabledWebzines)
{
    private readonly HashSet<string> _originalWebzines = [.. disabledWebzines];
    public HashSet<string> DisabledWebzines { get; } = [.. disabledWebzines];
    public HashSet<long> Unfollowed { get; } = [];
    public HashSet<string> UnblockedArtists { get; } = [];
    public HashSet<string> UnblockedLabels { get; } = [];
    public HashSet<BlockList.BlockedArtistId> UnblockedSameName { get; } = [];
    public bool FollowsChanged => Unfollowed.Count > 0;
    public bool BlocksChanged => UnblockedArtists.Count + UnblockedLabels.Count + UnblockedSameName.Count > 0;
    public bool WebzinesChanged => !_originalWebzines.SetEquals(DisabledWebzines);
    public IEnumerable<FollowList.Followed> Follows => follows.All.Where(f => !Unfollowed.Contains(f.Id));
    public IEnumerable<string> Artists => blocked.Artists.Where(a => !UnblockedArtists.Contains(a));
    public IEnumerable<string> Labels => blocked.Labels.Where(l => !UnblockedLabels.Contains(l));
    public IEnumerable<BlockList.BlockedArtistId> SameName => blocked.SameName.Where(s => !UnblockedSameName.Contains(s));

    public void Apply()
    {
        foreach (var id in Unfollowed) follows.Unfollow(id);
        foreach (var artist in UnblockedArtists) blocked.UnblockArtist(artist);
        foreach (var label in UnblockedLabels) blocked.UnblockLabel(label);
        foreach (var artist in UnblockedSameName) blocked.UnblockSameName(artist);
    }
}
