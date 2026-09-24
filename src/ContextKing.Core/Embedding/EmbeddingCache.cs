using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ContextKing.Core.Embedding;

/// <summary>
/// Stores card vectors between runs. A card's text is a pure function of file content and member,
/// never of the query, so the same text always maps to the same vector and a hit is exact rather
/// than approximate. Entries are keyed by the hash of model identity plus text, which means a model
/// change misses instead of returning a stale vector.
/// Every operation is best-effort: a cache fault must never fail a search.
/// </summary>
public sealed class EmbeddingCache : IDisposable
{
    // A 768-dimension vector plus its key, index entry and page overhead, measured at 4.3 KB.
    private const int ApproximateBytesPerEntry = 4400;
    private const int MinimumEntries = 256;
    // Evicting to just under the cap would re-evict on the next run; leave room for one more.
    private const double EvictionTarget = 0.9;

    private readonly SqliteConnection? _connection;
    private readonly string _model;
    private readonly int _maxEntries;
    private readonly int _maxAgeDays;
    private bool _dirty;

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    /// <summary>Rows currently stored, or -1 when the cache is disabled or unreadable.</summary>
    public long EntryCount
    {
        get
        {
            if (_connection is null) return -1;
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM embeddings";
                return Convert.ToInt64(command.ExecuteScalar());
            }
            catch (Exception) { return -1; }
        }
    }

    private EmbeddingCache(SqliteConnection? connection, string model, int maxEntries, int maxAgeDays)
    {
        _connection = connection;
        _model = model;
        _maxEntries = maxEntries;
        _maxAgeDays = maxAgeDays;
    }

    /// <summary>Opens the cache beside the index, or returns a disabled instance on any fault.</summary>
    public static EmbeddingCache Open(string indexDirectory, string model, int maxMegabytes, int maxAgeDays)
    {
        var maxEntries = Math.Max(MinimumEntries, (int)(Math.Clamp(maxMegabytes, 1, 4096) * 1024L * 1024L / ApproximateBytesPerEntry));
        try
        {
            Directory.CreateDirectory(indexDirectory);
            var connection = new SqliteConnection(
                $"Data Source={Path.Combine(indexDirectory, "embeddings.db")};Mode=ReadWriteCreate");
            connection.Open();
            // auto_vacuum only takes effect when set before the first table is created, and lets a
            // large eviction hand pages back to the file system instead of leaving the file at its
            // high-water mark forever.
            Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL");
            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, "PRAGMA busy_timeout=2000");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS embeddings (
                    hash     TEXT PRIMARY KEY,
                    vector   BLOB NOT NULL,
                    used_utc INTEGER NOT NULL
                )
                """);
            Execute(connection, "CREATE INDEX IF NOT EXISTS ix_embeddings_used ON embeddings(used_utc)");
            return new EmbeddingCache(connection, model, maxEntries, Math.Clamp(maxAgeDays, 1, 3650));
        }
        catch (Exception)
        {
            return new EmbeddingCache(null, model, maxEntries, maxAgeDays);
        }
    }

    public string Key(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_model + "\n" + text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Returns the cached vectors for <paramref name="keys"/>; absent keys are omitted.</summary>
    public Dictionary<string, float[]> Get(IReadOnlyList<string> keys)
    {
        var found = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (_connection is null || keys.Count == 0) return found;
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT hash, vector FROM embeddings WHERE hash IN ({Placeholders(keys.Count)})";
            for (var i = 0; i < keys.Count; i++) command.Parameters.AddWithValue("@p" + i, keys[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var blob = (byte[])reader["vector"];
                if (blob.Length == 0 || blob.Length % sizeof(float) != 0) continue;
                var vector = new float[blob.Length / sizeof(float)];
                MemoryMarshal.Cast<byte, float>(blob).CopyTo(vector);
                found[reader.GetString(0)] = vector;
            }

            if (found.Count > 0) Touch(found.Keys.ToArray());
        }
        catch (Exception) { /* A cache read never fails a search. */ }

        Hits += found.Count;
        Misses += keys.Count - found.Count;
        return found;
    }

    public void Put(IReadOnlyList<(string Key, float[] Vector)> entries)
    {
        if (_connection is null || entries.Count == 0) return;
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO embeddings (hash, vector, used_utc) VALUES (@hash, @vector, @used)
                ON CONFLICT(hash) DO UPDATE SET used_utc = excluded.used_utc
                """;
            var hash = command.Parameters.Add("@hash", SqliteType.Text);
            var vector = command.Parameters.Add("@vector", SqliteType.Blob);
            command.Parameters.AddWithValue("@used", Now);
            foreach (var (key, values) in entries)
            {
                var bytes = new byte[values.Length * sizeof(float)];
                MemoryMarshal.Cast<float, byte>(values).CopyTo(bytes);
                hash.Value = key;
                vector.Value = bytes;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _dirty = true;
        }
        catch (Exception) { /* A cache write never fails a search. */ }
    }

    /// <summary>Drops entries past their age limit, then the least recently used above the cap.</summary>
    public void Evict()
    {
        if (_connection is null || !_dirty) return;
        try
        {
            using var age = _connection.CreateCommand();
            age.CommandText = "DELETE FROM embeddings WHERE used_utc < @cutoff";
            age.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-_maxAgeDays).ToUnixTimeMilliseconds());
            var removed = age.ExecuteNonQuery();

            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM embeddings";
            var total = Convert.ToInt64(count.ExecuteScalar());
            var surplus = total - (long)(_maxEntries * EvictionTarget);
            if (surplus > 0)
            {
                using var trim = _connection.CreateCommand();
                trim.CommandText = """
                    DELETE FROM embeddings WHERE hash IN
                        (SELECT hash FROM embeddings ORDER BY used_utc ASC, hash ASC LIMIT @surplus)
                    """;
                trim.Parameters.AddWithValue("@surplus", surplus);
                removed += trim.ExecuteNonQuery();
            }

            if (removed > 0) Execute(_connection, "PRAGMA incremental_vacuum");
        }
        catch (Exception) { /* Eviction is maintenance; a failure only delays it. */ }
    }

    public void Dispose()
    {
        Evict();
        _connection?.Dispose();
    }

    private void Touch(IReadOnlyList<string> keys)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = $"UPDATE embeddings SET used_utc = @used WHERE hash IN ({Placeholders(keys.Count)})";
        command.Parameters.AddWithValue("@used", Now);
        for (var i = 0; i < keys.Count; i++) command.Parameters.AddWithValue("@p" + i, keys[i]);
        command.ExecuteNonQuery();
    }

    // Milliseconds, so batches written within one run still evict oldest-first.
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Placeholders(int count) => string.Join(',', Enumerable.Range(0, count).Select(i => "@p" + i));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
