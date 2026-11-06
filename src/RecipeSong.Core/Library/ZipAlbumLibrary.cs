using System.IO.Compression;
using System.Text.RegularExpressions;

namespace RecipeSong.Core.Library;

/// <summary>ZIP 소장은 보유 정보로만 사용한다. 청취 이력·플리·취향 학습에는 넣지 않는다.</summary>
public sealed record ArchiveAlbum(string Artist, string Album, int? Year, int Tracks, string Path);
public enum ArchiveScanStage { Waiting, Enumerating, Reading, Saving, Completed }
public sealed record ArchiveScanProgress(ArchiveScanStage Stage, string CurrentPath, int Processed, int Albums, int Reused, int Warnings);

public sealed partial class ZipAlbumLibrary
{
    public sealed record Entry(long Length, long Modified, ArchiveAlbum? Album);
    private readonly JsonCache<Entry> _files = new("zip-album-files", TimeSpan.FromDays(36500));
    private readonly JsonCache<List<ArchiveAlbum>> _folders = new("zip-album-folders", TimeSpan.FromDays(36500));
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".aiff", ".aif", ".wma", ".alac" };
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public IReadOnlyList<ArchiveAlbum> Scan(IEnumerable<string> folders, CancellationToken ct, IProgress<ArchiveScanProgress>? progress = null)
    {
        var albums = new List<ArchiveAlbum>();
        var warnings = new List<string>();
        var known = new HashSet<string>();
        int processed = 0, reused = 0;
        long lastReport = 0;
        static string Key(ArchiveAlbum album) => ListeningAnalyzer.NormalizeArtist(album.Artist) + "|" + ListeningAnalyzer.NormalizeArtist(album.Album);
        void Report(ArchiveScanStage stage, string path, bool force = false)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (progress == null || !force && now - lastReport < System.Diagnostics.Stopwatch.Frequency / 10) return;
            lastReport = now;
            progress.Report(new(stage, path, processed, known.Count, reused, warnings.Count));
        }
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            Report(ArchiveScanStage.Enumerating, folder, force: true);
            var found = new List<ArchiveAlbum>();
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
                foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true,
                    IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        if (!_files.TryGet(path, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc.Ticks)
                        {
                            Report(ArchiveScanStage.Reading, path, force: true);
                            cached = new(info.Length, info.LastWriteTimeUtc.Ticks, Read(path, ct));
                            _files.Set(path, cached);
                        }
                        else reused++;
                        if (cached.Album is { } album) { found.Add(album); known.Add(Key(album)); }
                        else warnings.Add(L.F("앨범 정보를 읽지 못했어요: {0}", path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { warnings.Add(L.F("ZIP을 읽지 못했어요: {0}", path)); }
                    processed++;
                    Report(ArchiveScanStage.Reading, path);
                }
                _folders.Set(folder, found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(L.F("아카이브 경로를 읽지 못해 마지막 보유 정보를 사용해요: {0}", folder));
                if (_folders.TryGet(folder, out var previous)) found = previous;
                known.Clear();
                known.UnionWith(albums.Concat(found).Select(Key));
            }
            albums.AddRange(found);
        }
        ct.ThrowIfCancellationRequested();
        Report(ArchiveScanStage.Saving, "", force: true);
        _files.Save();
        _folders.Save();
        Warnings = warnings;
        Report(ArchiveScanStage.Completed, "", force: true);
        return albums.DistinctBy(a => ListeningAnalyzer.NormalizeArtist(a.Artist) + "|" + ListeningAnalyzer.NormalizeArtist(a.Album)).ToArray();
    }

    private static ArchiveAlbum? Read(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var audio = zip.Entries.Where(e => Audio.Contains(Path.GetExtension(e.Name)) && !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (audio.Length == 0) return null;
        var trackCount = audio.Select(e => Path.ChangeExtension(e.FullName, null)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // 대표 음원 태그를 우선한다. 압축 해제 경로를 만들지 않고 읽는 크기를 제한한다.
        foreach (var entry in audio.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length is <= 0 or > 64 * 1024 * 1024) continue;
            try
            {
                var data = new byte[(int)entry.Length];
                using (var stream = entry.Open()) stream.ReadExactly(data);
                using var tags = TagLib.File.Create(new ZipAudio(entry.Name, data), TagLib.ReadStyle.None);
                var artist = !string.IsNullOrWhiteSpace(tags.Tag.FirstAlbumArtist) ? tags.Tag.FirstAlbumArtist : tags.Tag.FirstPerformer;
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(tags.Tag.Album))
                    return new(artist.Trim(), tags.Tag.Album.Trim(), tags.Tag.Year > 0 ? (int)tags.Tag.Year : null, trackCount, path);
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException) { }
        }
        // RecipeSong이 만든 표준 ZIP 이름도 읽는다. 임의 이름은 추측해 보유 처리하지 않는다.
        var match = ArchiveName().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success ? new(match.Groups["artist"].Value.Trim(), match.Groups["album"].Value.Trim(),
            int.Parse(match.Groups["year"].Value), trackCount, path) : null;
    }

    [GeneratedRegex(@"^\[(?<year>\d{4})\.\d{2}\.\d{2}\] \([^)]*\) (?<artist>.+?) - (?<album>.+)$")]
    private static partial Regex ArchiveName();

    private sealed class ZipAudio(string name, byte[] data) : TagLib.File.IFileAbstraction
    {
        public string Name => name;
        public Stream ReadStream => new MemoryStream(data, writable: false);
        public Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
