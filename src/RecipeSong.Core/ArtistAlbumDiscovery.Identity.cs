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
        long? selected = complete ? Select(matches, musician.Name)?.Artist.Id : null;
        var result = new ArtistIdentityResult(matches.OrderByDescending(m => m.Verified).ThenByDescending(m => m.AlbumMatches)
            .ThenByDescending(m => m.TrackMatches).ThenBy(m => m.Artist.Id).ToArray(), selected);
        if (selected is { } selectedId)
        {
            ct.ThrowIfCancellationRequested();
            var chosen = matches.First(m => m.Artist.Id == selectedId);
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

    /// <summary>
    /// 같은 뮤지션으로 정할 후보. 검증(보유 앨범 2장 또는 한 앨범의 곡 2개)을 통과한 후보가 하나면 그것.
    /// 여럿이어도 1위 앨범 일치가 2위의 2배 이상이고 2장 넘게 차이 나면 1위 (WONK 5장 vs 동명 WoNK 1장 — 예전엔 둘 다 버려 신보가 안 나왔다).
    /// 아무도 검증을 못 넘었으면, 증거(앨범 제목·곡)가 있는 후보가 하나뿐일 때 그것 (보유 앨범이 한 장뿐이거나 Deezer에 일부만 있는 뮤지션).
    /// 이름이 같으면 증거 하나로, 다르면(옥상달빛 → Okdal, 김뜻돌 → Meaningful Stone: Deezer 영문 표기) 앨범·곡 증거 둘 이상으로.
    /// 동명이인이 그 앨범 제목까지 같을 가능성은 낮고, 다른 후보에도 증거가 있으면 고르지 않는다.
    /// </summary>
    private static ArtistIdentityMatch? Select(IReadOnlyList<ArtistIdentityMatch> matches, string name)
    {
        var verified = matches.Where(m => m.Verified).OrderByDescending(m => m.AlbumMatches).ToArray();
        if (verified.Length == 1) return verified[0];
        if (verified.Length > 1)
            return verified[0].AlbumMatches >= 2 * verified[1].AlbumMatches && verified[0].AlbumMatches - verified[1].AlbumMatches >= 2 ? verified[0] : null;
        var evidenced = matches.Where(m => m.AlbumMatches > 0 || m.TrackMatches > 0).ToArray();
        if (evidenced is not [var only]) return null;
        bool strongAlbum = only.Evidence?.Any(e => e.StartsWith("album:") && IsIdentityTitle(e[6..]) && e[6..] != Key(name)) == true;
        return Key(only.Artist.Name) == Key(name) ? (only.TrackMatches > 0 || strongAlbum ? only : null)
            : strongAlbum && only.AlbumMatches + (only.TrackMatches ?? 0) >= 2 ? only : null;
    }

    private static bool IsIdentityTitle(string title) => title.Length >= 4 && title is not
        ("intro" or "outro" or "interlude" or "untitled" or "greatesthits" or "bestof" or "collection" or "live" or "singles" or "album");
}
