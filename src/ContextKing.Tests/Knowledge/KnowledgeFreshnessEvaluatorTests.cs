using ContextKing.Core.Knowledge;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Knowledge;

public sealed class KnowledgeFreshnessEvaluatorTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public void RefreshAll_BackfillsLegacySnippet_AsSchemaV2()
    {
        _repo.WriteFile("src/Payments/CheckoutService.cs", "class CheckoutService { }");
        _repo.StageAndCommit();

        var legacy = new KnowledgeSnippet
        {
            Id = "k1",
            Content = "Legacy entry",
            Folders = ["src/Payments"],
            CreatedAt = "2026-05-25T00:00:00Z",
        };

        var refreshed = new KnowledgeFreshnessEvaluator(_repo.Root).RefreshAll([legacy], out var changed);

        changed.Should().BeTrue();
        refreshed.Should().HaveCount(1);
        refreshed[0].SchemaVersion.Should().Be(2);
        refreshed[0].Fingerprints?.SemanticScopeHash.Should().NotBeNullOrWhiteSpace();
        refreshed[0].Validity?.Status.Should().Be("fresh");
    }

    [Fact]
    public void RefreshAll_KeepsFresh_WhenOnlyBranchChanges()
    {
        _repo.WriteFile("src/Payments/CheckoutService.cs", "class CheckoutService { }");
        _repo.StageAndCommit();

        var snippet = new KnowledgeSnippet
        {
            Id = "k2",
            Content = "Branch-safe knowledge",
            Folders = ["src/Payments"],
            CreatedAt = "2026-05-25T00:00:00Z",
        };

        var evaluator = new KnowledgeFreshnessEvaluator(_repo.Root);
        var baseline = evaluator.RefreshAll([snippet], out _)[0];
        var baselineHash = baseline.Fingerprints?.SemanticScopeHash;

        _repo.Git("checkout -b feature/knowledge-tests");

        var refreshed = evaluator.RefreshAll([baseline], out _)[0];
        refreshed.Validity?.Status.Should().Be("fresh");
        refreshed.Fingerprints?.SemanticScopeHash.Should().Be(baselineHash);
    }

    [Fact]
    public void RefreshAll_MarksReviewNeeded_WhenScopedContentChanges()
    {
        _repo.WriteFile("src/Payments/CheckoutService.cs", "class CheckoutService { }");
        _repo.StageAndCommit();

        var snippet = new KnowledgeSnippet
        {
            Id = "k3",
            Content = "Needs review when payment scope changes",
            Folders = ["src/Payments"],
            CreatedAt = "2026-05-25T00:00:00Z",
        };

        var evaluator = new KnowledgeFreshnessEvaluator(_repo.Root);
        var baseline = evaluator.RefreshAll([snippet], out _)[0];

        _repo.WriteFile("src/Payments/CheckoutService.cs", "class CheckoutService { void Changed() {} }");
        _repo.StageAndCommit();

        var refreshed = evaluator.RefreshAll([baseline], out _)[0];
        refreshed.Validity?.Status.Should().Be("review_needed");
    }

    public void Dispose() => _repo.Dispose();
}

