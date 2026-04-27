using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ContextKing.Core.SourceMap;

// ── Data transfer objects between index and callers ────────────────────────────

/// <summary>Per-folder state stored in the index, used to decide what needs re-embedding.</summary>
internal record struct StoredFolderState(string FileHashes, string? FilenameSet, int FileCount, int TokenCount);

/// <summary>A knowledge row ready to be written to the index.</summary>
internal sealed record KnowledgeRow(
    string Id,
    string Content,
    byte[] Embedding,
    string? Tags,
    string? Folders,
    string? Source,
    string? CreatedAt);

/// <summary>A knowledge row loaded from the index for scoring.</summary>
internal sealed record IndexedKnowledge(
    string Id,
    string Content,
    float[] Embedding,
    string? Tags,
    string? Folders);

/// <summary>A fully populated folder row ready to be written to the index.</summary>
internal sealed record FolderRow(
    string Path,
    string CombinedTokens,
    string EmbeddingText,
    byte[] EmbeddingBlob,
    string FileHashes,
    string FilenameSet,
    int FileCount = 0,
    int TokenCount = 0);

/// <summary>A folder row loaded from the index for scoring.</summary>
internal sealed record IndexedFolder(
    string Path,
    float[] Embedding,
    string CombinedTokens,
    string EmbeddingText,
    int FileCount,
    int TokenCount);

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
        cmd.CommandText = "SELECT path, file_hashes, filename_set, file_count, token_count FROM folders";

        var result = new Dictionary<string, StoredFolderState>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path        = reader.GetString(0);
            var hashes      = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var filenameSet = reader.IsDBNull(2) ? null         : reader.GetString(2);
            var fileCount   = reader.IsDBNull(3) ? 0            : reader.GetInt32(3);
            var tokenCount  = reader.IsDBNull(4) ? 0            : reader.GetInt32(4);
            result[path]    = new StoredFolderState(hashes, filenameSet, fileCount, tokenCount);
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
            "SELECT path, embedding, combined_tokens, embedding_text, file_count, token_count FROM folders WHERE embedding IS NOT NULL";

        var result = new List<IndexedFolder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path           = reader.GetString(0);
            var embedding      = DecodeEmbedding((byte[])reader.GetValue(1));
            var combinedTokens = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var embeddingText  = reader.IsDBNull(3) ? combinedTokens : reader.GetString(3);
            var fileCount      = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var tokenCount     = reader.IsDBNull(5) ? CountTokens(combinedTokens) : reader.GetInt32(5);
            result.Add(new IndexedFolder(path, embedding, combinedTokens, embeddingText, fileCount, tokenCount));
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
            INSERT INTO folders (path, combined_tokens, embedding_text, embedding, file_hashes, filename_set, file_count, token_count)
                VALUES ($path, $tokens, $etext, $blob, $hashes, $fs, $file_count, $token_count)
            ON CONFLICT(path) DO UPDATE SET
                combined_tokens = excluded.combined_tokens,
                embedding_text  = excluded.embedding_text,
                embedding       = excluded.embedding,
                file_hashes     = excluded.file_hashes,
                filename_set    = excluded.filename_set,
                file_count      = excluded.file_count,
                token_count     = excluded.token_count
            """;
        var pPath       = cmd.Parameters.Add("$path",        SqliteType.Text);
        var pTokens     = cmd.Parameters.Add("$tokens",      SqliteType.Text);
        var pEText      = cmd.Parameters.Add("$etext",       SqliteType.Text);
        var pBlob       = cmd.Parameters.Add("$blob",        SqliteType.Blob);
        var pHashes     = cmd.Parameters.Add("$hashes",      SqliteType.Text);
        var pFs         = cmd.Parameters.Add("$fs",          SqliteType.Text);
        var pFileCount  = cmd.Parameters.Add("$file_count",  SqliteType.Integer);
        var pTokenCount = cmd.Parameters.Add("$token_count", SqliteType.Integer);

        foreach (var row in rows)
        {
            pPath.Value       = row.Path;
            pTokens.Value     = row.CombinedTokens;
            pEText.Value      = row.EmbeddingText;
            pBlob.Value       = row.EmbeddingBlob;
            pHashes.Value     = row.FileHashes;
            pFs.Value         = row.FilenameSet;
            pFileCount.Value  = row.FileCount;
            pTokenCount.Value = row.TokenCount;
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

    // ── Knowledge schema + CRUD ────────────────────────────────────────────────

    /// <summary>
    /// Creates the knowledge table if it does not exist.
    /// Safe to call repeatedly — all operations are idempotent.
    /// </summary>
    public void EnsureKnowledgeSchema()
    {
        using var conn = Open();
        NonQuery(conn, """
            CREATE TABLE IF NOT EXISTS knowledge (
                id         TEXT PRIMARY KEY,
                content    TEXT NOT NULL,
                embedding  BLOB NOT NULL,
                tags       TEXT,
                folders    TEXT,
                source     TEXT,
                created_at TEXT
            )
            """);
        // Reuse the existing meta table for knowledge_snippets_hash
        NonQuery(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            )
            """);
    }

    public IReadOnlyList<IndexedKnowledge> LoadKnowledgeRows()
    {
        if (!Exists) return [];
        using var conn = OpenReadOnly();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, content, embedding, tags, folders FROM knowledge";

        var result = new List<IndexedKnowledge>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IndexedKnowledge(
                reader.GetString(0),
                reader.GetString(1),
                DecodeEmbedding((byte[])reader.GetValue(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    /// <summary>Replaces all knowledge rows in a single transaction.</summary>
    public void ReplaceAllKnowledge(IReadOnlyList<KnowledgeRow> rows)
    {
        using var conn = Open();
        NonQuery(conn, "DELETE FROM knowledge");

        if (rows.Count == 0) return;

        using var txn = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            INSERT INTO knowledge (id, content, embedding, tags, folders, source, created_at)
                VALUES ($id, $content, $blob, $tags, $folders, $source, $created_at)
            """;
        var pId  = cmd.Parameters.Add("$id",         SqliteType.Text);
        var pCnt = cmd.Parameters.Add("$content",    SqliteType.Text);
        var pBlb = cmd.Parameters.Add("$blob",        SqliteType.Blob);
        var pTgs = cmd.Parameters.Add("$tags",        SqliteType.Text);
        var pFld = cmd.Parameters.Add("$folders",     SqliteType.Text);
        var pSrc = cmd.Parameters.Add("$source",      SqliteType.Text);
        var pCat = cmd.Parameters.Add("$created_at",  SqliteType.Text);

        foreach (var row in rows)
        {
            pId.Value  = row.Id;
            pCnt.Value = row.Content;
            pBlb.Value = row.Embedding;
            pTgs.Value = (object?)row.Tags       ?? DBNull.Value;
            pFld.Value = (object?)row.Folders    ?? DBNull.Value;
            pSrc.Value = (object?)row.Source     ?? DBNull.Value;
            pCat.Value = (object?)row.CreatedAt  ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
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
                filename_set    TEXT,
                file_count      INTEGER DEFAULT 0,
                token_count     INTEGER DEFAULT 0
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

        // Migration: add embedding_text to any schema that pre-dates it
        try { NonQuery(conn, "ALTER TABLE folders ADD COLUMN embedding_text TEXT"); }
        catch { /* column already exists */ }

        // Migration: add folder statistics used by scorer diagnostics and noise penalties.
        try { NonQuery(conn, "ALTER TABLE folders ADD COLUMN file_count INTEGER DEFAULT 0"); }
        catch { /* column already exists */ }
        try { NonQuery(conn, "ALTER TABLE folders ADD COLUMN token_count INTEGER DEFAULT 0"); }
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

    private static int CountTokens(string combinedTokens) =>
        combinedTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
