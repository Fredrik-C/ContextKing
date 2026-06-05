using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class CandidateSemanticRerankerTests
{
    [Fact]
    public void Rerank_EmptyCandidates_ReturnsEmpty()
    {
        var reranker = new CandidateSemanticReranker(new MapEmbedder());

        reranker.Rerank("refund", null, [], 10, new SemanticRerankOptions())
            .Should().BeEmpty();
    }

    [Fact]
    public void Rerank_SemanticScoreCanPromoteLexicallyLowerCandidate()
    {
        var embedder = new MapEmbedder()
            .Map("intent", [1f, 0f])
            .Map("Path: src/A/Top.cs", [-1f, 0f])
            .Map("Path: src/B/Target.cs", [1f, 0f])
            .Map("Path: src/C/Floor.cs", [-1f, 0f]);
        var candidates = new[]
        {
            Hit("src/A/Top.cs", 10f, ["refund"]),
            Hit("src/B/Target.cs", 9f, ["refund"]),
            Hit("src/C/Floor.cs", 0f, ["refund"])
        };

        var result = new CandidateSemanticReranker(embedder)
            .Rerank("lexical", "intent", candidates, 3, new SemanticRerankOptions());

        result[0].Path.Should().Be("src/B/Target.cs");
        result[0].SemanticScore!.Value.Should().BeGreaterThan(result[1].SemanticScore!.Value);
    }

    [Fact]
    public void Rerank_FlatSemanticScoresPreserveLexicalOrdering()
    {
        var embedder = new MapEmbedder()
            .Map("query", [1f, 0f])
            .Map("Path:", [1f, 0f]);
        var candidates = new[]
        {
            Hit("src/A/First.cs", 3f, ["refund"]),
            Hit("src/B/Second.cs", 2f, ["refund"]),
            Hit("src/C/Third.cs", 1f, ["refund"])
        };

        var result = new CandidateSemanticReranker(embedder)
            .Rerank("query", null, candidates, 3, new SemanticRerankOptions());

        result.Select(x => x.Path).Should().Equal(
            "src/A/First.cs",
            "src/B/Second.cs",
            "src/C/Third.cs");
    }

    [Fact]
    public void Rerank_ZeroMatchedTermsCannotJumpToTopFromSemanticSimilarityOnly()
    {
        var embedder = new MapEmbedder()
            .Map("intent", [1f, 0f])
            .Map("Path: src/A/Grounded.cs", [0f, 1f])
            .Map("Path: src/B/Ungrounded.cs", [1f, 0f])
            .Map("Path: src/C/Floor.cs", [-1f, 0f]);
        var candidates = new[]
        {
            Hit("src/A/Grounded.cs", 9f, ["refund"]),
            Hit("src/B/Ungrounded.cs", 8f, []),
            Hit("src/C/Floor.cs", 0f, ["refund"])
        };

        var result = new CandidateSemanticReranker(embedder)
            .Rerank("lexical", "intent", candidates, 3, new SemanticRerankOptions());

        result[0].Path.Should().Be("src/A/Grounded.cs");
    }

    [Fact]
    public void Rerank_MustMatchingCandidateReceivesBoost()
    {
        var embedder = new MapEmbedder()
            .Map("query", [1f, 0f])
            .Map("Path:", [1f, 0f]);
        var candidates = new[]
        {
            Hit("src/A/GenericRefund.cs", 1f, ["refund"]),
            Hit("src/B/AdyenRefund.cs", 1f, ["refund"], typeNames: "AdyenRefundService")
        };

        var result = new CandidateSemanticReranker(embedder)
            .Rerank("query", null, candidates, 2, new SemanticRerankOptions(), ["adyen"]);

        result[0].Path.Should().Be("src/B/AdyenRefund.cs");
    }

    [Fact]
    public void Rerank_GenericPenaltyReducesNoisyCandidateScore()
    {
        var embedder = new MapEmbedder()
            .Map("query", [1f, 0f])
            .Map("Path:", [1f, 0f]);
        var candidates = new[]
        {
            Hit("src/Payment/RefundService.cs", 1f, ["refund"], signatureCount: 10),
            Hit("src/Legacy/RefundGodClass.cs", 1f, ["refund"], signatureCount: 300)
        };

        var result = new CandidateSemanticReranker(embedder)
            .Rerank("query", null, candidates, 2, new SemanticRerankOptions());

        result[0].Path.Should().Be("src/Payment/RefundService.cs");
        result[0].Score.Should().BeGreaterThan(result[1].Score);
    }

    [Fact]
    public void CandidateCard_ToEmbeddingText_TruncatesToMaxLength()
    {
        var card = SearchCandidateCard.FromHit(
            Hit(
                "src/Payment/RefundService.cs",
                1f,
                ["refund"],
                methodNames: new string('x', 100)));

        card.ToEmbeddingText(40).Should().HaveLength(40);
    }

    private static FileSearchHit Hit(
        string path,
        float lexicalScore,
        IReadOnlyList<string> matchedTerms,
        string typeNames = "RefundService",
        string methodNames = "ProcessRefund",
        int signatureCount = 2)
    {
        var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? ".";
        return new FileSearchHit(
            path,
            lexicalScore,
            lexicalScore,
            null,
            1,
            signatureCount,
            folder,
            Path.GetFileName(path),
            typeNames,
            $"Path: {path} Types: {typeNames} Members: {methodNames}",
            methodNames,
            matchedTerms);
    }

    private sealed class MapEmbedder : ITextEmbedder
    {
        private readonly List<(string Prefix, float[] Vector)> _vectors = [];

        public MapEmbedder Map(string prefix, float[] vector)
        {
            _vectors.Add((prefix, vector));
            return this;
        }

        public float[] Embed(string text)
        {
            var match = _vectors.FirstOrDefault(x => text.StartsWith(x.Prefix, StringComparison.Ordinal));
            return match.Vector ?? [0f, 1f];
        }
    }
}
