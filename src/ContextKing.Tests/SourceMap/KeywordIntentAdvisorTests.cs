using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class KeywordIntentAdvisorTests
{
    [Fact]
    public void BuildAdvice_ComposesAnchorDiscriminatorWorkflowQuery()
    {
        var dbPath = CreateTestDb(new[]
        {
            new IndexedFolder("src/Payouts/Stripe", [0.9f], "stripe reconciliator payouts transfer refund", "", 3, 20),
            new IndexedFolder("src/Payment/Stripe", [0.8f], "stripe payment processing refunds", "", 5, 30),
            new IndexedFolder("src/Payment/Common", [0.7f], "payment processing transfer reporting", "", 8, 40)
        });

        var advice = KeywordIntentAdvisor.BuildAdvice(
            dbPath,
            queryTerms: ["stripe", "reconciliation", "payouts"],
            matchedTerms: ["stripe", "payouts"],
            mustTerms: [],
            globalHints: ["reconciliator", "transfer", "processing"]);

        advice.SuggestedQueries.Should().NotBeEmpty();
        advice.SuggestedQueries[0].Should().Contain("payouts");
        (advice.SuggestedQueries[0].Contains("stripe", StringComparison.Ordinal) ||
         advice.SuggestedQueries[0].Contains("reconciliation", StringComparison.Ordinal) ||
         advice.SuggestedQueries[0].Contains("payouts", StringComparison.Ordinal))
            .Should().BeTrue();
        if (advice.SuggestedMust is not null)
            new[] { "stripe", "payouts" }.Should().Contain(advice.SuggestedMust);
        advice.SuggestedNextCommand.Should().Contain("ck find-files");
        advice.Terms.Should().Contain(t => t.Role == KeywordRole.Discriminator);
        advice.Terms.Should().Contain(t => t.Term == "stripe" && t.Role == KeywordRole.Anchor);
    }

    [Fact]
    public void BuildAdvice_PreservesExplicitMustAsSuggestedMust()
    {
        var dbPath = CreateTestDb(new[]
        {
            new IndexedFolder("src/A", [0.9f], "adyen terminal capture", "", 1, 8),
            new IndexedFolder("src/B", [0.8f], "stripe gateway payout", "", 1, 8)
        });

        var advice = KeywordIntentAdvisor.BuildAdvice(
            dbPath,
            queryTerms: ["terminal", "capture"],
            matchedTerms: ["terminal"],
            mustTerms: ["adyen"],
            globalHints: ["gateway", "provider"]);

        advice.SuggestedMust.Should().Be("adyen");
        advice.SuggestedNextCommand.Should().Contain("adyen");
    }

    private static string CreateTestDb(IReadOnlyList<IndexedFolder> folders)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ck-keyword-intent-{Guid.NewGuid():N}.db");
        if (File.Exists(path)) File.Delete(path);
        var index = new SourceMapIndex(path);
        index.EnsureSchema();
        var rows = folders
            .Select(f => new FolderRow(
                f.Path,
                f.CombinedTokens,
                f.EmbeddingText,
                SourceMapIndex.EncodeEmbedding(f.Embedding),
                FileHashes: "{}",
                FilenameSet: "",
                f.FileCount,
                f.TokenCount))
            .ToArray();
        index.UpsertFolders(rows);
        return path;
    }
}
