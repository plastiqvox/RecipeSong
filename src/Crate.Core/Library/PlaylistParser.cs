using System.Text;
using System.Text.RegularExpressions;

namespace Crate.Core.Library;

/// <summary>
/// Winamp 플레이리스트(.m3u, .m3u8, .pls) 파서.
/// 재생 기록이 없으므로 음원 파일의 태그(TagLib)와 파일 날짜로 보완한다.
/// </summary>
public static partial class PlaylistParser
{
    public static readonly string[] Extensions = [".m3u", ".m3u8", ".pls"];

    public static IEnumerable<TrackRecord> ParseFolder(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        foreach (var file in files)
            foreach (var t in ParseFile(file))
                yield return t;
    }

    public static IEnumerable<TrackRecord> ParseFile(string playlistPath)
    {
        var ext = Path.GetExtension(playlistPath).ToLowerInvariant();
        // .m3u는 보통 시스템 코드페이지(한국어 Windows는 CP949), .m3u8은 UTF-8
        var encoding = ext == ".m3u" ? LegacyEncoding() : Encoding.UTF8;
        var lines = File.ReadAllLines(playlistPath, encoding);
        var baseDir = Path.GetDirectoryName(playlistPath)!;
        var playlistTime = File.GetLastWriteTime(playlistPath);

        var entries = ext == ".pls" ? ParsePls(lines) : ParseM3u(lines);
        foreach (var (path, hint) in entries)
        {
            var full = ResolvePath(baseDir, path);
            yield return BuildTrack(full, hint, playlistTime);
        }
    }

    private static IEnumerable<(string Path, string? Hint)> ParseM3u(string[] lines)
    {
        string? hint = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                hint = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                continue;
            }
            if (line.StartsWith('#')) continue;
            yield return (line, hint);
            hint = null;
        }
    }

    private static IEnumerable<(string Path, string? Hint)> ParsePls(string[] lines)
    {
        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            var m = PlsEntry().Match(line);
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[2].Value);
            if (m.Groups[1].Value.Equals("File", StringComparison.OrdinalIgnoreCase)) files[n] = m.Groups[3].Value.Trim();
            else titles[n] = m.Groups[3].Value.Trim();
        }
        foreach (var (n, f) in files) yield return (f, titles.GetValueOrDefault(n));
    }

    private static string ResolvePath(string baseDir, string entry)
    {
        if (Uri.TryCreate(entry, UriKind.Absolute, out var u) && u.IsFile) return u.LocalPath;
        if (entry.Contains("://")) return entry; // 스트림 URL
        return Path.IsPathRooted(entry) ? entry : Path.GetFullPath(Path.Combine(baseDir, entry));
    }

    private static TrackRecord BuildTrack(string path, string? hint, DateTime playlistTime)
    {
        string? title = null, artist = null, album = null, albumArtist = null, genre = null;
        int? year = null;
        TimeSpan? duration = null;
        DateTime? added = null;

        if (File.Exists(path))
        {
            added = File.GetCreationTime(path);
            try
            {
                using var tf = TagLib.File.Create(path);
                title = NullIfEmpty(tf.Tag.Title);
                artist = NullIfEmpty(tf.Tag.FirstPerformer);
                albumArtist = NullIfEmpty(tf.Tag.FirstAlbumArtist);
                album = NullIfEmpty(tf.Tag.Album);
                genre = NullIfEmpty(tf.Tag.FirstGenre);
                year = tf.Tag.Year > 0 ? (int)tf.Tag.Year : null;
                duration = tf.Properties.Duration;
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException) { }
        }

        // 태그가 없으면 "Artist - Title" 형태의 EXTINF/파일명에서 추정
        if (artist == null || title == null)
        {
            var source = hint ?? Path.GetFileNameWithoutExtension(path);
            var dash = source.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist ??= source[..dash].Trim();
                title ??= source[(dash + 3)..].Trim();
            }
            title ??= source;
        }

        return new TrackRecord
        {
            Title = title,
            Artist = artist ?? "Unknown Artist",
            AlbumArtist = albumArtist,
            Album = album ?? "",
            Genre = genre,
            Year = year,
            Duration = duration,
            DateAdded = added ?? playlistTime,
            // 플레이리스트가 마지막으로 수정된 시점을 '들은 시기'의 근사치로 사용
            LastPlayed = playlistTime,
            PlayCount = 1,
            Location = path,
            Source = LibrarySource.Winamp
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Encoding LegacyEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }

    [GeneratedRegex(@"^(File|Title)(\d+)=(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlsEntry();
}
