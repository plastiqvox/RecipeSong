using RecipeSong.Core.Providers;

namespace RecipeSong.Core;

public sealed record ArtistIdentityMatch(DzArtist Artist, int AlbumMatches, int? TrackMatches, bool Verified, IReadOnlyList<string>? Evidence = null);
public sealed record ArtistIdentityResult(IReadOnlyList<ArtistIdentityMatch> Candidates, long? SelectedId, bool FromStore = false);

public sealed partial class ArtistAlbumDiscovery
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ArtistIdentityResult> _identityResults = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _identityGenerations = [];
    private readonly Lazy<IReadOnlyDictionary<string, string>> _identityHashes = new(() => tracks.Concat(ArchiveEvidence(archives)).GroupBy(t => Key(Who(t)))
        .ToDictionary(g => g.Key, g => ArtistIdentityStore.Fingerprint(g.Select(t => System.Text.Json.JsonSerializer.Serialize(new[] { Key(t.Album), Key(t.Title) })))));
    public string? EvidenceHash(string artist) => _identityHashes.Value.GetValueOrDefault(Key(artist));
    public void ConfirmArtist(PlaylistMusician musician, DzArtist artist)
    {
        var match = _identityResults.Where(pair => pair.Key.StartsWith(Key(musician.Name) + "\u001f", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value.Candidates).FirstOrDefault(candidate => candidate.Artist.Id == artist.Id);
        var previous = _identities.Read(musician.Name);
        _identities.Save(new(musician.Name, artist.Name, artist.Id, ArtistIdentitySource.Manual, EvidenceHash(musician.Name) ?? "",
            match?.Evidence ?? (previous?.ArtistId == artist.Id ? previous.Evidence : []),
            match?.AlbumMatches ?? (previous?.ArtistId == artist.Id ? previous.AlbumMatches : 0),
            match?.TrackMatches ?? (previous?.ArtistId == artist.Id ? previous.TrackMatches : null), DateTimeOffset.UtcNow));
        _identityResults.Clear();
    }
    public void ForgetArtist(string artist)
    {
        _identities.Forget(artist); _identityResults.Clear(); _identityDiscographies.Clear();
    }
    public async Task<ArtistIdentityResult> ResolveArtistAsync(PlaylistMusician musician, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stored = _identities.FindValid(musician.Name, EvidenceHash(musician.Name));
        if (stored != null)
            return new([new(new(stored.ArtistId, stored.CatalogName, null, null), stored.AlbumMatches, stored.TrackMatches,
                stored.Source == ArtistIdentitySource.Automatic, stored.Evidence)], stored.ArtistId, true);
        var generation = _identities.Generation(musician.Name);
        var previousGeneration = _identityGenerations.GetOrAdd(Key(musician.Name), generation);
        if (previousGeneration != generation)
        {
            _identityGenerations[Key(musician.Name)] = generation;
            _identityDiscographies.Clear();
        }
        var cacheKey = Key(musician.Name) + "\u001f" + musician.DeezerId + "\u001f" + _identities.Revision;
        if (_identityResults.TryGetValue(cacheKey, out var cached)) return cached;
        var owned = tracks.Concat(ArchiveEvidence(archives)).Where(t => Key(Who(t)) == Key(musician.Name) && !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => EditionKey(t.Album)).Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Sum(t => (long)Math.Max(0, t.PlayCount))).ThenBy(g => g.Key).ToArray();
        ct.ThrowIfCancellationRequested();
        if (owned.Length == 0 && musician.DeezerId > 0)
            return new([new(new(musician.DeezerId, musician.Name, null, null), 0, 0, false)], musician.DeezerId);
        var artists = (await client.SearchArtistsAsync(musician.Name, ct)).Where(a => a.Id > 0).DistinctBy(a => a.Id).ToList();
        var hint = _identities.Read(musician.Name);
        if (hint != null && artists.All(a => a.Id != hint.ArtistId)) artists.Add(new(hint.ArtistId, hint.CatalogName, null, null));
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
                matches.Add(new(artist, albumMatches, strongAlbums >= 2 ? null : songMatches.Count, strongAlbums >= 2 || strongTrackAlbum,
                    matched.Select(pair => "album:" + pair.Local.Key).Concat(songMatches.Select(song => "track:" + song)).ToArray()));
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
        if (selected is { } selectedId)
        {
            ct.ThrowIfCancellationRequested();
            var chosen = verified.Single(m => m.Artist.Id == selectedId);
            var saved = _identities.Save(new(musician.Name, chosen.Artist.Name, selectedId, ArtistIdentitySource.Automatic,
                EvidenceHash(musician.Name)!, chosen.Evidence ?? [], chosen.AlbumMatches, chosen.TrackMatches, DateTimeOffset.UtcNow), generation);
            if (!saved)
            {
                var current = _identities.FindValid(musician.Name, EvidenceHash(musician.Name));
                return current == null ? result with { SelectedId = null }
                    : new([new(new(current.ArtistId, current.CatalogName, null, null), current.AlbumMatches, current.TrackMatches,
                        current.Source == ArtistIdentitySource.Automatic, current.Evidence)], current.ArtistId, true);
            }
        }
        if (complete) _identityResults[cacheKey] = result;
        return result;
    }

    private static bool IsIdentityTitle(string title) => title.Length >= 4 && title is not
        ("intro" or "outro" or "interlude" or "untitled" or "greatesthits" or "bestof" or "collection" or "live" or "singles" or "album");
}
