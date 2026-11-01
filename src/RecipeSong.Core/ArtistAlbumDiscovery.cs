using System.Text.RegularExpressions;
using System.ComponentModel;
using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

public enum CatalogOwnership { Missing, Partial, PossibleEdition, InLibrary }

public sealed class ArtistAlbumThumbnail(AlbumSummary album) : INotifyPropertyChanged
{
    public AlbumSummary Album { get; } = album;
    private string? _coverUrl;
    public string? CoverUrl
    {
        get => _coverUrl;
        set { if (_coverUrl == value) return; _coverUrl = value; PropertyChanged?.Invoke(this, new(nameof(CoverUrl))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string CoverDescription => L.F("가장 많이 들은 앨범: {0} · 재생 {1:N0}회", Album.Album, Album.PlayCount);
}

public sealed class PlaylistMusician(string name, IReadOnlyList<TrackRecord> tracks, long deezerId = 0, bool followed = false)
{
    public string Name { get; } = name;
    public IReadOnlyList<TrackRecord> Tracks { get; } = tracks;
    public long DeezerId { get; } = deezerId;
    public bool Followed { get; } = followed;
    public IReadOnlyList<ArtistAlbumThumbnail> TopAlbums { get; init; } = [];
    public int AlbumCount => Tracks.Where(t => t.Album.Length > 0).Select(t => ListeningAnalyzer.NormalizeArtist(t.Album)).Distinct().Count();
    public double TasteScore => Tracks.Sum(t => t.Weight);
    public int LovedCount => Tracks.Count(t => t.Loved && !t.Disliked);
    public long PlayCount => Tracks.Sum(t => (long)Math.Max(0, t.PlayCount));
    public string Summary => L.F("플리 {0}곡 · {1}개 앨범", Tracks.Count, AlbumCount)
        + Environment.NewLine + L.F("좋아요 {0}곡 · 재생 {1:N0}회", LovedCount, PlayCount)
        + (Followed ? " · " + L.T("팔로우") : "");
}

public sealed record DiscoveredAlbum(DzAlbum Album, string Artist, CatalogOwnership Ownership, int OwnedTracks)
{
    public string Title => Album.Title;
    public string Summary => $"{Album.ReleaseDate ?? "?"} · {Album.RecordType ?? "?"} · " + (Ownership switch
    {
        CatalogOwnership.Partial => L.F("일부 보유 · {0}/{1}곡", OwnedTracks, Album.TrackCount),
        CatalogOwnership.PossibleEdition => L.T("보유 가능성 있음 · 다른 판본"),
        CatalogOwnership.InLibrary => L.T("라이브러리에 있음"),
        _ => L.T("미보유"),
    });
}

/// <summary>플리는 취향 아티스트의 출처다. 보유 판정에는 플리가 아닌 전체 라이브러리를 사용한다.</summary>
public sealed partial class ArtistAlbumDiscovery(DeezerClient client, IReadOnlyList<TrackRecord> tracks,
    IReadOnlyList<Playlist> playlists, IReadOnlyList<FollowList.Followed> follows,
    Func<AlbumSummary, CancellationToken, Task<string?>>? artwork = null)
{
    public IReadOnlyList<Playlist> Playlists { get; } = playlists.Where(p => p.Kind == PlaylistKind.User).ToArray();
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<AlbumSummary>>> _topAlbums = new(() => TopAlbums(tracks));

    private static IReadOnlyDictionary<string, IReadOnlyList<AlbumSummary>> TopAlbums(IReadOnlyList<TrackRecord> library) =>
        library.Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .DistinctBy(t => (Key(Who(t)), Key(t.Album), Key(t.Title)))
            .GroupBy(t => Key(Who(t))).ToDictionary(artist => artist.Key, artist =>
                (IReadOnlyList<AlbumSummary>)artist.GroupBy(t => Key(t.Album))
                    .OrderByDescending(g => g.Sum(t => (long)Math.Max(0, t.PlayCount)))
                    .ThenByDescending(g => g.Count()).ThenBy(g => g.First().Album, StringComparer.CurrentCultureIgnoreCase).Take(3).Select(album =>
            {
                var sample = album.FirstOrDefault(t => t.Location != null && File.Exists(t.Location)) ?? album.First();
                return new AlbumSummary(Who(sample), sample.Album, sample.Year,
                    (int)Math.Min(int.MaxValue, album.Sum(t => (long)Math.Max(0, t.PlayCount))), album.Count(), sample.Location, sample.Title);
            }).ToArray());

    public Task<string?> ArtistCoverAsync(AlbumSummary album, CancellationToken ct) => artwork != null
        ? artwork(album, ct) : Task.FromResult<string?>(null);

    public IReadOnlyList<PlaylistMusician> Musicians(Playlist? playlist = null, bool includeFollows = true)
    {
        var groups = (playlist == null ? Playlists.SelectMany(p => p.Tracks) : playlist.Tracks)
            .DistinctBy(t => (Key(Who(t)), Key(t.Album), Key(t.Title)))
            .Where(t => Key(Who(t)) is not ("" or "unknownartist" or "variousartists" or "va"))
            .GroupBy(t => Key(Who(t)))
            .Select(g => new PlaylistMusician(Who(g.First()), g.ToArray())).ToDictionary(g => Key(g.Name));
        if (includeFollows)
            foreach (var followed in follows)
            {
                var key = Key(followed.Name);
                groups[key] = groups.TryGetValue(key, out var group)
                    ? new(group.Name, group.Tracks, followed.Id, true)
                    : new(followed.Name, [], followed.Id, true);
            }
        return [.. groups.Values.Select(g => new PlaylistMusician(g.Name, g.Tracks, g.DeezerId, g.Followed)
            { TopAlbums = (_topAlbums.Value.GetValueOrDefault(Key(g.Name)) ?? []).Select(album => new ArtistAlbumThumbnail(album)).ToArray() })
            .OrderByDescending(g => g.TasteScore).ThenByDescending(g => g.LovedCount)
            .ThenByDescending(g => g.PlayCount).ThenByDescending(g => g.Tracks.Count)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private readonly Dictionary<long, IReadOnlyList<DzAlbum>> _identityDiscographies = [];
    private async Task<IReadOnlyList<DzAlbum>> IdentityDiscographyAsync(long artistId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_identityDiscographies.TryGetValue(artistId, out var cached)) return cached;
        var albums = await client.ArtistAlbumsAllAsync(artistId, ct);
        ct.ThrowIfCancellationRequested();
        _identityDiscographies[artistId] = albums;
        return albums;
    }

    public CatalogOwnership Ownership(string artist, DzAlbum album, out int ownedTracks)
    {
        var mine = tracks.Where(t => Key(Who(t)) == Key(artist)).ToArray();
        ownedTracks = mine.Where(t => Key(t.Album) == Key(album.Title)).Select(t => Key(t.Title)).Distinct().Count();
        if (ownedTracks > 0)
            return album.TrackCount is > 0 && ownedTracks < album.TrackCount ? CatalogOwnership.Partial : CatalogOwnership.InLibrary;
        return mine.Any(t => EditionKey(t.Album) == EditionKey(album.Title)) ? CatalogOwnership.PossibleEdition : CatalogOwnership.Missing;
    }

    public async Task<IReadOnlyList<DiscoveredAlbum>> AlbumsAsync(DzArtist artist, CancellationToken ct, string? libraryArtist = null)
    {
        var result = new List<DiscoveredAlbum>();
        foreach (var item in await IdentityDiscographyAsync(artist.Id, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Released is { } date && date > DateOnly.FromDateTime(DateTime.Today)) continue;
            var album = item;
            var state = Ownership(libraryArtist ?? artist.Name, album, out _);
            if (state == CatalogOwnership.InLibrary && album.TrackCount is not > 0)
            {
                album = await client.AlbumAsync(item.Id, ct) ?? throw new HttpRequestException(L.T("앨범 정보를 불러오지 못했어요."));
                if (album.TrackCount is not > 0) throw new HttpRequestException(L.T("앨범 곡수를 확인하지 못했어요."));
            }
            state = Ownership(libraryArtist ?? artist.Name, album, out var count);
            if (state != CatalogOwnership.InLibrary) result.Add(new(album, artist.Name, state, count));
        }
        return [.. result.OrderByDescending(a => a.Album.Released).ThenBy(a => a.Title)];
    }

    public async Task<(ReleaseRecommendation Recommendation, IReadOnlyList<DzTrack> Tracks)> DetailsAsync(DiscoveredAlbum album, CancellationToken ct)
    {
        var detail = await client.AlbumAsync(album.Album.Id, ct);
        if (detail?.Released == null) throw new HttpRequestException(L.T("앨범 정보를 불러오지 못했어요."));
        var songs = await client.AlbumTracksAllAsync(detail.Id, ct);
        if (detail.TrackCount is > 0 && songs.Count != detail.TrackCount)
            throw new HttpRequestException(L.T("수록곡 전체를 불러오지 못했어요. 다시 선택해 주세요."));
        var recommendation = new ReleaseRecommendation(album.Artist, detail.Title, detail.RecordType ?? "album", detail.Released.Value,
            detail.Label, detail.Cover, detail.Id, detail.Link, null, Region.Unknown, null, null, null, [], [], true, 0);
        return (recommendation, songs);
    }

    public static bool MatchesType(DzAlbum album, bool albums, bool eps, bool singles, bool live, bool compilations)
    {
        if (AlbumVariants.IsLive(album.Title) && !live) return false;
        if (IsCompilation(album.Title) || album.RecordType == "compile") return compilations;
        return album.RecordType switch { "album" => albums, "ep" => eps, "single" => singles, _ => false };
    }

    private static string Key(string text) => ListeningAnalyzer.NormalizeArtist(text);
    private static string Who(TrackRecord track) => Key(track.GroupArtist) is "variousartists" or "va" or "unknownartist"
        ? track.Artist : track.GroupArtist;
    private static string EditionKey(string title) => Key(EditionSuffix().Replace(title, "").Trim());
    [GeneratedRegex(@"\s*(?:[\(\[][^)\]]*\b(?:deluxe|remaster(?:ed)?|expanded|anniversary|bonus|mono|stereo)\b[^)\]]*[\)\]]|[-–—]\s*(?:(?:19|20)\d{2}\s+)?(?:deluxe|remaster(?:ed)?|expanded|anniversary|bonus)\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffix();
    private static bool IsCompilation(string title) => Regex.IsMatch(title, @"\b(?:greatest hits|best of|compilation|anthology)\b|베스트|컴필레이션", RegexOptions.IgnoreCase);
}
