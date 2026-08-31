using ContextKing.Cli.Commands;
using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class GetKeywordMapCommandTests
{
    [Fact]
    public void BuildKeywordMap_ReturnsSeedScopedRelatedTermsOrderedByDiscriminativeScore()
    {
        var results = new[]
        {
            new ScoredFile(
                "src/A/StripeRefundGateway.cs",
                0.94f,
                4,
                40,
                "stripe refund gateway terminal request entity",
                "ProcessRefund;BuildTerminalRequest",
                "StripeRefundGateway"),
            new ScoredFile(
                "src/B/StripeProcessor.cs",
                0.83f,
                3,
                30,
                "stripe gateway processor response",
                "ProcessPayment",
                "StripeProcessor"),
            new ScoredFile(
                "src/C/RefundNotifications.cs",
                0.79f,
                2,
                20,
                "refund capture notification gateway",
                "EmitRefundNotification",
                "RefundNotifications")
        };

        var map = GetKeywordMapCommand.BuildKeywordMap(
            results,
            ["stripe", "refund"],
            excludedTerms: ["stripe", "refund"],
            perKeyword: 3);

        map.Should().HaveCount(2);
        map[0].Seed.Should().Be("stripe");
        map[0].Related.Should().Contain("terminal");
        map[0].Related.Should().Contain("gateway");
        map[0].Related.Should().NotContain("stripe");

        map[1].Seed.Should().Be("refund");
        map[1].Related.Should().NotBeEmpty();
        map[1].Related.Should().Contain("terminal");
        map[1].Related.Should().NotContain("refund");
        map[1].Related.Should().NotContain("stripe");
    }

    [Fact]
    public void BuildKeywordMap_ReturnsEmptyWhenNoSeeds()
    {
        var map = GetKeywordMapCommand.BuildKeywordMap(
            [],
            [],
            excludedTerms: [],
            perKeyword: 5);

        map.Should().BeEmpty();
    }

    [Fact]
    public void BuildKeywordMap_PrefersTypeNameTermsOverMethodNameTerms()
    {
        var results = new[]
        {
            new ScoredFile(
                "src/Prices/PriceSettlement.cs",
                0.95f,
                1,
                1,
                "price settlement",
                "CalculateAdjustment",
                "PriceSettlement")
        };

        var map = GetKeywordMapCommand.BuildKeywordMap(
            results,
            ["price"],
            excludedTerms: ["price"],
            perKeyword: 3);

        map[0].Related.Should().Contain("settlement");
        map[0].Related[0].Should().Be("settlement");
    }

    [Fact]
    public void SelectSemanticHints_ReturnsAtMostThreeDistinctIndexTerms()
    {
        var semanticResults = new[]
        {
            new ScoredFile("src/Products/ProductRepricer.cs", 0.92f, 1, 1,
                "price repricer rounding", "CalculatePrice", "ProductRepricer"),
            new ScoredFile("src/Orders/OrderAmount.cs", 0.85f, 1, 1,
                "order rounding amount", "CalculateAmount", "OrderAmount")
        };

        var hints = GetKeywordMapCommand.SelectSemanticHints(
            semanticResults,
            queryTerms: ["price", "calculate"],
            relatedHints: ["amount"],
            maxHints: 3);

        hints.Should().HaveCount(3);
        hints.Should().Contain("repricer");
        hints.Should().NotContain("price");
        hints.Should().NotContain("calculate");
        hints.Should().NotContain("amount");
    }
}
