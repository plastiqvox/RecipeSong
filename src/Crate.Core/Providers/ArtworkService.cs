namespace Crate.Core.Providers;

/// <summary>
/// 내 라이브러리 앨범의 아트. 음원 내장 커버 → Deezer → iTunes Search 순으로 찾고 디스크에 캐시한다.
/// 내 앨범의 '샘플'은 로컬 파일을 그대로 재생하므로 미리듣기 URL은 다루지 않는다.
/// </summary>
public sealed class ArtworkService(DeezerClient deezer, ITunesSearchClient itunes)
{
    private readonly JsonCache<AlbumArt> _cache = new("album-art", TimeSpan.FromDays(180));
    private readonly string _artDir = Directory.CreateDirectory(Path.Combine(JsonCache<AlbumArt>.Dir, "art")).FullName;

    public async Task<AlbumArt> GetAsync(AlbumSummary album, CancellationToken ct = default)
    {
        var key = $"{album.Artist}\u001f{album.Album}".ToLowerInvariant();
        if (_cache.TryGet(key, out var hit)) return hit;

        var art = TryEmbedded(album, key);
        if (art == null)
        {
            var dz = string.IsNullOrWhiteSpace(album.Album) ? null : await deezer.FindAlbumAsync(album.Artist, album.Album, ct);
            art = dz?.Cover != null
                ? new AlbumArt(dz.Cover, dz.Id)
                : await itunes.FindAlbumAsync(album.Artist, album.Album, ct) is { ArtworkUrl: { } u } ? new AlbumArt(u) : new AlbumArt(null);
        }
        _cache.Set(key, art);
        return art;
    }

    public void Save() => _cache.Save();

    /// <summary>음원 파일에 내장된 커버를 꺼내 캐시 폴더에 저장한다.</summary>
    private AlbumArt? TryEmbedded(AlbumSummary album, string key)
    {
        if (album.SampleTrackPath == null) return null;
        try
        {
            using var f = TagLib.File.Create(album.SampleTrackPath);
            var pic = f.Tag.Pictures.FirstOrDefault();
            if (pic == null) return null;
            var ext = pic.MimeType?.Contains("png") == true ? ".png" : ".jpg";
            var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            var path = Path.Combine(_artDir, name + ext);
            if (!File.Exists(path)) File.WriteAllBytes(path, pic.Data.Data);
            return new AlbumArt(new Uri(path).AbsoluteUri);
        }
        catch (Exception) { return null; }
    }
}
