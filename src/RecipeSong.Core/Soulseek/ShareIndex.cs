using System.Collections.Concurrent;
using Slsk = global::Soulseek;

namespace RecipeSong.Core.Soulseek;

public sealed record SharedFile(string Remote, string Local, string Root, long Size, Slsk.File Info);

/// <summary>스캔한 파일만 공개한다. 링크·정션은 따라가지 않고 업로드 직전에 경로를 다시 확인한다.</summary>
public sealed class ShareIndex
{
    private SharedFile[] _files = [];
    private long _generation;
    public IReadOnlyList<SharedFile> Files => Volatile.Read(ref _files);
    public string Status { get; private set; } = "";
    public int DirectoryCount => Files.Select(f => Path.GetDirectoryName(f.Remote)).Distinct().Count();
    private readonly SemaphoreSlim _scan = new(1, 1);

    public void RestrictTo(IEnumerable<string> roots)
    {
        Interlocked.Increment(ref _generation);
        var allowed = roots.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _files, Files.Where(f => allowed.Contains(f.Root)).ToArray());
    }

    public async Task ScanAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        await _scan.WaitAsync(ct);
        try
        {
            var generation = Interlocked.Read(ref _generation);
            var result = await Task.Run(() =>
            {
                var files = new List<SharedFile>();
                int errors = 0;
                for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    var root = Path.GetFullPath(roots[rootIndex]);
                    var alias = $"{rootIndex + 1:00}-" + AlbumArchive.SafeName(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
                    try
                    {
                        if (!IsSafe(root, root) || !Directory.Exists(root)) { errors++; continue; }
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                        foreach (var local in Directory.EnumerateFiles(root, "*", options))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (Path.GetRelativePath(root, local).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith(".recipesong-", StringComparison.OrdinalIgnoreCase))) continue;
                            if (local.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || local.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                var info = new FileInfo(local);
                                if (!IsSafe(root, local)) { errors++; continue; }
                                var remote = alias + "\\" + Path.GetRelativePath(root, local).Replace('/', '\\');
                                var attributes = new List<Slsk.FileAttribute>();
                                if (new SoulseekFile(remote, info.Length).IsAudio)
                                {
                                    try
                                    {
                                        using var tags = TagLib.File.Create(local);
                                        attributes.Add(new(Slsk.FileAttributeType.BitRate, tags.Properties.AudioBitrate));
                                        attributes.Add(new(Slsk.FileAttributeType.SampleRate, tags.Properties.AudioSampleRate));
                                        attributes.Add(new(Slsk.FileAttributeType.Length, (int)tags.Properties.Duration.TotalSeconds));
                                    }
                                    catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { }
                                }
                                files.Add(new(remote, local, root, info.Length, new Slsk.File(1, remote, info.Length, info.Extension.TrimStart('.'), attributes)));
                                Status = L.F("공유 파일 {0:N0}개 스캔 중…", files.Count);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
                }
                Status = L.F("공유 파일 {0:N0}개 · 읽지 못한 항목 {1:N0}개", files.Count, errors);
                return files.ToArray();
            }, ct);
            if (generation == Interlocked.Read(ref _generation)) Volatile.Write(ref _files, result);
        }
        finally { _scan.Release(); }
    }

    public static bool IsSafe(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        for (var p = full; p != null; p = Path.GetDirectoryName(p))
        {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    public Stream OpenRead(string remote, long offset)
    {
        var file = Files.SingleOrDefault(f => f.Remote == remote) ?? throw new FileNotFoundException("File not shared");
        if (!IsSafe(file.Root, file.Local)) throw new UnauthorizedAccessException("File not shared");
        var stream = new FileStream(file.Local, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != file.Size || offset < 0 || offset > stream.Length) { stream.Dispose(); throw new IOException("Shared file changed"); }
        stream.Position = offset;
        return stream;
    }

    public Slsk.BrowseResponse Browse() => new(Files.GroupBy(f => f.Remote[..f.Remote.LastIndexOf('\\')]).Select(g =>
        new Slsk.Directory(g.Key, g.Select(f => new Slsk.File(1, Path.GetFileName(f.Local), f.Size, f.Info.Extension, f.Info.Attributes)))));

    public IEnumerable<Slsk.File> Search(Slsk.SearchQuery query, IReadOnlyList<string> excludedPhrases) =>
        Files.Where(f => query.Terms.All(t => f.Remote.Contains(t, StringComparison.OrdinalIgnoreCase))
            && !query.Exclusions.Any(t => f.Remote.Contains(t, StringComparison.OrdinalIgnoreCase))
            && !excludedPhrases.Any(t => f.Remote.Contains(t, StringComparison.OrdinalIgnoreCase)))
        .Take(500).Select(f => f.Info);
}
