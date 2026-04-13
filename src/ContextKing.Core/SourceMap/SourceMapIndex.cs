using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ContextKing.Core.SourceMap;

// ── Data transfer objects between index and callers ────────────────────────────

/// <summary>Per-folder state stored in the index, used to decide what needs re-embedding.</summary>
internal record struct StoredFolderState(string FileHashes, string? FilenameSet);

/// <summary>A fully populated folder row ready to be written to the index.</summary>
internal sealed record FolderRow(
    string Path,
    string CombinedTokens,
    byte[] EmbeddingBlob,
    string FileHashes,
    string FilenameSet);

/// <summary>A folder row loaded from the index for scoring.</summary>
internal sealed record IndexedFolder(
    string Path,
    float[] Embedding,
    string CombinedTokens);

// ── Index ──────────────────────────────────────────────────────────────────────

/// <summary>
/// All SQLite access for the source-map index.
/// Each method opens and closes its own connection; the index is safe to use
/// from multiple callers in sequence (not concurrently).
/// </summary>
internal sealed class SourceMapIndex(string dbPath)
{
    public bool Exists => File.Exists(dbPath);

    public static string DbPathFor(string worktreeRoot)
    {
        var indexDir = Path.Combine(worktreeRoot, ".ck-index");
        return Path.Combine(indexDir, "index.db");
    }

