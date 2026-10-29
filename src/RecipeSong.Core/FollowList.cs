using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>
/// 팔로우한 아티스트: %LOCALAPPDATA%\RecipeSong\followed.json. 신보는 내 라이브러리 뮤지션처럼 항상 맨 앞,
/// 비슷한 아티스트 찾기의 출발점(가중치 최대), 다른 앨범에 참여하면 가산점. 소리 학습에는 쓰지 않는다(앨범·곡 하트가 맡는다).
/// 동명이인을 피하려고 이름이 아니라 Deezer 아티스트 id로 팔로우한다.
/// </summary>
public sealed class FollowList
{
    /// <summary>추천의 FromPlaylist 자리에 넣는 값 (데이터 키 — 화면에서만 번역)</summary>
    public const string Tag = "팔로우";

    /// <summary>Region: 지역을 나누던 때 저장한 지역 이름 (지금은 "" 로 저장하고 쓰지 않는다)</summary>
    public sealed record Followed(long Id, string Name, string Region, DateTime At);

    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "followed.json");
    private readonly List<Followed> _items;

    public FollowList()
    {
        try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<Followed>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (JsonException) { _items = []; }
    }

    public IReadOnlyList<Followed> All => _items;
    public bool Contains(long id) => _items.Any(f => f.Id == id);

    /// <summary>엔진에 넘기는 모양: 라이브러리 뮤지션과 같은 자리 (Deezer id를 이미 안다)</summary>
    public IReadOnlyList<PlaylistArtist> AsPlaylistArtists() =>
        [.. _items.Select(f => new PlaylistArtist(f.Name, Tag, null, f.Region, 0, new HashSet<string>(), f.Id))];

    public void Follow(long id, string name, string region)
    {
        if (id == 0 || Contains(id)) return;
        _items.Add(new(id, name, region, DateTime.Now));
        Save();
    }

    public void Unfollow(long id)
    {
        if (_items.RemoveAll(f => f.Id == id) > 0) Save();
    }

    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_items));
}
