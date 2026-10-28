namespace RecipeSong.Core.Providers;

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

    /// <summary>
    /// 커버가 없는 추천 신보 (발매 전이라 Deezer 목록에 커버가 아직 없는 경우가 많다): Deezer 앨범을 다시 보고 → iTunes Search.
    /// 못 찾은 결과는 캐시하지 않는다 (발매일 즈음 커버가 올라오면 다음에 찾는다).
    /// </summary>
    public async Task<string?> FindReleaseCoverAsync(long deezerAlbumId, string artist, string album, CancellationToken ct = default)
    {
        var key = $"release\u001f{deezerAlbumId}";
        if (_cache.TryGet(key, out var hit)) return hit.ArtworkUrl;
        var url = (deezerAlbumId != 0 ? (await deezer.AlbumAsync(deezerAlbumId, ct))?.Cover : null)
                  ?? (await itunes.FindAlbumAsync(artist, album, ct))?.ArtworkUrl;
        if (url != null) _cache.Set(key, new AlbumArt(url, deezerAlbumId));
        return url;
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
