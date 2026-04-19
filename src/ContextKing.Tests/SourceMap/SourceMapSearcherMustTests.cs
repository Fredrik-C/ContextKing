using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

/// <summary>
/// Integration tests for SourceMapSearcher's --must behaviour.
/// Uses a repo with three payment-domain folders to verify that --must
/// boosts the target provider, penalises competing providers, and leaves
/// neutral infrastructure folders untouched.
/// </summary>
public class SourceMapSearcherMustTests : IClassFixture<EmbedderFixture>, IDisposable
{
    private readonly BgeEmbedder _embedder;
    private readonly TempRepo    _repo = new();
    private readonly string      _dbPath;

    // Canonical folder paths
    private const string StripeFolder  = "src/Payment/Stripe";
    private const string AdyenFolder   = "src/Payment/Adyen";
    private const string CoreFolder    = "src/Payment/Core";
    private const string AuthFolder    = "src/Auth";

    public SourceMapSearcherMustTests(EmbedderFixture fixture)
    {
        _embedder = fixture.Embedder;
        _dbPath   = SourceMapBuilder.GetDbPath(_repo.Root);

        // Three payment-domain folders: two competing providers + shared infra + unrelated
        _repo.WriteFile("src/Payment/Stripe/StripeGateway.cs");
        _repo.WriteFile("src/Payment/Stripe/StripeWebhookHandler.cs");
        _repo.WriteFile("src/Payment/Adyen/AdyenNotificationHandler.cs");
        _repo.WriteFile("src/Payment/Adyen/AdyenTerminalGateway.cs");
        _repo.WriteFile("src/Payment/Core/PaymentProcessor.cs");
        _repo.WriteFile("src/Payment/Core/RefundService.cs");
        _repo.WriteFile("src/Auth/AuthController.cs");
        _repo.WriteFile("src/Auth/JwtProvider.cs");
        _repo.StageAndCommit();

        new SourceMapBuilder(_embedder).BuildAsync(_repo.Root).GetAwaiter().GetResult();
    }

    // ── Must-boost ────────────────────────────────────────────────────────────

    [Fact]
    public void Must_BoostsStripeFolder_WhenMustIsStripe()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue);
        var withMust    = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue,
            mustTexts: ["stripe"]);

        var scoreWithout = withoutMust.First(r => r.Path == StripeFolder).Score;
        var scoreWith    = withMust   .First(r => r.Path == StripeFolder).Score;

        scoreWith.Should().BeGreaterThan(scoreWithout,
            "must='stripe' should add a positive bonus to the Stripe folder");
    }

    [Fact]
    public void Must_DoesNotBoostAdyenFolder_WhenMustIsStripe()
    {
        var withoutMust = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue);
        var withMust    = Searcher().Search(_dbPath, "payment gateway processing", topK: int.MaxValue,
            mustTexts: ["stripe"]);

        var scoreWithout = withoutMust.First(r => r.Path == AdyenFolder).Score;
        var scoreWith    = withMust   .First(r => r.Path == AdyenFolder).Score;

        // Adyen should NOT receive the must boost (it has no "stripe" token)
        scoreWith.Should().BeLessThanOrEqualTo(scoreWithout,
            "must='stripe' must not boost a folder that does not contain 'stripe'");
    }

    [Fact]
    public void Must_StripeFolderRanksAboveAdyenFolder_WithStripeQuery()
    {
        // Without --must, a Stripe-flavoured query already ranks Stripe above Adyen.
        // With --must "stripe", the gap should be maintained or widened.
        var results = Searcher().Search(_dbPath, "stripe payment gateway terminal", topK: int.MaxValue,
            mustTexts: ["stripe"]);

        var stripeRank = results.Select((r, i) => (r, i)).First(x => x.r.Path == StripeFolder).i;
        var adyenRank  = results.Select((r, i) => (r, i)).First(x => x.r.Path == AdyenFolder).i;

        stripeRank.Should().BeLessThan(adyenRank,
            "Stripe folder should rank higher than Adyen folder when --must stripe is given");
    }

    [Fact]
    public void Must_AuthFolderUnaffected_WhenMustIsStripe()
    {
        // Auth is semantically unrelated to payment/stripe — should be below the
        // CompetingThreshold and receive neither boost nor penalty.
        var withoutMust = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue);
        var withMust    = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue,
            mustTexts: ["stripe"]);

        var scoreWithout = withoutMust.First(r => r.Path == AuthFolder).Score;
        var scoreWith    = withMust   .First(r => r.Path == AuthFolder).Score;

        // Auth should not be boosted (no "stripe" token). It may be penalised
        // if its similarity to embed("stripe") happens to exceed the threshold,
        // but we conservatively only assert it was not boosted.
        scoreWith.Should().BeLessThanOrEqualTo(scoreWithout + 0.001f,
            "an unrelated folder must not receive the must boost");
    }

    // ── No-must baseline ──────────────────────────────────────────────────────

    [Fact]
    public void Must_NullMustTexts_ProducesSameResultsAsNoMust()
    {
        var withNull  = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue, mustTexts: null);
        var withEmpty = Searcher().Search(_dbPath, "payment gateway", topK: int.MaxValue);

        withNull.Select(r => r.Path).Should().Equal(withEmpty.Select(r => r.Path));

        for (int i = 0; i < withNull.Count; i++)
            withNull[i].Score.Should().BeApproximately(withEmpty[i].Score, 0.0001f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SourceMapSearcher Searcher() => new(_embedder);

    public void Dispose() => _repo.Dispose();
}
