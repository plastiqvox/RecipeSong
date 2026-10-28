using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>
/// 차단 사유 (여러 개 고를 수 있다). 소리 때문인 사유(Sound)가 하나라도 있으면 그 앨범을 '별로'로 학습한다.
/// SameName이 있으면 이름 대신 그 Deezer 아티스트 id만 막는다 (동명이인 + 취향 아님 → 그 사람만 막고 소리는 학습).
/// </summary>
[Flags]
public enum BlockReason
{
    // 소리 때문 (학습)
    Taste = 1, Vocal = 16, Genre = 32, Production = 64,
    // 그 밖 (학습 안 함)
    SameName = 2, Known = 4, Other = 8, Mainstream = 128, TooMany = 256, Reissues = 512, Language = 1024, Personal = 2048,
}

public static class BlockReasons
{
    /// <summary>학습에 쓰는 사유</summary>
    public const BlockReason Sound = BlockReason.Taste | BlockReason.Vocal | BlockReason.Genre | BlockReason.Production;

    public static bool Learns(this BlockReason r) => (r & Sound) != 0;
}

/// <summary>
/// 차단 목록: %LOCALAPPDATA%\RecipeSong\blocked.json. 차단한 아티스트·레이블의 신보는 어디서도 추천하지 않는다
/// (내 플리 뮤지션·AI 제안 포함). '별로'가 앨범 하나라면 차단은 그 아티스트·레이블 전체.
/// 사유가 '취향이 아님'이면 차단을 누른 카드의 앨범을 '별로'처럼 학습한다 (그 소리와 닮은 신보도 덜 추천).
/// '동명이인'은 이름이 아니라 그 Deezer 아티스트 id만 막는다 (진짜 그 사람의 신보는 그대로 나온다).
/// </summary>
public sealed class BlockList
{
    public sealed record Data(List<string> Artists, List<string> Labels, List<BlockedAlbum>? Albums = null,
        List<BlockedArtistId>? SameName = null);
    /// <summary>Learn: '별로'로 학습할지 (사유가 취향일 때만)</summary>
    public sealed record BlockedAlbum(long Id, string Artist, string? Label, bool Learn = true, BlockReason Reasons = 0);
    /// <summary>동명이인으로 막은 Deezer 아티스트. ArtistId를 모르는 예전 결과는 AlbumId로 그 앨범만 숨긴다.</summary>
    public sealed record BlockedArtistId(long ArtistId, string Name, long AlbumId, string Album, bool Learn = false, BlockReason Reasons = 0);

    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "blocked.json");
    private Data _data;

    public BlockList()
    {
        try { _data = File.Exists(_file) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new([], []) : new([], []); }
        catch (JsonException) { _data = new([], []); }
    }

    public IReadOnlyList<string> Artists => _data.Artists;
    public IReadOnlyList<string> Labels => _data.Labels;
    public IReadOnlyList<BlockedArtistId> SameName => _data.SameName ?? [];

    /// <summary>학습용 '별로' 신호: 취향 때문에 차단할 때 보고 있던 앨범</summary>
    public IEnumerable<long> AlbumIds =>
        (_data.Albums ?? []).Where(a => a.Learn).Select(a => a.Id).Concat(SameName.Where(s => s.Learn).Select(s => s.AlbumId));

    public IReadOnlySet<string> ArtistKeys => _data.Artists.Select(ListeningAnalyzer.NormalizeArtist).ToHashSet();
    public IReadOnlySet<string> LabelKeys => _data.Labels.SelectMany(l => LabelAffinity.Keys(l)).ToHashSet();
    public IReadOnlySet<long> ArtistIds => SameName.Where(s => s.ArtistId != 0).Select(s => s.ArtistId).ToHashSet();

    public bool IsBlocked(string artist, string? label) =>
        ArtistKeys.Contains(ListeningAnalyzer.NormalizeArtist(artist)) || LabelAffinity.Keys(label).Any(LabelKeys.Contains);

    public bool IsBlocked(ReleaseRecommendation r) =>
        IsBlocked(r.Artist, r.Label) || SameName.Any(s => (r.ArtistId != 0 && s.ArtistId == r.ArtistId) || s.AlbumId == r.DeezerAlbumId);

    public void BlockArtist(ReleaseRecommendation r, BlockReason reason)
    {
        bool learn = reason.Learns();
        if (reason.HasFlag(BlockReason.SameName))
        {
            var list = _data.SameName ?? [];
            if (list.All(s => s.AlbumId != r.DeezerAlbumId)) list.Add(new(r.ArtistId, r.Artist, r.DeezerAlbumId, r.Album, learn, reason));
            _data = _data with { SameName = list };
        }
        else
        {
            if (!_data.Artists.Contains(r.Artist)) _data.Artists.Add(r.Artist);
            AddAlbum(r, reason);
        }
        Save();
    }

    public void BlockLabel(ReleaseRecommendation r, BlockReason reason)
    {
        if (r.Label is not { Length: > 0 } label) return;
        if (!_data.Labels.Contains(label)) _data.Labels.Add(label);
        AddAlbum(r, reason);
        Save();
    }

    public void UnblockArtist(string artist) { if (_data.Artists.Remove(artist)) { DropUnblocked(); Save(); } }
    public void UnblockLabel(string label) { if (_data.Labels.Remove(label)) { DropUnblocked(); Save(); } }
    public void UnblockSameName(BlockedArtistId s) { if (_data.SameName?.Remove(s) == true) Save(); }

    /// <summary>되돌리기용: 차단하기 전 상태를 글로 떠 두고 그대로 되돌린다 (목록이 가변이라 깊은 복사)</summary>
    public string Snapshot() => JsonSerializer.Serialize(_data);
    public void Restore(string snapshot) { _data = JsonSerializer.Deserialize<Data>(snapshot) ?? new([], []); Save(); }

    private void AddAlbum(ReleaseRecommendation r, BlockReason reason)
    {
        var albums = _data.Albums ?? [];
        var i = albums.FindIndex(a => a.Id == r.DeezerAlbumId);
        if (i >= 0) albums[i] = albums[i] with { Learn = albums[i].Learn || reason.Learns(), Reasons = albums[i].Reasons | reason };
        else albums.Add(new(r.DeezerAlbumId, r.Artist, r.Label, reason.Learns(), reason));
        _data = _data with { Albums = albums };
    }

    /// <summary>해제하면 그 차단으로 남긴 학습 신호도 뺀다 (아직 다른 차단에 걸린 앨범은 둔다)</summary>
    private void DropUnblocked() => _data.Albums?.RemoveAll(a => !IsBlocked(a.Artist, a.Label));

    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_data));
}
