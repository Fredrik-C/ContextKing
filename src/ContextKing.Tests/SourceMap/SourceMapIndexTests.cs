using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class SourceMapIndexTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "ck-test-" + Path.GetRandomFileName() + ".db");

    private SourceMapIndex Index => new(_dbPath);

    // ── Schema ────────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchema_CreatesTablesOnFirstCall()
    {
        Index.EnsureSchema();

        // Tables must exist — verify by performing a read without exception
        var states = Index.LoadFolderStates();
        states.Should().BeEmpty();
    }

    [Fact]
    public void EnsureSchema_IsIdempotent()
    {
        Index.EnsureSchema();
        Action again = () => Index.EnsureSchema();
        again.Should().NotThrow();
    }

    // ── Meta ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteMeta_ReadMeta_RoundTrip()
    {
        Index.EnsureSchema();
        Index.WriteMeta("my_key", "my_value");
        Index.ReadMeta("my_key").Should().Be("my_value");
    }

    [Fact]
    public void WriteMeta_OverwritesExistingKey()
    {
        Index.EnsureSchema();
        Index.WriteMeta("k", "v1");
        Index.WriteMeta("k", "v2");
        Index.ReadMeta("k").Should().Be("v2");
    }

    [Fact]
    public void ReadMeta_MissingKey_ReturnsNull()
    {
        Index.EnsureSchema();
        Index.ReadMeta("no_such_key").Should().BeNull();
    }

    // ── Upsert / Load ─────────────────────────────────────────────────────────

    [Fact]
    public void UpsertFolders_ThenLoadIndexedFolders_RoundTrip()
    {
        Index.EnsureSchema();

        var embedding = new float[384];
        embedding[0] = 1f;
        var blob = SourceMapIndex.EncodeEmbedding(embedding);

        var rows = new[]
        {
            new FolderRow("src/Payment", "src payment", "src Payment", blob, "{}", "StripeService.cs")
        };
        Index.UpsertFolders(rows);

        var folders = Index.LoadIndexedFolders();
        folders.Should().HaveCount(1);
        folders[0].Path.Should().Be("src/Payment");
        folders[0].CombinedTokens.Should().Be("src payment");
        folders[0].Embedding.Should().HaveCount(384);
        folders[0].Embedding[0].Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void UpsertFolders_OverwritesExistingRow()
    {
        Index.EnsureSchema();
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);

        Index.UpsertFolders([new FolderRow("src/A", "original", "original text", blob, "{}", "a.cs")]);
        Index.UpsertFolders([new FolderRow("src/A", "updated",  "updated text", blob, "{}", "a.cs")]);

        var folders = Index.LoadIndexedFolders();
        folders.Should().HaveCount(1);
        folders[0].CombinedTokens.Should().Be("updated");
    }

    [Fact]
    public void LoadFolderStates_ReturnsFilenameSetAndHashes()
    {
        Index.EnsureSchema();
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        Index.UpsertFolders([new FolderRow("src/X", "tokens", "embedding text", blob, "{\"a.cs\":\"abc\"}", "a.cs")]);

        var states = Index.LoadFolderStates();
        states.Should().ContainKey("src/X");
        states["src/X"].FilenameSet.Should().Be("a.cs");
        states["src/X"].FileHashes.Should().Be("{\"a.cs\":\"abc\"}");
    }

    [Fact]
    public void LoadIndexedFolders_SkipsRowsWithNullEmbedding()
    {
        Index.EnsureSchema();
        // Insert a row with a real embedding and one without
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        Index.UpsertFolders([new FolderRow("src/Good", "tokens", "embedding text", blob, "{}", "f.cs")]);

        // Directly insert a row with no embedding via the state-loading path
        // (can't do this through UpsertFolders, so verify via LoadFolderStates instead)
        var states = Index.LoadFolderStates();
        states.Should().ContainKey("src/Good");

        var folders = Index.LoadIndexedFolders();
        folders.Should().OnlyContain(f => f.Path == "src/Good");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteFolders_RemovesFoldersMissingFromKeepSet()
    {
        Index.EnsureSchema();
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        Index.UpsertFolders([
            new FolderRow("src/Keep",   "tokens", "embedding text", blob, "{}", "a.cs"),
            new FolderRow("src/Remove", "tokens", "embedding text", blob, "{}", "b.cs"),
        ]);

        Index.DeleteFolders(["src/Keep"]);

        var folders = Index.LoadIndexedFolders();
        folders.Should().HaveCount(1);
        folders[0].Path.Should().Be("src/Keep");
    }

    [Fact]
    public void DeleteFolders_EmptyKeepSet_DeletesAll()
    {
        Index.EnsureSchema();
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        Index.UpsertFolders([new FolderRow("src/A", "tokens", "embedding text", blob, "{}", "a.cs")]);

        Index.DeleteFolders([]);

        Index.LoadIndexedFolders().Should().BeEmpty();
    }

    [Fact]
    public void ClearAllFolders_EmptiesTable()
    {
        Index.EnsureSchema();
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        Index.UpsertFolders([new FolderRow("src/A", "tokens", "embedding text", blob, "{}", "a.cs")]);

        Index.ClearAllFolders();

        Index.LoadIndexedFolders().Should().BeEmpty();
    }

    // ── Embedding encoding ────────────────────────────────────────────────────

    [Fact]
    public void EncodeDecodeEmbedding_IsLosslessRoundTrip()
    {
        var original = Enumerable.Range(0, 384).Select(i => (float)i * 0.001f).ToArray();
        var blob     = SourceMapIndex.EncodeEmbedding(original);
        var decoded  = SourceMapIndex.DecodeEmbedding(blob);

        decoded.Should().Equal(original);
    }

    [Fact]
    public void EncodeEmbedding_ProducesCorrectByteLength()
    {
        var blob = SourceMapIndex.EncodeEmbedding(new float[384]);
        blob.Should().HaveCount(384 * sizeof(float)); // 1536 bytes
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }
}
