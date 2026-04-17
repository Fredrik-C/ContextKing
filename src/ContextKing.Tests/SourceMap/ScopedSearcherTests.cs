using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

/// <summary>
/// Integration tests for ScopedSearcher. Builds a real index from a controlled
/// TempRepo and verifies that semantic scope + git grep works end-to-end.
/// </summary>
public class ScopedSearcherTests : IClassFixture<EmbedderFixture>, IDisposable
{
    private readonly BgeEmbedder _embedder;
    private readonly TempRepo    _repo = new();
    private readonly string      _dbPath;

    public ScopedSearcherTests(EmbedderFixture fixture)
    {
        _embedder = fixture.Embedder;
        _dbPath   = SourceMapBuilder.GetDbPath(_repo.Root);

        // Two semantically distinct domains with real method content for git grep.
        _repo.WriteFile("src/Payment/PaymentService.cs", """
            public class PaymentService
            {
                public async Task<Result> ProcessPayment(PaymentRequest request) { }
                public async Task<Result> RefundPayment(string transactionId) { }
            }
            """);
        _repo.WriteFile("src/Payment/StripeGateway.cs", """
            public class StripeGateway
            {
                public async Task<ChargeResult> CreateCharge(decimal amount) { }
                public async Task<RefundResult> CreateRefund(string chargeId) { }
            }
            """);
        _repo.WriteFile("src/Auth/AuthController.cs", """
            public class AuthController
            {
                public async Task<TokenResponse> Login(LoginRequest request) { }
                public async Task Logout(string sessionId) { }
            }
            """);
        _repo.WriteFile("src/Auth/JwtProvider.cs", """
            public class JwtProvider
            {
                public string GenerateToken(UserClaims claims) { }
                public bool ValidateToken(string token) { }
            }
            """);
        _repo.StageAndCommit();

        new SourceMapBuilder(_embedder).BuildAsync(_repo.Root).GetAwaiter().GetResult();
    }

    [Fact]
    public void Search_FindsKeywordInTopScoredFolder()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment stripe gateway", "CreateCharge", topK: 5);

        result.Matches.Should().NotBeEmpty();
        result.Matches.Should().Contain(m => m.File.Contains("StripeGateway.cs"));
    }

    [Fact]
    public void Search_ReturnsLineNumberAndContent()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment processing", "RefundPayment", topK: 5);

        result.Matches.Should().NotBeEmpty();
        var match = result.Matches.First(m => m.File.Contains("PaymentService.cs"));
        match.Line.Should().BeGreaterThan(0);
        match.Content.Should().Contain("RefundPayment");
    }

    [Fact]
    public void Search_NoMatchReturnsEmptyMatchesButFolders()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment processing", "NonExistentMethod12345", topK: 5);

        result.Matches.Should().BeEmpty();
        result.Folders.Should().NotBeEmpty("semantic search should still return folders");
    }

    [Fact]
    public void Search_CaseInsensitiveByDefault()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment", "refundpayment", topK: 5, ignoreCase: true);

        result.Matches.Should().NotBeEmpty("case-insensitive search should match RefundPayment");
    }

    [Fact]
    public void Search_CaseSensitiveWhenRequested()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment", "refundpayment", topK: 5, ignoreCase: false);

        result.Matches.Should().BeEmpty("case-sensitive search should not match RefundPayment with lowercase query");
    }

    [Fact]
    public void Search_OnlyMatchesInScopedFolders()
    {
        // Searching with auth-specific scope should not return payment results
        var result = Searcher().Search(
            _dbPath, _repo.Root, "jwt authentication token", "GenerateToken", topK: 1);

        result.Folders.Should().HaveCount(1);
        result.Folders[0].Path.Should().Be("src/Auth");
        result.Matches.Should().OnlyContain(m => m.File.Contains("Auth/"));
    }

    [Fact]
    public void Search_FolderScoreIsPopulated()
    {
        var result = Searcher().Search(
            _dbPath, _repo.Root, "payment", "ProcessPayment", topK: 5);

        result.Matches.Should().NotBeEmpty();
        result.Matches.All(m => m.FolderScore > 0).Should().BeTrue();
    }

    private ScopedSearcher Searcher() => new(new SourceMapSearcher(_embedder));

    public void Dispose() => _repo.Dispose();
}
