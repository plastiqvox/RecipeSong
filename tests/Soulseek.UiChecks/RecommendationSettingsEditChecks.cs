using System.IO;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Wpf;

static partial class Program
{
    static void CheckRecommendationSettingsEdits(string output)
    {
        var previous = CacheLocation.Directory;
        CacheLocation.Directory = Path.Combine(output, "settings-edits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(CacheLocation.Directory);
        try
        {
            var blocked = new BlockList();
            var sameName = new BlockList.BlockedArtistId(30, "Other artist", 3, "Other album", true);
            blocked.Restore(JsonSerializer.Serialize(new BlockList.Data(["Remove artist", "Keep artist"], ["Remove label"],
                [new(1, "Remove artist", "Remove label"), new(2, "Keep artist", null)], [sameName])));
            var follows = new FollowList();
            follows.Follow(10, "Remove follow", "");
            follows.Follow(20, "Keep follow", "");
            var webzines = new HashSet<string> { "Original feed" };
            var files = new[] { "blocked.json", "followed.json" }.Select(name => Path.Combine(CacheLocation.Directory, name)).ToArray();
            var before = files.Select(File.ReadAllText).ToArray();
            var edits = new RecommendationSettingsEdits(blocked, follows, webzines);
            edits.Unfollowed.Add(10);
            edits.UnblockedArtists.Add("Remove artist");
            edits.UnblockedLabels.Add("Remove label");
            edits.UnblockedSameName.Add(sameName);
            edits.DisabledWebzines.Add("New feed");
            if (!edits.BlocksChanged || !edits.FollowsChanged || !edits.WebzinesChanged
                || edits.Follows.Count() != 1 || edits.Artists.Count() != 1 || edits.Labels.Any() || edits.SameName.Any())
                throw new Exception("Draft edits did not update their preview.");
            if (!before.SequenceEqual(files.Select(File.ReadAllText)) || !follows.Contains(10) || !blocked.Artists.Contains("Remove artist")
                || webzines.Contains("New feed")) throw new Exception("Editing settings wrote to the live lists before save.");
            var cancelled = new RecommendationSettingsEdits(new BlockList(), new FollowList(), webzines);
            if (cancelled.BlocksChanged || cancelled.FollowsChanged || cancelled.WebzinesChanged
                || cancelled.Follows.Count() != 2 || cancelled.Artists.Count() != 2 || cancelled.SameName.Count() != 1)
                throw new Exception("Discarded edits survived reopening settings.");
            edits.Apply();
            var savedBlocks = new BlockList();
            var savedFollows = new FollowList();
            if (savedFollows.Contains(10) || !savedFollows.Contains(20) || savedBlocks.Artists.Contains("Remove artist")
                || !savedBlocks.Artists.Contains("Keep artist") || savedBlocks.Labels.Any() || savedBlocks.SameName.Any()
                || savedBlocks.AlbumIds.Contains(1) || !savedBlocks.AlbumIds.Contains(2))
                throw new Exception("Saved list edits or their learning signals were not persisted correctly.");
            edits.DisabledWebzines.Remove("New feed");
            if (edits.WebzinesChanged) throw new Exception("Restoring the original feed selection should not count as a change.");
        }
        finally { CacheLocation.Directory = previous; }
    }
}
