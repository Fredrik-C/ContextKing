using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

/// <summary>
/// Integration tests for SourceMapSearcher. Builds a real index from a controlled
/// TempRepo and verifies semantic ranking behaviour.
/// </summary>
public class SourceMapSearcherTests : IClassFixture<EmbedderFixture>, IDisposable
{
    private readonly BgeEmbedder _embedder;
    private readonly TempRepo    _repo = new();
    private readonly string      _dbPath;

    public SourceMapSearcherTests(EmbedderFixture fixture)
    {
        _embedder = fixture.Embedder;
        _dbPath   = SourceMapBuilder.GetDbPath(_repo.Root);

        // Build a controlled index with two semantically distinct domains.
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Payment/StripeGateway.cs");
        _repo.WriteFile("src/Auth/AuthController.cs");
        _repo.WriteFile("src/Auth/JwtProvider.cs");
        _repo.StageAndCommit();

        new SourceMapBuilder(_embedder).BuildAsync(_repo.Root).GetAwaiter().GetResult();
    }

    // ── Result count ──────────────────────────────────────────────────────────

    [Fact]
    public void Search_TopK_LimitsResultCount()
    {
        var results = Searcher().Search(_dbPath, "payment", topK: 1);
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Search_TopKGreaterThanFolderCount_ReturnsAllFolders()
    {
        var results = Searcher().Search(_dbPath, "payment", topK: 100);
        results.Should().HaveCount(2); // only 2 folders exist in the index
    }

    // ── Min-score filtering ───────────────────────────────────────────────────

    [Fact]
    public void Search_MinScore_ExcludesFoldersBelowThreshold()
    {
        // Use a threshold just above the lowest score so at least one folder is excluded.
        var all = Searcher().Search(_dbPath, "stripe payment reconciliation", topK: int.MaxValue);
        all.Should().HaveCount(2);

        var lowestScore = all.Min(r => r.Score);
        var threshold   = lowestScore + 0.001f; // just above the weakest result

        var filtered = Searcher().Search(_dbPath, "stripe payment reconciliation",
            topK: int.MaxValue, minScore: threshold);

        filtered.Should().HaveCount(1, "the lowest-scoring folder should be excluded");
        filtered.All(r => r.Score >= threshold).Should().BeTrue();
    }

    [Fact]
    public void Search_MinScoreZero_BehavesLikeNoFilter()
    {
        var withFilter    = Searcher().Search(_dbPath, "payment", topK: int.MaxValue, minScore: 0f);
        var withoutFilter = Searcher().Search(_dbPath, "payment", topK: int.MaxValue);
        withFilter.Should().BeEquivalentTo(withoutFilter);
    }

    [Fact]
    public void Search_MinScoreAboveAllScores_ReturnsEmpty()
    {
        var results = Searcher().Search(_dbPath, "payment", topK: int.MaxValue, minScore: 2.0f);
        results.Should().BeEmpty("no folder can score above the max possible score");
    }

    [Fact]
    public void Search_MinScoreAndTopK_BothApply()
    {
        // Get the scores without any filter to know what to expect.
        var all = Searcher().Search(_dbPath, "stripe payment reconciliation", topK: int.MaxValue);
        var lowestScore = all.Min(r => r.Score);
        var threshold   = lowestScore + 0.001f; // excludes the weakest result

        // topK: 1 + minScore: threshold → should return at most 1 result, all above threshold
        var results = Searcher().Search(_dbPath, "stripe payment reconciliation",
            topK: 1, minScore: threshold);

        results.Should().HaveCountLessOrEqualTo(1);
        results.All(r => r.Score >= threshold).Should().BeTrue();
    }

    // ── Semantic ranking ──────────────────────────────────────────────────────

    [Fact]
    public void Search_PaymentQuery_RanksPaymentFolderFirst()
    {
        var results = Searcher().Search(_dbPath, "stripe payment reconciliation");

        results[0].Path.Should().Be("src/Payment");
    }

    [Fact]
    public void Search_AuthQuery_RanksAuthFolderFirst()
    {
        var results = Searcher().Search(_dbPath, "jwt authentication token");

        results[0].Path.Should().Be("src/Auth");
    }

    [Fact]
    public void Search_ScoresDescending()
    {
        var results = Searcher().Search(_dbPath, "stripe payment", topK: 10);

        for (int i = 1; i < results.Count; i++)
            results[i].Score.Should().BeLessOrEqualTo(results[i - 1].Score);
    }

    // ── Exact-match bonus ─────────────────────────────────────────────────────

    [Fact]
    public void Search_ExactTokenMatch_BoostsRelevantFolder()
    {
        // "payment" is an exact token in src/Payment's combined_tokens.
        // "auth" is an exact token in src/Auth's combined_tokens.
        // Querying "payment" should produce a higher score for Payment than Auth.
        var results = Searcher().Search(_dbPath, "payment");
        var paymentScore = results.First(r => r.Path == "src/Payment").Score;
        var authScore    = results.First(r => r.Path == "src/Auth").Score;

        paymentScore.Should().BeGreaterThan(authScore);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var emptyDbPath = Path.Combine(Path.GetTempPath(), "ck-empty-" + Path.GetRandomFileName() + ".db");
        try
        {
            var emptyIndex = new SourceMapIndex(emptyDbPath);
            emptyIndex.EnsureSchema();

            var results = Searcher().Search(emptyDbPath, "payment");
            results.Should().BeEmpty();
        }
        finally
        {
            try { File.Delete(emptyDbPath); } catch { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SourceMapSearcher Searcher() => new(_embedder);

    public void Dispose() => _repo.Dispose();
}
