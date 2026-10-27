using System.Globalization;
using System.Xml;

namespace RecipeSong.Core.Library;

/// <summary>플레이리스트가 곡을 iTunes Track ID로 가리키는 원본 형태.</summary>
public sealed record ITunesPlaylistRaw(
    string Name, string? PersistentId, string? ParentPersistentId,
    PlaylistKind Kind, IReadOnlyList<int> TrackIds);

public sealed record ITunesLibrary(IReadOnlyList<TrackRecord> Tracks, IReadOnlyList<ITunesPlaylistRaw> Playlists);

/// <summary>
/// "iTunes Music Library.xml" / "Library.xml" (plist) 파서.
/// 수십 MB여도 객체 트리로 한 번에 읽는 편이 단순하고 충분히 빠르다.
/// </summary>
public static class ITunesLibraryParser
{
    /// <summary>iTunes가 만드는 시스템 플레이리스트를 구분하는 키들</summary>
    private static readonly string[] SystemKeys =
        ["Master", "Distinguished Kind", "Music", "Movies", "TV Shows", "Podcasts", "Audiobooks", "Purchased Music", "Books"];

    public static IEnumerable<TrackRecord> Parse(string path) => ParseLibrary(path).Tracks;

    public static ITunesLibrary ParseLibrary(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(path, settings);
        reader.MoveToContent();  // <plist>
        reader.Read();           // <dict>
        var root = ReadDict(reader);

        var tracks = new List<TrackRecord>();
        if (root.GetValueOrDefault("Tracks") is Dictionary<string, object?> trackDict)
            foreach (var v in trackDict.Values)
                if (v is Dictionary<string, object?> f && ToTrack(f) is { } t)
                    tracks.Add(t);

        var playlists = new List<ITunesPlaylistRaw>();
        if (root.GetValueOrDefault("Playlists") is List<object?> pl)
            foreach (var p in pl.OfType<Dictionary<string, object?>>())
                playlists.Add(ToPlaylist(p));

        return new ITunesLibrary(tracks, playlists);
    }

    private static object? ReadValue(XmlReader r)
    {
        switch (r.Name)
        {
            case "dict": return ReadDict(r);
            case "array": return ReadArray(r);
            case "true": r.Skip(); return true;
            case "false": r.Skip(); return false;
            default: return r.ReadElementContentAsString();
        }
    }

    // ReadElementContentAsString/Skip은 다음 노드로 이미 이동하므로 그때는 Read()를 부르지 않는다
    private static Dictionary<string, object?> ReadDict(XmlReader r)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.IsEmptyElement) { r.Read(); return d; }
        r.Read();
        string? key = null;
        while (r.NodeType != XmlNodeType.EndElement && !r.EOF)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
            if (r.Name == "key") { key = r.ReadElementContentAsString(); continue; }
            var v = ReadValue(r);
            if (key != null) d[key] = v;
            key = null;
        }
        r.Read();
        return d;
    }

    private static List<object?> ReadArray(XmlReader r)
    {
        var list = new List<object?>();
        if (r.IsEmptyElement) { r.Read(); return list; }
        r.Read();
        while (r.NodeType != XmlNodeType.EndElement && !r.EOF)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
            list.Add(ReadValue(r));
        }
        r.Read();
        return list;
    }

    private static string? Str(Dictionary<string, object?> f, string key) => f.GetValueOrDefault(key) as string;

    private static TrackRecord? ToTrack(Dictionary<string, object?> f)
    {
        if (f.ContainsKey("Podcast") || f.ContainsKey("Movie") || f.ContainsKey("TV Show")
            || Str(f, "Kind")?.Contains("video", StringComparison.OrdinalIgnoreCase) == true)
            return null;
        if (Str(f, "Name") is not { } name) return null;

        return new TrackRecord
        {
            ITunesTrackId = int.TryParse(Str(f, "Track ID"), out var id) ? id : null,
            Title = name,
            Artist = Str(f, "Artist") ?? "Unknown Artist",
            AlbumArtist = Str(f, "Album Artist"),
            Album = Str(f, "Album") ?? "",
            Genre = Str(f, "Genre"),
            Year = int.TryParse(Str(f, "Year"), out var y) ? y : null,
            PlayCount = int.TryParse(Str(f, "Play Count"), out var pc) ? pc : 0,
            Loved = f.GetValueOrDefault("Loved") is true || f.GetValueOrDefault("Favorited") is true,
            Rating = f.GetValueOrDefault("Rating Computed") is true ? 0 : int.TryParse(Str(f, "Rating"), out var rt) ? rt : 0,
            Disliked = f.GetValueOrDefault("Disliked") is true,
            SkipCount = int.TryParse(Str(f, "Skip Count"), out var sk) ? sk : 0,
            DateAdded = ParseDate(Str(f, "Date Added")),
            LastPlayed = ParseDate(Str(f, "Play Date UTC")),
            Duration = long.TryParse(Str(f, "Total Time"), out var ms) ? TimeSpan.FromMilliseconds(ms) : null,
            Location = LocationToPath(Str(f, "Location")),
            Source = LibrarySource.ITunes
        };
    }

    private static ITunesPlaylistRaw ToPlaylist(Dictionary<string, object?> p)
    {
        var kind =
            SystemKeys.Any(p.ContainsKey) || p.GetValueOrDefault("Visible") is false ? PlaylistKind.System
            : p.GetValueOrDefault("Folder") is true ? PlaylistKind.Folder
            : p.ContainsKey("Smart Info") || p.ContainsKey("Smart Criteria") ? PlaylistKind.Smart
            : PlaylistKind.User;

        var ids = (p.GetValueOrDefault("Playlist Items") as List<object?>)?
            .OfType<Dictionary<string, object?>>()
            .Select(i => int.TryParse(Str(i, "Track ID"), out var id) ? id : -1)
            .Where(id => id >= 0)
            .ToList() ?? [];

        return new ITunesPlaylistRaw(
            Str(p, "Name") ?? "(이름 없음)", Str(p, "Playlist Persistent ID"), Str(p, "Parent Persistent ID"), kind, ids);
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToLocalTime() : null;

    private static string? LocationToPath(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        // iTunes는 file://localhost/D:/... 형식을 쓰는데 그대로 두면 \\localhost\D:\ UNC 경로가 된다
        if (url.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase)) url = "file:///" + url[17..];
        try { return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null; }
        catch (UriFormatException) { return null; }
    }
}
