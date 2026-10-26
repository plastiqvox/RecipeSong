using System.Text.Json;

namespace Crate.Core;

public sealed record SavedRelease(ReleaseRecommendation Rec, DateTime SavedAt);

/// <summary>나중에 디깅하려고 저장한 발매작. %LOCALAPPDATA%\Crate\saved.json (캐시와 달리 만료 없음).</summary>
public sealed class SavedReleases
{
    private readonly string _file;
    private readonly List<SavedRelease> _items;

    public SavedReleases(string? file = null)
    {
        _file = file ?? Path.Combine(JsonCache<object>.Dir, "saved.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        try
        {
            _items = File.Exists(_file)
                ? JsonSerializer.Deserialize<List<SavedRelease>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch (JsonException)
        {
            // 깨진 파일은 지우지 않고 옆에 남겨 둔다
            File.Copy(_file, _file + ".broken", overwrite: true);
            _items = [];
        }
    }

    /// <summary>최근에 저장한 순</summary>
    public IReadOnlyList<SavedRelease> All => [.. _items.OrderByDescending(s => s.SavedAt)];

    public bool Contains(long deezerAlbumId) => _items.Any(s => s.Rec.DeezerAlbumId == deezerAlbumId);

    public void Add(ReleaseRecommendation rec)
    {
        if (Contains(rec.DeezerAlbumId)) return;
        _items.Add(new SavedRelease(rec, DateTime.Now));
        Save();
    }

    public void Remove(long deezerAlbumId)
    {
        if (_items.RemoveAll(s => s.Rec.DeezerAlbumId == deezerAlbumId) > 0) Save();
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
        File.Move(tmp, _file, overwrite: true);
    }
}
