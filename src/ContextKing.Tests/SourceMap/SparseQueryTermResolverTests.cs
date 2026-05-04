using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class SparseQueryTermResolverTests
{
    [Fact]
    public void Resolve_SparseTerm_AddsClosestCorpusVariant()
    {
        var folders = new List<IndexedFolder>
        {
            new("src/A", [1f], "stripe reconcile report", "stripe reconcile report", 1, 3),
            new("src/B", [1f], "stripe reconcile reports", "stripe reconcile reports", 1, 3)
        };
        var corpus = CorpusTokenStatistics.Build(folders);

        var resolved = new SparseQueryTermResolver().Resolve(["reconciled"], corpus);

        resolved.Should().Contain("reconciled");
        resolved.Should().Contain("reconcile");
    }

    [Fact]
    public void Resolve_DoesNotPromoteLowSupportSingletonArtifact()
    {
        var folders = new List<IndexedFolder>
        {
            new("src/A", [1f], "stripe reconciliator", "stripe reconciliator", 1, 2),
            new("src/B", [1f], "stripe payouts", "stripe payouts", 1, 2)
        };
        var corpus = CorpusTokenStatistics.Build(folders);

        var resolved = new SparseQueryTermResolver().Resolve(["reconcile"], corpus);

        resolved.Should().Contain("reconcile");
        resolved.Should().NotContain("reconciliator");
    }

    [Fact]
    public void Resolve_HighFrequencyKnownTerm_DoesNotRequireBackoff()
    {
        var folders = new List<IndexedFolder>
        {
            new("src/A", [1f], "payment processing", "payment processing", 1, 2),
            new("src/B", [1f], "payment gateway", "payment gateway", 1, 2)
        };
        var corpus = CorpusTokenStatistics.Build(folders);

        var resolved = new SparseQueryTermResolver().Resolve(["payment"], corpus);

        resolved.Should().Equal(["payment"]);
    }
}
