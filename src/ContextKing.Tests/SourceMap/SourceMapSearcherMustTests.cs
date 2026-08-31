using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class SourceMapSearcherMustTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _dbPath;

    private const string StripeFile = "src/Payment/Stripe/StripeGateway.cs";
    private const string AdyenFile = "src/Payment/Adyen/AdyenGateway.cs";

    public SourceMapSearcherMustTests()
    {
        WriteIndexedClass(StripeFile, "StripeGateway", "ProcessStripePayment");
        WriteIndexedClass("src/Payment/Stripe/StripeWebhookHandler.cs", "StripeWebhookHandler", "HandleStripeWebhook");
        WriteIndexedClass(AdyenFile, "AdyenGateway", "ProcessAdyenPayment");
        WriteIndexedClass("src/Auth/JwtProvider.cs", "JwtProvider", "IssueJwtToken");
        _repo.StageAndCommit();

        new SourceMapBuilder().BuildAsync(_repo.Root).GetAwaiter().GetResult();
        _dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
    }

    [Fact]
    public void Must_BoostsStripeFile_WhenMustIsStripe()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue);
        var withMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue, mustTerms: ["stripe"]);

        var scoreWithout = withoutMust.First(r => r.Path == StripeFile).Score;
        var scoreWith = withMust.First(r => r.Path == StripeFile).Score;
        scoreWith.Should().BeGreaterThan(scoreWithout);
    }

    [Fact]
    public void Must_DoesNotBoostAdyenFile_WhenMustIsStripe()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue);
        var withMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue, mustTerms: ["stripe"]);

        var scoreWithout = withoutMust.First(r => r.Path == AdyenFile).Score;
        var scoreWith = withMust.First(r => r.Path == AdyenFile).Score;
        scoreWith.Should().BeLessThanOrEqualTo(scoreWithout);
    }

    [Fact]
    public void Must_NullMustTerms_ProducesSameOrderingAsNoMust()
    {
        var withNull = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue, mustTerms: null);
        var withEmpty = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue);

        withNull.Select(x => x.Path).Should().Equal(withEmpty.Select(x => x.Path));
    }

    [Fact]
    public void Must_MultiWordText_BoostsEachKeyword()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue);
        var withMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue, mustTerms: ["stripe payment"]);

        var stripeBoost = withMust.First(r => r.Path == StripeFile).Score - withoutMust.First(r => r.Path == StripeFile).Score;
        var adyenBoost = withMust.First(r => r.Path == AdyenFile).Score - withoutMust.First(r => r.Path == AdyenFile).Score;

        stripeBoost.Should().BeGreaterThan(adyenBoost);
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
