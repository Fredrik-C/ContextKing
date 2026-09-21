using ContextKing.Core.Embedding;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ContextKing.Tests.Embedding;

public class EmbeddingCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ck-cache-" + Path.GetRandomFileName());

    [Fact]
    public void StoredVectorsComeBackForTheSameText()
    {
        using var cache = EmbeddingCache.Open(_directory, "model@1", 128, 30);
        var key = cache.Key("void RequestTerminalRefundAsync()");
        cache.Put([(key, [0.25f, -0.5f, 0.75f])]);

        var found = cache.Get([key, cache.Key("something else")]);

        found[key].Should().Equal(0.25f, -0.5f, 0.75f);
        found.Should().HaveCount(1);
        cache.Hits.Should().Be(1);
        cache.Misses.Should().Be(1);
    }

    [Fact]
    public void ADifferentModelMissesRatherThanReturningAStaleVector()
    {
        using (var first = EmbeddingCache.Open(_directory, "model@1", 128, 30))
            first.Put([(first.Key("text"), [1f])]);

        using var second = EmbeddingCache.Open(_directory, "model@2", 128, 30);
        second.Get([second.Key("text")]).Should().BeEmpty();
    }

    // 2 MB of the measured 4400 bytes per entry, above the 256-entry floor, evicted to 90%.
    private const int Cap = 2 * 1024 * 1024 / 4400;
    private const int Target = (int)(Cap * 0.9);

    [Fact]
    public void EvictionTrimsBackUnderTheCap()
    {
        using var cache = EmbeddingCache.Open(_directory, "model@1", maxMegabytes: 2, maxAgeDays: 30);
        cache.Put([.. Enumerable.Range(0, Cap + 120).Select(i => (cache.Key("member" + i), new[] { (float)i }))]);
        cache.EntryCount.Should().BeGreaterThan(Cap);

        cache.Evict();

        cache.EntryCount.Should().Be(Target);
    }

    [Fact]
    public void EvictionDropsTheLeastRecentlyUsedFirst()
    {
        using var cache = EmbeddingCache.Open(_directory, "model@1", maxMegabytes: 2, maxAgeDays: 3650);
        var stale = cache.Key("stale");
        cache.Put([(stale, [1f])]);
        Backdate(days: 1);
        // One entry over the eviction target, so exactly one row goes and there is no tie to break.
        cache.Put([.. Enumerable.Range(0, Target).Select(i => (cache.Key("filler" + i), new[] { (float)i }))]);

        cache.Evict();

        cache.EntryCount.Should().Be(Target);
        cache.Get([stale]).Should().BeEmpty();
        cache.Get([cache.Key("filler0")]).Should().HaveCount(1);
    }

    [Fact]
    public void EvictionDropsEntriesPastTheAgeLimit()
    {
        using var cache = EmbeddingCache.Open(_directory, "model@1", 128, maxAgeDays: 7);
        var key = cache.Key("member");
        cache.Put([(key, [1f])]);
        Backdate(days: 30);

        cache.Evict();

        cache.EntryCount.Should().Be(0);
        cache.Get([key]).Should().BeEmpty();
    }

    [Fact]
    public void AnUnusableDirectoryDisablesTheCacheInsteadOfThrowing()
    {
        var file = Path.Combine(_directory, "occupied");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, "not a directory");

        using var cache = EmbeddingCache.Open(file, "model@1", 128, 30);

        cache.Put([(cache.Key("text"), [1f])]);
        cache.Get([cache.Key("text")]).Should().BeEmpty();
        cache.EntryCount.Should().Be(-1);
    }

    private void Backdate(int days)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "embeddings.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE embeddings SET used_utc = @used";
        command.Parameters.AddWithValue("@used", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch { }
    }
}
