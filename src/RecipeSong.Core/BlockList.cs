using System.Text.Json;

namespace RecipeSong.Core;

/// <summary>
/// 차단 목록: %LOCALAPPDATA%\RecipeSong\blocked.json. 차단한 아티스트·레이블의 신보는 어디서도 추천하지 않는다
/// (내 플리 뮤지션·AI 제안 포함). '별로'가 앨범 하나라면 차단은 그 아티스트·레이블 전체.
/// </summary>
public sealed class BlockList
{
    public sealed record Data(List<string> Artists, List<string> Labels);

    private readonly string _file = Path.Combine(JsonCache<object>.Dir, "blocked.json");
    private readonly Data _data;

    public BlockList()
    {
        try { _data = File.Exists(_file) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new([], []) : new([], []); }
        catch (JsonException) { _data = new([], []); }
    }

    public IReadOnlyList<string> Artists => _data.Artists;
    public IReadOnlyList<string> Labels => _data.Labels;

    public IReadOnlySet<string> ArtistKeys => _data.Artists.Select(ListeningAnalyzer.NormalizeArtist).ToHashSet();
    public IReadOnlySet<string> LabelKeys => _data.Labels.SelectMany(l => LabelAffinity.Keys(l)).ToHashSet();

    public bool IsBlocked(string artist, string? label) =>
        ArtistKeys.Contains(ListeningAnalyzer.NormalizeArtist(artist)) || LabelAffinity.Keys(label).Any(LabelKeys.Contains);

    public void BlockArtist(string artist) { if (!_data.Artists.Contains(artist)) { _data.Artists.Add(artist); Save(); } }
    public void BlockLabel(string label) { if (!_data.Labels.Contains(label)) { _data.Labels.Add(label); Save(); } }
    public void UnblockArtist(string artist) { if (_data.Artists.Remove(artist)) Save(); }
    public void UnblockLabel(string label) { if (_data.Labels.Remove(label)) Save(); }

    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_data));
}
