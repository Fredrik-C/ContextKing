using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class MethodSemanticRerankerTests
{
    private static MethodCandidateCard Card(string member, string file = "src/Refund.cs") =>
        new(file, "CSharp", "Refund", member, $"void {member}()", "calls Retry", "Retry();", 1, 2, ["call:Retry"]);

    [Fact]
    public void EmptyCardsDoNotEmbedQuery()
    {
        var embedder = new FakeEmbedder();
        new MethodSemanticReranker(embedder).Score("intent", []).EmbeddedCount.Should().Be(0);
        embedder.QueryCalls.Should().Be(0);
    }

    [Fact]
    public void UsesBatchAndEmbedsTaskOnce()
    {
        var embedder = new FakeEmbedder();
        var result = new MethodSemanticReranker(embedder).Score("intent", [Card("Good"), Card("Bad", "src/Other.cs")]);
        embedder.QueryCalls.Should().Be(1);
        embedder.BatchCalls.Should().Be(1);
        result.Files["src/Refund.cs"].Score.Should().BeApproximately(1, 0.0001f);
        result.Files["src/Other.cs"].Score.Should().BeApproximately(0, 0.0001f);
        result.Flat.Should().BeFalse();
    }

    [Fact]
    public void FailedBatchIsRetriedAndInvalidVectorsAreNotSyntheticZeroScores()
    {
        var embedder = new FakeEmbedder { FailBatch = true };
        var result = new MethodSemanticReranker(embedder).Score("intent", [Card("Good"), Card("Invalid")]);
        result.EmbeddedCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.Files["src/Refund.cs"].Score.Should().BeApproximately(1, 0.0001f);
    }

    [Fact]
    public void TotalFailureAndCancellationReturnNoMethodScores()
    {
        var result = new MethodSemanticReranker(new FakeEmbedder()).Score("intent", [Card("Invalid")]);
        result.Files.Should().BeEmpty();
        result.FailureCount.Should().Be(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var embedder = new FakeEmbedder();
        result = new MethodSemanticReranker(embedder).Score("intent", [Card("Good")], cancellationToken: cancellation.Token);
        result.Cancelled.Should().BeTrue();
        embedder.QueryCalls.Should().Be(0);
    }

    [Fact]
    public void FlatScoresAreDetected()
    {
        new MethodSemanticReranker(new FakeEmbedder()).Score("intent", [Card("Good"), Card("Good")])
            .Flat.Should().BeTrue();
    }

    [Fact]
    public void AggregationRenormalizesMissingMembersAndIgnoresLongTail()
    {
        var best = new ScoredMethod(Card("Good"), 0.9f);
        MethodSemanticReranker.Aggregate([best]).Score.Should().BeApproximately(0.9f, 0.0001f);
        var second = new ScoredMethod(Card("Second"), 0.6f);
        MethodSemanticReranker.Aggregate([best, second]).Score.Should().BeApproximately(0.8325f, 0.0001f);
        var third = new ScoredMethod(Card("Third"), 0.3f);
        var expected = MethodSemanticReranker.Aggregate([best, second, third]);
        var many = new[] { best, second, third }.Concat(Enumerable.Repeat(new ScoredMethod(Card("Other"), 0), 100)).ToArray();
        MethodSemanticReranker.Aggregate(many).Should().Be(expected);
    }

    [Fact]
    public void BoundingIsDeterministicAndPreservesUnicodeBoundaries()
    {
        var text = string.Concat(Enumerable.Repeat("a😀b", 100));
        for (var cap = 0; cap < 150; cap++)
        {
            var bounded = MethodCandidateCard.Bound(text, cap);
            bounded.Length.Should().BeLessThanOrEqualTo(cap);
            bounded.Should().Be(MethodCandidateCard.Bound(text, cap));
            var encoding = new System.Text.UnicodeEncoding(false, false, true);
            var roundTrip = () => encoding.GetString(encoding.GetBytes(bounded));
            roundTrip.Should().NotThrow();
        }
        MethodCandidateCard.Bound(text, 50).Should().Contain("<omitted>");
    }

    [Fact]
    public void CardTextHonorsBothCapsAndStableLabels()
    {
        var card = Card("Good") with { BodyExcerpt = new string('x', 10000) };
        var text = card.ToEmbeddingText(300, 40);
        text.Length.Should().BeLessThanOrEqualTo(300);
        text.Should().StartWith("Language: CSharp\nPath: src/Refund.cs\nType: Refund\nMember: Good\nSignature:");
        text[(text.IndexOf("Code:\n", StringComparison.Ordinal) + 6)..].Length.Should().BeLessThanOrEqualTo(40);
    }

    private sealed class FakeEmbedder : IBatchTextEmbedder
    {
        public int QueryCalls { get; private set; }
        public int BatchCalls { get; private set; }
        public bool FailBatch { get; init; }
        public float[] Embed(string text)
        {
            if (text == "intent") { QueryCalls++; return [1, 0]; }
            if (text.Contains("Member: Invalid")) return [float.NaN, 0];
            return text.Contains("Member: Good") ? [1, 0] : [-1, 0];
        }
        public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
        {
            BatchCalls++;
            if (FailBatch) throw new InvalidOperationException("Injected batch failure");
            return texts.Select(Embed).ToArray();
        }
    }
}
