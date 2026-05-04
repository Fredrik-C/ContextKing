using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class QueryIntentProcessorTests
{
    private static CorpusTokenStatistics BuildCorpus(params string[] tokenRows)
    {
        var folders = tokenRows
            .Select((tokens, i) => new IndexedFolder(
                $"src/F{i}",
                [1f, 0f],
                tokens,
                tokens,
                1,
                tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length))
            .ToArray();

        return CorpusTokenStatistics.Build(folders);
    }

    [Fact]
    public void Process_FlowLikeQuery_AddsLayerAndGenericExpansions()
    {
        var corpus = BuildCorpus(
            "terminal refund processor handler gateway service",
            "notification event request response dto mapper");
        var processed = QueryIntentProcessor.Process("how is terminal refund handled", corpus);

        processed.Intent.Should().Be(QueryIntent.Flow);
        processed.ExpandedTerms.Should().Contain("terminal");
        processed.ExpandedTerms.Should().Contain("refund");
        processed.ExpandedTerms.Should().Contain("processor");
    }

    [Fact]
    public void Process_DefinitionLikeQuery_AddsDefinitionLayerTerms()
    {
        var corpus = BuildCorpus(
            "dto model entity record interface contract schema",
            "request response payload");
        var processed = QueryIntentProcessor.Process("find dto for payment response", corpus);

        processed.Intent.Should().Be(QueryIntent.Definition);
        processed.ExpandedTerms.Should().Contain("dto");
        processed.ExpandedTerms.Should().Contain("model");
        processed.ExpandedTerms.Should().Contain("entity");
    }

    [Fact]
    public void Process_UsesRepositoryAgnosticVocabulary()
    {
        var corpus = BuildCorpus(
            "job queue worker background",
            "error failure exception");
        var processed = QueryIntentProcessor.Process("queue worker failure handling", corpus);

        processed.ExpandedTerms.Should().Contain("queue");
        processed.ExpandedTerms.Should().Contain("worker");
        processed.ExpandedTerms.Should().Contain("failure");
        processed.ExpandedTerms.Should().NotContain("dto");
        processed.ExpandedTerms.Should().NotContain("gateway");
    }

    [Fact]
    public void Process_DoesNotInjectDomainSpecificPaymentSynonyms()
    {
        var corpus = BuildCorpus("terminal refund", "request response");
        var processed = QueryIntentProcessor.Process("terminal refund", corpus);

        processed.ExpandedTerms.Should().NotContain("pos");
        processed.ExpandedTerms.Should().NotContain("inperson");
        processed.ExpandedTerms.Should().NotContain("reversal");
        processed.ExpandedTerms.Should().NotContain("cancel");
    }
}
