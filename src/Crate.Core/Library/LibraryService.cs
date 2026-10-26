namespace Crate.Core.Library;

/// <summary>설정에 지정된 iTunes/Winamp 소스를 읽어 메모리에 들고 있는다.</summary>
public sealed class LibraryService(AppSettings settings)
{
    private IReadOnlyList<TrackRecord>? _tracks;
    private IReadOnlyList<Playlist> _playlists = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettings Settings { get; } = settings;
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public async Task<IReadOnlyList<TrackRecord>> GetTracksAsync(CancellationToken ct = default)
    {
        if (_tracks != null) return _tracks;
        await _lock.WaitAsync(ct);
        try
        {
            return _tracks ??= await Task.Run(Load, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        await GetTracksAsync(ct);
        return _playlists;
    }

    public void Invalidate() => _tracks = null;

    private IReadOnlyList<TrackRecord> Load()
    {
        var list = new List<TrackRecord>();
        var warnings = new List<string>();
        IReadOnlyList<ITunesPlaylistRaw> rawPlaylists = [];

        if (Settings.ITunesLibraryPath is { } xml)
        {
            if (File.Exists(xml))
            {
                var lib = ITunesLibraryParser.ParseLibrary(xml);
                list.AddRange(lib.Tracks);
                rawPlaylists = lib.Playlists;
            }
            else warnings.Add($"iTunes 라이브러리 파일이 없습니다: {xml}");
        }
        foreach (var folder in Settings.PlaylistFolders)
        {
            if (Directory.Exists(folder)) list.AddRange(PlaylistParser.ParseFolder(folder));
            else warnings.Add($"플레이리스트 폴더가 없습니다: {folder}");
        }
        if (list.Count == 0 && warnings.Count == 0)
            warnings.Add("설정된 라이브러리가 없습니다. iTunes XML 경로나 플레이리스트 폴더를 지정하세요.");

        Warnings = warnings;
        var (merged, byOriginal) = Dedupe(list);

        // 플레이리스트의 Track ID를 합쳐진 곡으로 다시 연결
        var byId = list.Where(t => t.ITunesTrackId != null).ToDictionary(t => t.ITunesTrackId!.Value);
        _playlists = rawPlaylists
            .Select(p => new Playlist(p.Name, p.PersistentId, p.ParentPersistentId, p.Kind,
                [.. p.TrackIds.Select(id => byId.GetValueOrDefault(id)).OfType<TrackRecord>().Select(t => byOriginal[t]).Distinct()]))
            .ToList();
        return merged;
    }

    /// <summary>같은 곡이 iTunes와 Winamp 양쪽에 있으면 하나로 합친다. 원본 → 합쳐진 곡 매핑도 돌려준다.</summary>
    private static (List<TrackRecord> Merged, Dictionary<TrackRecord, TrackRecord> ByOriginal) Dedupe(List<TrackRecord> tracks)
    {
        var merged = new List<TrackRecord>();
        var map = new Dictionary<TrackRecord, TrackRecord>(ReferenceEqualityComparer.Instance);
        foreach (var g in tracks.GroupBy(t => (Norm(t.Artist), Norm(t.Title), Norm(t.Album))))
        {
            var m = g.Count() == 1 ? g.First() : g.First() with
            {
                PlayCount = g.Sum(t => t.PlayCount),
                Loved = g.Any(t => t.Loved),
                LastPlayed = g.Max(t => t.LastPlayed),
                DateAdded = g.Min(t => t.DateAdded),
                Location = g.Select(t => t.Location).FirstOrDefault(File.Exists) ?? g.First().Location
            };
            merged.Add(m);
            foreach (var t in g) map[t] = m;
        }
        return (merged, map);
    }

    internal static string Norm(string s) => s.Trim().ToLowerInvariant();
}
