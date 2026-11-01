using System.IO.Compression;
using RecipeSong.Core.Soulseek;

static class TrackNameChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string root, string ffmpeg)
    {
        var folder = Path.Combine(root, "track-names");
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "base.flac");
        await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", original], default);
        string Tagged(string fileName, uint disc, uint discCount, uint track, string? title)
        {
            var target = Path.Combine(folder, fileName + ".flac");
            File.Copy(original, target);
            using var audio = TagLib.File.Create(target);
            audio.Tag.Disc = disc; audio.Tag.DiscCount = discCount; audio.Tag.Track = track; audio.Tag.Title = title;
            audio.Tag.Album = "Old album"; audio.Tag.AlbumArtists = ["Old album artist"]; audio.Tag.Performers = ["Old artist"];
            audio.Save();
            return target;
        }
        var single = Tagged("single", 1, 1, 7, "Song title");
        Check(AlbumTrackNames.Create(new(), [single]).Single() == "07_Song title.mp3", "Single disc must omit disc prefix.");
        var first = Tagged("first", 1, 2, 1, "Same Song");
        var second = Tagged("second", 2, 2, 1, "Same Song");
        Check(AlbumTrackNames.Create(new(), [first]).Single() == "1-01_Same Song.mp3", "Partial multi-disc selection must retain known disc count.");
        Check(AlbumTrackNames.Create(new(), [first, second]).SequenceEqual(new[] { "1-01_Same Song.mp3", "2-01_Same Song.mp3" }), "Multi-disc naming failed.");
        var noTags = Tagged("no-tags", 0, 0, 0, null);
        var remoteJob = new AlbumDownload { Files = [new(@"Album\CD1\03 - Intro.flac", 1), new(@"Album\Disc 2\01_Outro.flac", 1)] };
        Check(AlbumTrackNames.Create(remoteJob, [noTags, noTags]).SequenceEqual(new[] { "1-03_Intro.mp3", "2-01_Outro.mp3" }), "Remote file and disc-folder fallback failed.");
        remoteJob.Files = [new(@"Album\04. 99 Luftballons.FLAC", 1)];
        Check(AlbumTrackNames.Create(remoteJob, [noTags]).Single() == "04_99 Luftballons.mp3", "Numeric song title must not become a disc number.");
        remoteJob.Artist = "Artist"; remoteJob.Album = "Album"; remoteJob.Files = [new(@"Album\Artist_Album_05_Song.flac", 1)];
        Check(AlbumTrackNames.Create(remoteJob, [noTags]).Single() == "05_Song.mp3", "Artist and album filename prefixes must not become the song title.");
        var unsafeTitle = Tagged("unsafe", 0, 0, 1, "A/B");
        Check(AlbumTrackNames.Create(new(), [unsafeTitle]).Single() == "01_A_B.mp3", "Invalid filename characters must be sanitized.");
        var duplicate = AlbumTrackNames.Create(new(), [single, single]);
        Check(duplicate.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Duplicate names must not overwrite tracks.");
        var cover = Path.Combine(folder, "art.jpg");
        await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "color=c=blue:s=128x128", "-frames:v", "1", cover], default);
        var job = new AlbumDownload { Artist = "Artist", Album = "Multi disc", RecordType = "album", ReleaseDate = new(2026, 11, 1), CoverUrl = cover, FfmpegPath = ffmpeg, ArchiveDirectory = Path.Combine(folder, "archives") };
        var zip = await AlbumArchive.CreateAsync(job, [first, second], default);
        using (var archive = ZipFile.OpenRead(zip))
            Check(archive.Entries.Select(e => e.Name).SequenceEqual(new[] { "cover.jpg", "1-01_Same Song.mp3", "2-01_Same Song.mp3" }), "Actual ZIP must use the requested audio filenames.");
        void Tags(string path, string entryName, string title, uint track, uint disc, uint trackTotal, uint discTotal)
        {
            using var archive = ZipFile.OpenRead(path);
            var extracted = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp3");
            (archive.GetEntry(entryName) ?? throw new Exception("Named track missing.")).ExtractToFile(extracted);
            using var audio = TagLib.File.Create(extracted);
            Check(audio.Tag.Title == title && audio.Tag.Track == track && audio.Tag.Disc == disc, "Track title/number/disc tags must match filenames.");
            Check(audio.Tag.TrackCount == trackTotal && audio.Tag.DiscCount == discTotal, "Partial selections must not invent track/disc totals.");
            Check(audio.Tag.Album == job.Album && audio.Tag.Performers.Contains(job.Artist) && audio.Tag.AlbumArtists.Contains(job.Artist), "Album and artist tags must be updated.");
            Check(audio.Tag.Year == 2026 && audio.Tag.Pictures.Length == 1, "Release year and embedded artwork must remain.");
        }
        Tags(zip, "1-01_Same Song.mp3", "Same Song", 1, 1, 0, 2);
        Tags(zip, "2-01_Same Song.mp3", "Same Song", 1, 2, 0, 2);
        using (var audio = TagLib.File.Create(single)) { audio.Tag.TrackCount = 12; audio.Save(); }
        var singleZip = await AlbumArchive.CreateAsync(job, [single], default);
        Tags(singleZip, "07_Song title.mp3", "Song title", 7, 1, 12, 1);
        job.Files = [new(@"Album\04_Fallback title.flac", 1)];
        var fallbackZip = await AlbumArchive.CreateAsync(job, [noTags], default);
        Tags(fallbackZip, "04_Fallback title.mp3", "Fallback title", 4, 1, 0, 0);
        var mp3 = Path.Combine(folder, "untagged.mp3");
        await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-i", original, "-c:a", "libmp3lame", mp3], default);
        job.Files = [new(@"Album\09_Copied audio.mp3", 1)];
        var copiedZip = await AlbumArchive.CreateAsync(job, [mp3], default);
        Tags(copiedZip, "09_Copied audio.mp3", "Copied audio", 9, 1, 0, 0);
        Check(File.Exists(first) && File.Exists(second), "Naming must not rename received source files.");
        Console.WriteLine("Single/multi-disc filenames, album/artist/title/track/disc tags, partial totals and FLAC/MP3 output checks passed");
    }
}
