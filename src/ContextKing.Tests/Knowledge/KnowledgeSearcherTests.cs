using ContextKing.Core.Knowledge;
using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Knowledge;

/// <summary>
/// Integration tests for the knowledge index builder and searcher.
/// Uses a real BGE embedder and a real SQLite DB in a temp repo.
/// </summary>
public sealed class KnowledgeSearcherTests : IClassFixture<EmbedderFixture>, IDisposable
{
    private readonly EmbedderFixture _fixture;
    private readonly TempRepo        _repo = new();

    public KnowledgeSearcherTests(EmbedderFixture fixture) => _fixture = fixture;

    // ── Index build ───────────────────────────────────────────────────────────

    [Fact]
    public void Build_WhenNoSnippetsFile_DoesNothing()
    {
        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);

        var act = () => builder.Build(dbPath, _repo.Root);
        act.Should().NotThrow();
    }

    [Fact]
    public void IsUpToDate_WhenNoFile_ReturnsTrue()
    {
        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);
        builder.IsUpToDate(dbPath, _repo.Root).Should().BeTrue();
    }

    [Fact]
    public void IsUpToDate_AfterBuild_ReturnsTrue()
    {
        WriteSnippet("a1", "Stripe webhooks require HMAC-SHA256 signature validation.");
        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);

        builder.Build(dbPath, _repo.Root);

        builder.IsUpToDate(dbPath, _repo.Root).Should().BeTrue();
    }

    [Fact]
    public void IsUpToDate_AfterSnippetAdded_ReturnsFalse()
    {
        WriteSnippet("a1", "First snippet.");
        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);
        builder.Build(dbPath, _repo.Root);

        WriteSnippet("b2", "Second snippet added later.");

        builder.IsUpToDate(dbPath, _repo.Root).Should().BeFalse();
    }

    [Fact]
    public void IsUpToDate_AfterSessionSnippetFileAdded_ReturnsFalse()
    {
        WriteSnippet("a1", "First snippet.");
        var dbPath = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);
        builder.Build(dbPath, _repo.Root);

        var sessionPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "session-b");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        File.WriteAllText(sessionPath, """
            {"id":"b2","content":"Second snippet.","created_at":"2026-01-01T00:00:00Z"}
            """);

        builder.IsUpToDate(dbPath, _repo.Root).Should().BeFalse();
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public void SearchByQuery_ReturnsRelevantSnippets()
    {
        WriteSnippet("s1", "Interac refunds require card-present because the network mandates terminal authentication.",
            tags: ["interac", "refund", "terminal"]);
        WriteSnippet("s2", "The inventory reservation lock is held for at most 30 seconds before automatic release.",
            tags: ["inventory", "reservation", "lock"]);

        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);
        builder.Build(dbPath, _repo.Root);

        var searcher = new KnowledgeSearcher(_fixture.Embedder);
        var results  = searcher.SearchByQuery(dbPath, "interac refund terminal payment", topK: 5);

        results.Should().NotBeEmpty();
        results[0].Id.Should().Be("s1",
            "the Interac snippet should rank highest for an Interac refund query");
    }

    [Fact]
    public void SearchByQuery_ReturnsEmpty_WhenNoSnippets()
    {
        var dbPath = EnsureDb();
        new SourceMapIndex(dbPath).EnsureKnowledgeSchema();

        var searcher = new KnowledgeSearcher(_fixture.Embedder);
        searcher.SearchByQuery(dbPath, "anything", topK: 5).Should().BeEmpty();
    }

    [Fact]
    public void SearchByQuery_RespectsTopK()
    {
        for (int i = 0; i < 5; i++)
            WriteSnippet($"s{i}", $"Payment gateway snippet number {i} about refunds and settlements.");

        var dbPath  = EnsureDb();
        var builder = new KnowledgeIndexBuilder(_fixture.Embedder);
        builder.Build(dbPath, _repo.Root);

        var searcher = new KnowledgeSearcher(_fixture.Embedder);
        var results  = searcher.SearchByQuery(dbPath, "payment refund gateway", topK: 3);

        results.Should().HaveCount(3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string EnsureDb()
    {
        _repo.WriteFile("src/Placeholder/Placeholder.cs");
        _repo.StageAndCommit();

        var builder = new SourceMapBuilder();
        builder.BuildAsync(_repo.Root).GetAwaiter().GetResult();

        var dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
        new SourceMapIndex(dbPath).EnsureKnowledgeSchema();
        return dbPath;
    }

    private void WriteSnippet(string id, string content,
        IReadOnlyList<string>? tags    = null,
        IReadOnlyList<string>? folders = null)
    {
        var snippet = new KnowledgeSnippet
        {
            Id        = id,
            Content   = content,
            Tags      = tags    ?? [],
            Folders   = folders ?? [],
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
        new KnowledgeStore(_repo.Root).Append(snippet);
    }

    public void Dispose() => _repo.Dispose();
}

