using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>
/// 차단 목록: %LOCALAPPDATA%\RecipeSong\blocked.json. 차단한 아티스트·레이블의 신보는 어디서도 추천하지 않는다
/// (내 플리 뮤지션·AI 제안 포함). '별로'가 앨범 하나라면 차단은 그 아티스트·레이블 전체.
/// 차단을 누른 카드의 앨범은 Albums에 남겨 '별로'처럼 학습한다 (그 소리와 닮은 신보도 덜 추천).
/// </summary>
public sealed class BlockList
{
    public sealed record Data(List<string> Artists, List<string> Labels, List<BlockedAlbum>? Albums = null);
    public sealed record BlockedAlbum(long Id, string Artist, string? Label);

    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "blocked.json");
    private Data _data;

    public BlockList()
    {
        try { _data = File.Exists(_file) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new([], []) : new([], []); }
        catch (JsonException) { _data = new([], []); }
    }

    public IReadOnlyList<string> Artists => _data.Artists;
    public IReadOnlyList<string> Labels => _data.Labels;

    /// <summary>차단할 때 보고 있던 앨범 (학습용 '별로' 신호)</summary>
    public IEnumerable<long> AlbumIds => (_data.Albums ?? []).Select(a => a.Id);

    public IReadOnlySet<string> ArtistKeys => _data.Artists.Select(ListeningAnalyzer.NormalizeArtist).ToHashSet();
    public IReadOnlySet<string> LabelKeys => _data.Labels.SelectMany(l => LabelAffinity.Keys(l)).ToHashSet();

    public bool IsBlocked(string artist, string? label) =>
        ArtistKeys.Contains(ListeningAnalyzer.NormalizeArtist(artist)) || LabelAffinity.Keys(label).Any(LabelKeys.Contains);

    public void BlockArtist(string artist, long albumId = 0, string? label = null)
    {
        if (!_data.Artists.Contains(artist)) _data.Artists.Add(artist);
        AddAlbum(albumId, artist, label);
        Save();
    }

    public void BlockLabel(string label, long albumId = 0, string artist = "")
    {
        if (!_data.Labels.Contains(label)) _data.Labels.Add(label);
        AddAlbum(albumId, artist, label);
        Save();
    }

    public void UnblockArtist(string artist) { if (_data.Artists.Remove(artist)) { DropUnblocked(); Save(); } }
    public void UnblockLabel(string label) { if (_data.Labels.Remove(label)) { DropUnblocked(); Save(); } }

    private void AddAlbum(long id, string artist, string? label)
    {
        var albums = _data.Albums ?? [];
        if (id != 0 && albums.All(a => a.Id != id)) albums.Add(new(id, artist, label));
        if (_data.Albums == null && albums.Count > 0) _data = _data with { Albums = albums };
    }

    /// <summary>해제하면 그 차단으로 남긴 학습 신호도 뺀다 (아직 다른 차단에 걸린 앨범은 둔다)</summary>
    private void DropUnblocked() => _data.Albums?.RemoveAll(a => !IsBlocked(a.Artist, a.Label));

    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_data));
}
