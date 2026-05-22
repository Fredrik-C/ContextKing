using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class SourceMapSearcherTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _dbPath;

    public SourceMapSearcherTests()
    {
        WriteIndexedClass("src/Payment/StripeGateway.cs", "StripeGateway", "ProcessStripePayment");
        WriteIndexedClass("src/Payment/RefundService.cs", "RefundService", "RefundPayment");
        WriteIndexedClass("src/Auth/JwtProvider.cs", "JwtProvider", "IssueJwtToken");
        _repo.StageAndCommit();

        new SourceMapBuilder().BuildAsync(_repo.Root).GetAwaiter().GetResult();
        _dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
    }

    [Fact]
    public void Search_TopK_LimitsResultCount()
    {
        var results = Searcher().Search(_dbPath, "payment", topK: 1);
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Search_PaymentQuery_RanksPaymentFilesBeforeAuth()
    {
        var results = Searcher().Search(_dbPath, "stripe payment refund", topK: 10);
        results.Should().NotBeEmpty();
        results.First().Path.Should().Contain("src/Payment/");
    }

    [Fact]
    public void Search_AuthQuery_RanksAuthFirst()
    {
        var results = Searcher().Search(_dbPath, "jwt authentication token", topK: 10);
        results.Should().NotBeEmpty();
        results.First().Path.Should().Contain("src/Auth/");
    }

    [Fact]
    public void Search_MustTerm_BoostsMustMatchingFiles()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway", topK: 10);
        var withMust = Searcher().Search(_dbPath, "payment gateway", topK: 10, mustTerms: ["stripe"]);

        var stripeWithout = withoutMust.First(x => x.Path.Contains("StripeGateway"));
        var stripeWith = withMust.First(x => x.Path.Contains("StripeGateway"));
        stripeWith.Score.Should().BeGreaterThan(stripeWithout.Score);
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var emptyDbPath = Path.Combine(Path.GetTempPath(), "ck-empty-" + Path.GetRandomFileName() + ".db");
        try
        {
            var emptyIndex = new SourceMapIndex(emptyDbPath);
            emptyIndex.EnsureSchema();

            Searcher().Search(emptyDbPath, "payment").Should().BeEmpty();
        }
        finally
        {
            try { File.Delete(emptyDbPath); } catch { }
        }
    }

    private FileMapSearcher Searcher() => new();

    private void WriteIndexedClass(string relativePath, string typeName, string methodName)
    {
        _repo.WriteFile(relativePath, $$"""
            namespace Demo;
            public class {{typeName}}
            {
                public void {{methodName}}() { }
            }
            """);
    }

    public void Dispose() => _repo.Dispose();
}
