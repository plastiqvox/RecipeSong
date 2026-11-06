using System.IO;
using System.Text;
using RecipeSong.Core;
using RecipeSong.Wpf.ViewModels;

static partial class Program
{
    static void CheckExcludedPlayback(string output)
    {
        // 사용자 앱·외부 미리듣기·실제 음악 없이, 별도 캐시와 무음 파일만 사용한다.
        var path = Path.Combine(output, "silence.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 1600);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(1600); writer.Write(new byte[1600]);
        }
        var album = new ReleaseRecommendation("Test artist", "Playing album", "album", new(2026, 11, 1), "Test label", null,
            1001, null, null, Region.International, null, null, null, [], [], false, 0, ArtistId: 41);
        var other = album with { Artist = "Other artist", Label = "Other label", DeezerAlbumId = 1002, ArtistId = 42 };
        var card = new ReleaseItem(album, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, false);
        var player = new PlayerViewModel { Volume = 0 };
        var disliked = new SavedReleases(Path.Combine(output, "disliked.json"));
        var blocked = new BlockList();
        var emptyBlocks = blocked.Snapshot();
        bool ownerPlaying = false;
        int ended = 0;
        int listened = 0;
        player.Listened += (_, _, _) => listened++;
        Action<bool> owner = playing => ownerPlaying = playing;
        bool Excluded(ReleaseRecommendation r) => disliked.Contains(r.DeezerAlbumId) || blocked.IsBlocked(r);
        void Play() => player.Play(new Uri(path), "Silent test", "", null, owner, () => ended++, card);
        void ExpectStopped()
        {
            if (player.IsPlaying || player.HasMedia || player.NowRelease != null || player.HasNowRelease || ownerPlaying
                || player.IsCurrent(owner) || player.Position != 0 || ended != 0 || listened != 0)
                throw new Exception("Excluding the playing album must close media and clear its owner without auto-advancing.");
            player.Toggle();
            player.Rebind(owner, () => ended++, card);
            if (player.IsPlaying || player.HasMedia || player.NowRelease != null || ended != 0)
                throw new Exception("Stopped playback must not resume or rebind through a stale action.");
        }

        Play();
        disliked.Add(other);
        player.StopIfExcluded(Excluded);
        if (!player.IsPlaying || player.NowRelease != card) throw new Exception("Disliking another album must not stop playback.");
        disliked.Add(album);
        player.StopIfExcluded(Excluded);
        ExpectStopped();
        disliked.Remove(album.DeezerAlbumId);
        player.StopIfExcluded(Excluded);
        ExpectStopped();

        Play();
        blocked.BlockArtist(other, BlockReason.Other);
        player.StopIfExcluded(Excluded);
        if (!player.IsPlaying) throw new Exception("Blocking another artist must not stop playback.");
        blocked.BlockArtist(album with { DeezerAlbumId = 1003, Album = "Another album" }, BlockReason.Other);
        player.StopIfExcluded(Excluded);
        ExpectStopped();
        blocked.Restore(emptyBlocks);

        Play();
        player.Toggle();
        blocked.BlockLabel(album with { Artist = "Label mate", DeezerAlbumId = 1004 }, BlockReason.Other);
        player.StopIfExcluded(Excluded);
        ExpectStopped();
        blocked.Restore(emptyBlocks);

        Play();
        blocked.BlockArtist(album with { ArtistId = 99, DeezerAlbumId = 1005 }, BlockReason.SameName);
        player.StopIfExcluded(Excluded);
        if (!player.IsPlaying) throw new Exception("Blocking a namesake with a different artist ID must preserve playback.");
        blocked.BlockArtist(album with { DeezerAlbumId = 1006 }, BlockReason.SameName);
        player.StopIfExcluded(Excluded);
        ExpectStopped();
        blocked.Restore(emptyBlocks);
        player.Stop();
    }
}
