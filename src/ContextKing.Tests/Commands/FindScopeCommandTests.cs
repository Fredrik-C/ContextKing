using ContextKing.Cli.Commands;
using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class FindScopeCommandTests
{
    [Fact]
    public void SelectKeywordHintGroups_PutsExactSymbolsBeforeProviderAndWorkflowHints()
    {
        var results = new[]
        {
            new ScoredFolderDetails(
                "src/Feature/A",
                0.95f,
                0.80f,
                0.10f,
                0f,
                0f,
                2,
                10,
                ["stripe"],
                ["uniquecandidatetoken", "stripe", "refund"]),
            new ScoredFolderDetails(
                "src/Feature/B",
                0.72f,
                0.58f,
                0.06f,
                0f,
                0f,
                4,
                40,
                [],
                ["adyen", "authorization", "gateway"]),
            new ScoredFolderDetails(
                "src/Feature/C",
                0.55f,
                0.50f,
                0.04f,
                0f,
                0.02f,
                6,
                60,
                [],
                ["capture", "notification"])
        };

        var groups = FindScopeCommand.SelectKeywordHintGroups(results, ["stripe", "terminal"], maxHints: 3);

        groups.Symbols.Should().ContainSingle().Which.Should().Be("uniquecandidatetoken");
        groups.Providers.Should().ContainSingle().Which.Should().Be("adyen");
        groups.Workflows.Should().ContainInOrder("refund", "authorization", "gateway");
        groups.All.Should().StartWith("uniquecandidatetoken");
    }
}
