using System.Text.RegularExpressions;

namespace RecipeSong.Core.Soulseek;

internal static class AlbumTrackNames
{
    internal sealed record Track(string FileName, uint Disc, uint DiscCount, uint Number, uint TrackCount, string Title);

    public static IReadOnlyList<string> Create(AlbumDownload job, IReadOnlyList<string> sources) => Metadata(job, sources).Select(t => t.FileName).ToList();

    public static IReadOnlyList<Track> Metadata(AlbumDownload job, IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return [];
        var tracks = sources.Select((source, index) => Read(job, source, index, sources.Count)).ToList();
        var multiDisc = tracks.Any(t => t.DiscCount > 1 || t.Disc > 1) || tracks.Select(t => t.Disc).Distinct().Count() > 1;
        var knownDiscCount = tracks.Max(t => t.DiscCount);
        if (knownDiscCount < tracks.Max(t => t.Disc)) knownDiscCount = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cover.jpg" };
        return tracks.Select(track =>
        {
            var prefix = multiDisc ? $"{track.Disc}-{track.Number:00}_" : $"{track.Number:00}_";
            var stem = prefix + AlbumArchive.SafeName(track.Title);
            var name = stem + ".mp3";
            for (var suffix = 2; !used.Add(name); suffix++) name = $"{stem} ({suffix}).mp3";
            return track with { FileName = name, DiscCount = knownDiscCount, TrackCount = track.TrackCount >= track.Number ? track.TrackCount : 0 };
        }).ToList();
    }

    private static Track Read(AlbumDownload job, string source, int index, int sourceCount)
    {
        uint disc = 0, discCount = 0, number = 0, trackCount = 0;
        string? title = null;
        try
        {
            using var file = TagLib.File.Create(source);
            disc = file.Tag.Disc; discCount = file.Tag.DiscCount; number = file.Tag.Track; trackCount = file.Tag.TrackCount; title = file.Tag.Title;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException) { }

        // 다운로드용 임시 순번 대신 원래 공유 파일명·폴더에서 빠진 태그를 보완한다.
        var remote = job.Files.Count == sourceCount ? job.Files[index].Filename : source;
        var path = remote.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        foreach (var prefix in new[] { job.Artist, job.Album }.Where(p => !string.IsNullOrWhiteSpace(p)))
            stem = Regex.Replace(stem, "^" + Regex.Escape(prefix) + @"\s*[-–—_]\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var paired = Regex.Match(stem, @"^(?:(?:cd|disc|disk)\s*)?(?<disc>[1-9]\d{0,2})[-._](?<track>\d{1,3})[\s._-]+(?<title>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var single = Regex.Match(stem, @"^(?<track>\d{1,3})[\s._-]+(?<title>.+)$");
        var parsed = paired.Success && (number == 0 || number == Parse(paired.Groups["track"].Value)) ? paired : single;
        if (parsed.Success)
        {
            if (number == 0) number = Parse(parsed.Groups["track"].Value);
            if (disc == 0 && parsed.Groups["disc"].Success) disc = Parse(parsed.Groups["disc"].Value);
            stem = parsed.Groups["title"].Value;
        }
        if (disc == 0)
        {
            var folder = Regex.Match(path, @"(?:^|/)(?:cd|disc|disk)\s*0*(?<disc>[1-9]\d{0,2})(?:\b[^/]*)(?:/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (folder.Success) disc = Parse(folder.Groups["disc"].Value);
        }
        return new("", disc == 0 ? 1 : disc, discCount, number == 0 ? (uint)index + 1 : number, trackCount,
            string.IsNullOrWhiteSpace(title) ? stem.Trim() : title.Trim());
    }

    private static uint Parse(string value) => uint.TryParse(value, out var number) ? number : 0;
}
