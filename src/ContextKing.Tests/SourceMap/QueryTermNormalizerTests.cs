using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class QueryTermNormalizerTests
{
    [Fact]
    public void Expand_Reconciliation_IncludesGenericMorphologicalVariants()
    {
        var variants = QueryTermNormalizer.Expand("reconciliation");

        variants.Should().Contain("reconciliation");
        variants.Should().Contain("reconcile");
    }

    [Fact]
    public void ExpandMany_DeduplicatesVariantsAcrossTerms()
    {
        var variants = QueryTermNormalizer.ExpandMany(["reports", "reporting"]);

        variants.Should().Contain("report");
        variants.Should().OnlyHaveUniqueItems();
    }
}
