using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>하트를 누른 곡 (Deezer 곡 ID 기준)</summary>
public sealed record LikedTrack(long TrackId, long AlbumId, string Artist, string Title, DateTime At);

/// <summary>
/// 곡 하트: %LOCALAPPDATA%\RecipeSong\liked-tracks.json.
/// 소리 분석 서비스가 그 곡의 미리듣기를 들어 취향 학습("좋아요와 닮은 신보" 가산점)에 쓴다.
/// </summary>
public sealed class LikedTracks
{
    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "liked-tracks.json");
    private readonly List<LikedTrack> _items;

    public LikedTracks()
    {
        try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<LikedTrack>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (JsonException) { _items = []; }
    }

    public IReadOnlyList<LikedTrack> All => _items;
    public bool Contains(long trackId) => trackId != 0 && _items.Any(t => t.TrackId == trackId);
    public int CountInAlbum(long albumId) => _items.Count(t => t.AlbumId == albumId);

    /// <summary>하트 ↔ 해제. 하트가 됐으면 true</summary>
    public bool Toggle(LikedTrack track)
    {
        bool liked = _items.RemoveAll(t => t.TrackId == track.TrackId) == 0;
        if (liked) _items.Add(track);
        File.WriteAllText(_file, JsonSerializer.Serialize(_items));
        return liked;
    }
}
