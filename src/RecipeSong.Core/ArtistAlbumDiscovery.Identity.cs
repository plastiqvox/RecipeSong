using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

public sealed record ArtistIdentityMatch(DzArtist Artist, int AlbumMatches, int? TrackMatches, bool Verified);
public sealed record ArtistIdentityResult(IReadOnlyList<ArtistIdentityMatch> Candidates, long? SelectedId);

public sealed partial class ArtistAlbumDiscovery
{
    private readonly Dictionary<string, ArtistIdentityResult> _identityResults = [];
    public async Task<ArtistIdentityResult> ResolveArtistAsync(PlaylistMusician musician, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cacheKey = Key(musician.Name) + "\u001f" + musician.DeezerId;
        if (_identityResults.TryGetValue(cacheKey, out var cached)) return cached;
        var owned = tracks.Where(t => Key(Who(t)) == Key(musician.Name) && !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => EditionKey(t.Album)).Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Sum(t => (long)Math.Max(0, t.PlayCount))).ThenBy(g => g.Key).ToArray();
        ct.ThrowIfCancellationRequested();
        if (owned.Length == 0 && musician.DeezerId > 0)
            return new([new(new(musician.DeezerId, musician.Name, null, null), 0, 0, false)], musician.DeezerId);
        var artists = (await client.SearchArtistsAsync(musician.Name, ct)).Where(a => a.Id > 0).DistinctBy(a => a.Id).ToList();
        if (musician.DeezerId > 0 && artists.All(a => a.Id != musician.DeezerId))
            artists.Add(new(musician.DeezerId, musician.Name, null, null));
        // 이름 검색에서 빠진 ID도 실제 보유 앨범의 검색 결과에서 보충한다.
        foreach (var album in owned.Take(3))
            foreach (var hit in await client.SearchAlbumsAsync($"{musician.Name} {album.First().Album}", 10, ct))
                if (hit.Artist is { Id: > 0 } artist && Key(artist.Name) == Key(musician.Name)
                    && EditionKey(hit.Title) == album.Key && artists.All(a => a.Id != artist.Id)) artists.Add(artist);

        var matches = new List<ArtistIdentityMatch>();
        var complete = true;
        foreach (var artist in artists)
        {
            ct.ThrowIfCancellationRequested();
            var albumMatches = 0;
            var strongAlbums = 0;
            var strongTrackAlbum = false;
            var songMatches = new HashSet<string>();
            try
            {
                var catalog = await IdentityDiscographyAsync(artist.Id, ct);
                var matched = owned.Select(local => (Local: local, Album: catalog.FirstOrDefault(a => EditionKey(a.Title) == local.Key)))
                    .Where(pair => pair.Album != null).ToArray();
                albumMatches = matched.Length;
                strongAlbums = matched.Count(pair => IsIdentityTitle(pair.Local.Key) && pair.Local.Key != Key(musician.Name));
                if (strongAlbums < 2)
                    foreach (var pair in matched.Take(3))
                    {
                        var names = pair.Local.Select(t => Key(t.Title)).Where(IsIdentityTitle).ToHashSet();
                        var matchingSongs = new HashSet<string>();
                        foreach (var song in await client.AlbumTracksAllAsync(pair.Album!.Id, ct))
                            if (names.Contains(Key(song.Title))) matchingSongs.Add(Key(song.Title));
                        foreach (var name in matchingSongs) songMatches.Add(pair.Local.Key + "\u001f" + name);
                        strongTrackAlbum |= matchingSongs.Count >= 2;
                    }
                matches.Add(new(artist, albumMatches, strongAlbums >= 2 ? null : songMatches.Count, strongAlbums >= 2 || strongTrackAlbum));
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                complete = false;
                matches.Add(new(artist, albumMatches, songMatches.Count, false));
            }
        }
        ct.ThrowIfCancellationRequested();
        var verified = matches.Where(m => m.Verified).ToArray();
        long? selected = complete && verified.Length == 1 ? verified[0].Artist.Id : null;
        var result = new ArtistIdentityResult(matches.OrderByDescending(m => m.Verified).ThenByDescending(m => m.AlbumMatches)
            .ThenByDescending(m => m.TrackMatches).ThenBy(m => m.Artist.Id).ToArray(), selected);
        if (complete) _identityResults[cacheKey] = result;
        return result;
    }

    private static bool IsIdentityTitle(string title) => title.Length >= 4 && title is not
        ("intro" or "outro" or "interlude" or "untitled" or "greatesthits" or "bestof" or "collection" or "live" or "singles" or "album");
}
