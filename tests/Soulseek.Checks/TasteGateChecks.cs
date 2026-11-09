using RecipeSong.Core;

static class TasteGateChecks
{
    public static void Run()
    {
        var album = new ReleaseRecommendation("Artist", "Album", "album", new(2026, 11, 4), null, null, 1, null, null,
            Region.International, null, null, .58, [], [], false, 999, AnalyzedTracks: 3,
            Evidence: new([], .5, .6, .55, false, [new(11, "One", .6, true), new(12, "Two", .61, true), new(13, "Three", .3, false)]));
        foreach (var candidate in new[] { album, album with { AiReason = "AI", AiBy = ["GPT"] }, album with { FromPlaylist = "내 라이브러리" }, album with { FromPlaylist = FollowList.Tag } })
            if (TasteGate.Assess(candidate) != TasteDecision.Recommended) throw new Exception("Source changed the common taste decision.");
        var evidence = album.Evidence!;
        var bad = album with { Evidence = evidence with { Tracks = [new(11, "One", .6, true), new(12, "Two", .61, false), new(13, "Three", .3, false)] } };
        if (TasteGate.Assess(bad) != TasteDecision.BelowThreshold || TasteGate.Assess(bad with { FromPlaylist = FollowList.Tag }) != TasteDecision.BelowThreshold)
            throw new Exception("Known/followed artist or ranking score bypassed taste criteria.");
        // 내 라이브러리 뮤지션 신보는 기준 미달·근거 없음이어도 보인다
        if (TasteGate.Assess(bad with { FromPlaylist = TasteProfile.LibraryTag }) != TasteDecision.Recommended
            || TasteGate.Assess(album with { FromPlaylist = TasteProfile.LibraryTag, Taste = null }) != TasteDecision.Recommended)
            throw new Exception("Library artist release was cut by the taste gate.");
        // 지금 기준선을 주면 저장된 통과 표시 대신 곡 점수로 다시 판정한다 (기준선이 계산마다 움직여도 같은 점수는 같은 판정)
        if (TasteGate.Assess(bad, threshold: .5) != TasteDecision.Recommended || TasteGate.Assess(album, threshold: .65) != TasteDecision.BelowThreshold
            || TasteGate.Assess(bad with { FromPlaylist = FollowList.Tag }, id => id == 12, .5) != TasteDecision.BelowThreshold)
            throw new Exception("Current threshold was not applied to stored track scores.");
        if (TasteGate.Assess(album, id => id == 12) != TasteDecision.BelowThreshold)
            throw new Exception("Explicitly disliked track still counted as a match.");
        var negative = album with { Evidence = evidence with { Tracks = [new(11, "One", .6, true), new(12, "Two", .61, true, true), new(13, "Three", .3, false)] } };
        if (TasteGate.Assess(negative) != TasteDecision.BelowThreshold) throw new Exception("Negative match bypassed gate.");
        foreach (var unknown in new[] { album with { Taste = null }, album with { Evidence = null }, album with { AnalyzedTracks = 1 },
            album with { Evidence = evidence with { Tracks = [new(11, "One", .6)] } } })
            if (TasteGate.Assess(unknown) != TasteDecision.NeedsReview) throw new Exception("Missing evidence was treated as fit/dislike.");
        var single = album with { RecordType = "single", AnalyzedTracks = 1, Evidence = evidence with { Tracks = [new(11, "One", .6, true)] } };
        if (TasteGate.Assess(single) != TasteDecision.Recommended) throw new Exception("A fully scored single requires three tracks.");
        // 싱글 여유폭: 기준선(.55) 바로 아래는 통과, 그보다 아래는 미달. 앨범에는 여유폭을 쓰지 않는다
        if (TasteGate.Assess(single with { Evidence = evidence with { Tracks = [new(11, "One", .547, false)] } }) != TasteDecision.Recommended
            || TasteGate.Assess(single with { Evidence = evidence with { Tracks = [new(11, "One", .54, false)] } }) != TasteDecision.BelowThreshold
            || TasteGate.Assess(album with { Evidence = evidence with { Tracks = [new(11, "One", .6, true), new(12, "Two", .547, false), new(13, "Three", .3, false)] } }) != TasteDecision.BelowThreshold)
            throw new Exception("Single margin was not applied or leaked to albums.");
        var ep = album with { RecordType = "ep", AnalyzedTracks = 2, Evidence = evidence with { TotalTracks = 2,
            Tracks = [new(11, "One", .6, true), new(12, "Two", .61, true)] } };
        if (TasteGate.Assess(ep) != TasteDecision.Recommended || TasteGate.Assess(ep with { Evidence = ep.Evidence! with { TotalTracks = null } }) != TasteDecision.NeedsReview)
            throw new Exception("Known short release and insufficient preview coverage were conflated.");
        var store = new DislikedTracks();
        store.Toggle(new(77, 1, "Artist", "Disliked", DateTime.Now));
        if (!new DislikedTracks().Contains(77)) throw new Exception("Track dislike did not survive reload.");
        store.Remove(77);
        if (new DislikedTracks().Contains(77)) throw new Exception("Removed track dislike was retained.");
        if (store.Toggle(new(0, 1, "Artist", "Unknown ID", DateTime.Now))) throw new Exception("Invalid track ID was saved.");
        Console.WriteLine("Taste gate: all sources, library bypass, followed artists, missing evidence, singles, track dislikes and persistence passed.");
    }
}
