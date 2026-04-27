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
            new ScoredFolderDetails(
                "src/A",
                0.94f,
                0.84f,
                0.10f,
                0f,
                0f,
                4,
                40,
                ["stripe", "refund"],
                ["gateway", "terminal", "request", "entity"]),
            new ScoredFolderDetails(
                "src/B",
                0.83f,
                0.73f,
                0.10f,
                0f,
                0f,
                3,
                30,
                ["stripe"],
                ["gateway", "processor", "response"]),
            new ScoredFolderDetails(
                "src/C",
                0.79f,
                0.69f,
                0.10f,
                0f,
                0f,
                2,
                20,
                ["refund"],
                ["capture", "notification", "gateway"])
        };

        var map = GetKeywordMapCommand.BuildKeywordMap(
            results,
            ["stripe", "refund"],
            excludedTerms: ["stripe", "refund"],
            perKeyword: 3);

        map.Should().HaveCount(2);
        map[0].Seed.Should().Be("stripe");
        map[0].Related.Should().Contain("terminal");
        map[0].Related.Should().Contain("processor");
        map[0].Related.Should().NotContain("stripe");

        map[1].Seed.Should().Be("refund");
        map[1].Related.Should().Contain("notification");
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
