using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RecipeSong.Core;

public enum ArtistIdentitySource { Legacy, Lookup, Automatic, Manual }
public sealed record StoredArtistIdentity(string Name, string CatalogName, long ArtistId, ArtistIdentitySource Source,
    string EvidenceHash, IReadOnlyList<string> Evidence, int AlbumMatches, int? TrackMatches, DateTimeOffset CheckedAt);

/// <summary>확정 ID와 이름 검색 힌트를 분리해 저장한다. 연결은 작업마다 열어 프로세스 간 변경을 바로 읽는다.</summary>
public sealed class ArtistIdentityStore
{
    private readonly string _connectionString;
    public string FilePath { get; }
    public ArtistIdentityStore(string? path = null)
    {
        FilePath = Path.GetFullPath(path ?? Path.Combine(CacheLocation.Directory, "artist-identities.sqlite3"));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath, DefaultTimeout = 15, Pooling = false }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS artist_identities (
                artist_key TEXT PRIMARY KEY, name TEXT NOT NULL, catalog_name TEXT NOT NULL,
                artist_id INTEGER NOT NULL CHECK(artist_id>0), source INTEGER NOT NULL,
                evidence_hash TEXT NOT NULL, evidence_json TEXT NOT NULL,
                album_matches INTEGER NOT NULL, track_matches INTEGER, checked_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS identity_meta (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, imported INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS identity_epochs (artist_key TEXT PRIMARY KEY, generation INTEGER NOT NULL);
            INSERT OR IGNORE INTO identity_meta VALUES(1,0,0);
            """;
        command.ExecuteNonQuery();
        ImportLegacy(db);
    }

    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public long Revision
    {
        get { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT revision FROM identity_meta WHERE id=1"; return (long)cmd.ExecuteScalar()!; }
    }
    public StoredArtistIdentity? Read(string name)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name,catalog_name,artist_id,source,evidence_hash,evidence_json,album_matches,track_matches,checked_at FROM artist_identities WHERE artist_key=$key";
        cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(name));
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        return new(row.GetString(0), row.GetString(1), row.GetInt64(2), (ArtistIdentitySource)row.GetInt32(3), row.GetString(4),
            JsonSerializer.Deserialize<string[]>(row.GetString(5)) ?? [], row.GetInt32(6), row.IsDBNull(7) ? null : row.GetInt32(7), DateTimeOffset.Parse(row.GetString(8)));
    }
    public StoredArtistIdentity? FindValid(string name, string? evidenceHash)
    {
        var entry = Read(name);
        return entry != null && (entry.Source == ArtistIdentitySource.Manual
            || entry.Source == ArtistIdentitySource.Automatic && evidenceHash != null && entry.EvidenceHash == evidenceHash
                && DateTimeOffset.UtcNow - entry.CheckedAt < TimeSpan.FromDays(90)) ? entry : null;
    }
    public StoredArtistIdentity? FindLookup(string name)
    {
        var entry = Read(name);
        return entry?.Source == ArtistIdentitySource.Lookup && DateTimeOffset.UtcNow - entry.CheckedAt < TimeSpan.FromDays(90) ? entry : null;
    }
    public long Generation(string name)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT generation FROM identity_epochs WHERE artist_key=$key";
        cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(name)); return (long?)cmd.ExecuteScalar() ?? 0;
    }
    public bool Save(StoredArtistIdentity entry, long? expectedGeneration = null)
    {
        if (entry.ArtistId <= 0 || ListeningAnalyzer.NormalizeArtist(entry.Name).Length == 0) throw new ArgumentException(nameof(entry));
        using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        if (expectedGeneration is { } expected)
        {
            cmd.CommandText = "SELECT generation FROM identity_epochs WHERE artist_key=$key";
            cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(entry.Name));
            if (((long?)cmd.ExecuteScalar() ?? 0) != expected) return false;
            cmd.Parameters.Clear();
        }
        cmd.CommandText = """
            INSERT INTO artist_identities VALUES($key,$name,$catalog,$id,$source,$hash,$evidence,$albums,$tracks,$at)
            ON CONFLICT(artist_key) DO UPDATE SET name=excluded.name,catalog_name=excluded.catalog_name,
                artist_id=excluded.artist_id,source=excluded.source,evidence_hash=excluded.evidence_hash,
                evidence_json=excluded.evidence_json,album_matches=excluded.album_matches,
                track_matches=excluded.track_matches,checked_at=excluded.checked_at
            WHERE artist_identities.source <= excluded.source;
            """;
        cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(entry.Name)); cmd.Parameters.AddWithValue("$name", entry.Name);
        cmd.Parameters.AddWithValue("$catalog", entry.CatalogName); cmd.Parameters.AddWithValue("$id", entry.ArtistId);
        cmd.Parameters.AddWithValue("$source", (int)entry.Source); cmd.Parameters.AddWithValue("$hash", entry.EvidenceHash);
        cmd.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(entry.Evidence)); cmd.Parameters.AddWithValue("$albums", entry.AlbumMatches);
        cmd.Parameters.AddWithValue("$tracks", (object?)entry.TrackMatches ?? DBNull.Value); cmd.Parameters.AddWithValue("$at", entry.CheckedAt.ToString("O"));
        var saved = cmd.ExecuteNonQuery() > 0;
        if (saved) BumpRevision(db, transaction);
        transaction.Commit();
        return saved;
    }
    public void Forget(string name)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = transaction; cmd.CommandText = "DELETE FROM artist_identities WHERE artist_key=$key";
        cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(name));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO identity_epochs VALUES($key,1) ON CONFLICT(artist_key) DO UPDATE SET generation=generation+1";
        cmd.ExecuteNonQuery(); BumpRevision(db, transaction); transaction.Commit();
    }
    private static void BumpRevision(SqliteConnection db, SqliteTransaction transaction)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE identity_meta SET revision=revision+1 WHERE id=1"; cmd.ExecuteNonQuery();
    }
    private void ImportLegacy(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT imported FROM identity_meta WHERE id=1";
        if ((long)cmd.ExecuteScalar()! != 0) { transaction.Commit(); return; }
        foreach (var file in new[] { "deezer-playlist-artist-ids.json", "deezer-artist-ids.json" })
        {
            var path = Path.Combine(Path.GetDirectoryName(FilePath)!, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("Value", out var value)
                        || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0) continue;
                    cmd.Parameters.Clear();
                    cmd.CommandText = "INSERT OR IGNORE INTO artist_identities VALUES($key,$name,$name,$id,0,'','[]',0,NULL,$at)";
                    cmd.Parameters.AddWithValue("$key", ListeningAnalyzer.NormalizeArtist(property.Name)); cmd.Parameters.AddWithValue("$name", property.Name);
                    cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
                }
            }
            catch (JsonException) { /* 손상된 옛 캐시는 보존하고 새로 확인한다. */ }
        }
        cmd.Parameters.Clear(); cmd.CommandText = "UPDATE identity_meta SET imported=1 WHERE id=1"; cmd.ExecuteNonQuery(); transaction.Commit();
    }
    public static string Fingerprint(IEnumerable<string> titles) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(titles.Distinct().Order(StringComparer.Ordinal)))));
}
