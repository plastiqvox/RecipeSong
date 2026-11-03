using System.Text.Json;

namespace RecipeSong.Core;

public sealed record DislikedTrack(long TrackId, long AlbumId, string Artist, string Title, DateTime At);

/// <summary>명시적인 곡별 ‘별로’만 저장한다. 미청취·ZIP 보유에서 만들지 않는다.</summary>
public sealed class DislikedTracks
{
    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "disliked-tracks.json");
    private readonly List<DislikedTrack> _items;
    public DislikedTracks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<DislikedTrack>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (JsonException) { _items = []; }
    }
    public IReadOnlyList<DislikedTrack> All => _items;
    public bool Contains(long id) => id != 0 && _items.Any(t => t.TrackId == id);
    public bool Toggle(DislikedTrack track)
    {
        if (track.TrackId <= 0) return false;
        bool dislike = _items.RemoveAll(t => t.TrackId == track.TrackId) == 0;
        if (dislike) _items.Add(track);
        Save();
        return dislike;
    }
    public void Remove(long id) { if (_items.RemoveAll(t => t.TrackId == id) > 0) Save(); }
    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_items));
}
