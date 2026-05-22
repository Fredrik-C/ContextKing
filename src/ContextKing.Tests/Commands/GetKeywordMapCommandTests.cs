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
                "ProcessRefund;BuildTerminalRequest"),
            new ScoredFile(
                "src/B/StripeProcessor.cs",
                0.83f,
                3,
                30,
                "stripe gateway processor response",
                "ProcessPayment"),
            new ScoredFile(
                "src/C/RefundNotifications.cs",
                0.79f,
                2,
                20,
                "refund capture notification gateway",
                "EmitRefundNotification")
        };

        var map = GetKeywordMapCommand.BuildKeywordMap(
            results,
            ["stripe", "refund"],
            excludedTerms: ["stripe", "refund"],
            perKeyword: 3);

        map.Should().HaveCount(2);
        map[0].Seed.Should().Be("stripe");
        map[0].Related.Should().Contain("terminal");
        map[0].Related.Should().Contain("processrefund");
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
}