    // ── Schema ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates tables if they do not exist, and runs any pending one-time migrations.
    /// Safe to call on every build — all operations are idempotent.
    /// </summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        CreateTables(conn);
        MigrateFilenameSet(conn);
    }

    // ── Reads ──────────────────────────────────────────────────────────────────

    public string? ReadMeta(string key)
    {
        using var conn = OpenReadOnly();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Loads the filename-set fingerprint and file-hash JSON for every folder,
    /// used by <see cref="SourceMapBuilder"/> to determine which folders need re-embedding.
    /// </summary>
    public Dictionary<string, StoredFolderState> LoadFolderStates()
    {
        using var conn = OpenReadOnly();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT path, file_hashes, filename_set FROM folders";

        var result = new Dictionary<string, StoredFolderState>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path        = reader.GetString(0);
            var hashes      = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var filenameSet = reader.IsDBNull(2) ? null         : reader.GetString(2);
            result[path]    = new StoredFolderState(hashes, filenameSet);
        }
        return result;
    }

    /// <summary>
    /// Loads all indexed folders with their embeddings and token strings,
    /// used by <see cref="SourceMapSearcher"/> for scoring.
    /// </summary>
    public IReadOnlyList<IndexedFolder> LoadIndexedFolders()
    {
        using var conn = OpenReadOnly();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT path, embedding, combined_tokens FROM folders WHERE embedding IS NOT NULL";

        var result = new List<IndexedFolder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path           = reader.GetString(0);
            var embedding      = DecodeEmbedding((byte[])reader.GetValue(1));
            var combinedTokens = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            result.Add(new IndexedFolder(path, embedding, combinedTokens));
        }
        return result;
    }

    // ── Writes ─────────────────────────────────────────────────────────────────

    public void WriteMeta(string key, string value)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key",   key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upserts all rows in a single transaction.</summary>
    public void UpsertFolders(IReadOnlyList<FolderRow> rows)
    {
        if (rows.Count == 0) return;

        using var conn = Open();
        using var txn  = conn.BeginTransaction();
        using var cmd  = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            INSERT INTO folders (path, combined_tokens, embedding, file_hashes, filename_set)
                VALUES ($path, $tokens, $blob, $hashes, $fs)
            ON CONFLICT(path) DO UPDATE SET
                combined_tokens = excluded.combined_tokens,
                embedding       = excluded.embedding,
                file_hashes     = excluded.file_hashes,
                filename_set    = excluded.filename_set
            """;
        var pPath   = cmd.Parameters.Add("$path",   SqliteType.Text);
        var pTokens = cmd.Parameters.Add("$tokens", SqliteType.Text);
        var pBlob   = cmd.Parameters.Add("$blob",   SqliteType.Blob);
        var pHashes = cmd.Parameters.Add("$hashes", SqliteType.Text);
        var pFs     = cmd.Parameters.Add("$fs",     SqliteType.Text);

        foreach (var row in rows)
        {
            pPath.Value   = row.Path;
            pTokens.Value = row.CombinedTokens;
            pBlob.Value   = row.EmbeddingBlob;
            pHashes.Value = row.FileHashes;
            pFs.Value     = row.FilenameSet;
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public void ClearAllFolders()
    {
        using var conn = Open();
        NonQuery(conn, "DELETE FROM folders");
    }

    /// <summary>
    /// Removes rows for folders not present in <paramref name="pathsToKeep"/>.
    /// </summary>
    public void DeleteFolders(HashSet<string> pathsToKeep)
    {
        using var conn = Open();
        using var sel  = conn.CreateCommand();
        sel.CommandText = "SELECT path FROM folders";

        var toDelete = new List<string>();
        using (var reader = sel.ExecuteReader())
            while (reader.Read())
            {
                var p = reader.GetString(0);
                if (!pathsToKeep.Contains(p)) toDelete.Add(p);
            }

        if (toDelete.Count == 0) return;

        using var del   = conn.CreateCommand();
        del.CommandText = "DELETE FROM folders WHERE path = $path";
        var param = del.Parameters.Add("$path", SqliteType.Text);
        foreach (var p in toDelete)
        {
            param.Value = p;
            del.ExecuteNonQuery();
        }
    }

    // ── Embedding encoding ─────────────────────────────────────────────────────

    public static byte[] EncodeEmbedding(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(vector).CopyTo(bytes);
        return bytes;
    }

    public static float[] DecodeEmbedding(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(blob).CopyTo(floats);
        return floats;
    }

    // ── Schema helpers ─────────────────────────────────────────────────────────

    private static void CreateTables(SqliteConnection conn)
    {
        NonQuery(conn, """
            CREATE TABLE IF NOT EXISTS folders (
                id              INTEGER PRIMARY KEY,
                path            TEXT    UNIQUE NOT NULL,
                combined_tokens TEXT,
                embedding       BLOB,
                file_hashes     TEXT,
                filename_set    TEXT
            )
            """);
        NonQuery(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            )
            """);

        // Migration: add filename_set to any schema that pre-dates it
        try { NonQuery(conn, "ALTER TABLE folders ADD COLUMN filename_set TEXT"); }
        catch { /* column already exists */ }
    }

    /// <summary>
    /// One-time migration: derives and populates <c>filename_set</c> from existing
    /// <c>file_hashes</c> JSON rows so old indexes are not fully rebuilt on upgrade.
    /// </summary>
    private static void MigrateFilenameSet(SqliteConnection conn)
    {
        var toUpdate = new List<(string path, string filenameSet)>();

        using var sel = conn.CreateCommand();
        sel.CommandText =
            "SELECT path, file_hashes FROM folders WHERE filename_set IS NULL AND file_hashes IS NOT NULL";
        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            var path   = reader.GetString(0);
            var hashes = reader.GetString(1);
            try
            {
                using var doc = JsonDocument.Parse(hashes);
                var key = string.Join('|',
                    doc.RootElement
                       .EnumerateObject()
                       .Select(p => p.Name)
                       .Order(StringComparer.OrdinalIgnoreCase));
                toUpdate.Add((path, key));
            }
            catch { /* malformed JSON — leave NULL, will be corrected on next re-embed */ }
        }

        if (toUpdate.Count == 0) return;

        using var txn = conn.BeginTransaction();
        using var upd = conn.CreateCommand();
        upd.Transaction = txn;
        upd.CommandText = "UPDATE folders SET filename_set = $fs WHERE path = $path";
        var paramFs   = upd.Parameters.Add("$fs",   SqliteType.Text);
        var paramPath = upd.Parameters.Add("$path", SqliteType.Text);
        foreach (var (path, filenameSet) in toUpdate)
        {
            paramFs.Value   = filenameSet;
            paramPath.Value = path;
            upd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    // ── Connection helpers ─────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        conn.Open();
        return conn;
    }

    private SqliteConnection OpenReadOnly()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static void NonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
