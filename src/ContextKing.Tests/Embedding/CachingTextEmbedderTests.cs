using ContextKing.Core.Embedding;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ContextKing.Tests.Embedding;

public class CachingTextEmbedderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ck-cache-" + Path.GetRandomFileName());

    private sealed class CountingEmbedder : IBatchTextEmbedder, IQueryTextEmbedder
    {
        public int Embedded { get; private set; }
        public int QueriesEmbedded { get; private set; }

        public float[] Embed(string text) => EmbedBatch([text])[0];
        public float[] EmbedQuery(string text) { QueriesEmbedded++; return [-1f]; }

        public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
        {
            Embedded += texts.Count;
            return texts.Select(t => new[] { (float)t.Length }).ToArray();
        }
    }

    [Fact]
    public void SecondRunEmbedsNothingAndReturnsTheSameVectors()
    {
        var inner = new CountingEmbedder();
        string[] texts = ["member one", "member two", "member three"];

        IReadOnlyList<float[]> first;
        using (var cache = EmbeddingCache.Open(_directory, "model@1", 128, 30))
            first = new CachingTextEmbedder(inner, cache).EmbedBatch(texts);
        inner.Embedded.Should().Be(3);

        using var reopened = EmbeddingCache.Open(_directory, "model@1", 128, 30);
        var second = new CachingTextEmbedder(inner, reopened).EmbedBatch(texts);

        inner.Embedded.Should().Be(3);
        reopened.Hits.Should().Be(3);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void OnlyMissesReachTheInnerEmbedderAndOrderIsPreserved()
    {
        var inner = new CountingEmbedder();
        using var cache = EmbeddingCache.Open(_directory, "model@1", 128, 30);
        var embedder = new CachingTextEmbedder(inner, cache);
        embedder.EmbedBatch(["cached"]);

        // "cached" is already known, the duplicate must not be embedded twice, and every
        // position must still carry its own vector.
        var result = embedder.EmbedBatch(["fresh", "cached", "fresh"]);

        inner.Embedded.Should().Be(2);
        result.Should().HaveCount(3);
        result[0].Should().Equal(5f);
        result[1].Should().Equal(6f);
        result[2].Should().Equal(5f);
    }

    [Fact]
    public void QueriesBypassTheCacheAndKeepTheModelQueryPath()
    {
        var inner = new CountingEmbedder();
        using var cache = EmbeddingCache.Open(_directory, "model@1", 128, 30);

        new CachingTextEmbedder(inner, cache).EmbedQuery("find terminal refunds").Should().Equal(-1f);

        inner.QueriesEmbedded.Should().Be(1);
        cache.EntryCount.Should().Be(0);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch { }
    }
}
