using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class ScopeConcentrationRerankerTests
{
    [Fact]
    public void Rerank_PrefersConcentratedDomain_WhenHeadIsHighlySpread()
    {
        var reranker = new ScopeConcentrationReranker();
        var scored = new[]
        {
            New("src/Modules/A/FeatureOne", 1.000f),
            New("src/Modules/B/OnlyOne", 1.002f),
            New("src/Modules/A/FeatureTwo", 0.999f),
            New("src/Modules/C/OnlyOne", 1.001f),
            New("src/Modules/A/FeatureThree", 0.998f)
        };

        var reranked = reranker.Rerank(scored, ["stripe", "payouts", "reporting"]);

        reranked[0].Path.Should().StartWith("src/Modules/A/");
    }

    [Fact]
    public void Rerank_BoostsPathPhraseCohesion_WhenMultipleTermsShareSegment()
    {
        var reranker = new ScopeConcentrationReranker();
        var scored = new[]
        {
            New("src/Modules/PaymentProcessing/StripePayoutsReporting", 1.000f),
            New("src/Modules/PaymentProcessing/Stripe/Reports", 1.000f)
        };

        var reranked = reranker.Rerank(scored, ["stripe", "payouts", "reporting"]);

        reranked[0].Path.Should().Be("src/Modules/PaymentProcessing/StripePayoutsReporting");
    }

    private static ScoredFolderDetails New(string path, float score)
        => new(path, score, 0f, 0f, 0f, 0f, 1, 1, [], [], "");
}
