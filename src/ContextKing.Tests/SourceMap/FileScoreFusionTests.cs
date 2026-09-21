using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class FileScoreFusionTests
{
    private static FileSearchHit Hit(string path, float score) => new(path, score, score, null, 1, 1, "src", path, "Refund", "", "Refund", ["refund"]);
    private static FileMethodScore Method(string path, float score) => new(score,
        new(path, "CSharp", "Refund", "Refund", "void Refund()", "", "", 1, 2, ["call:Refund"]));

    [Fact]
    public void RelevantMethodPromotesFileAndMissingMethodsRemainEligible()
    {
        var candidates = new[] { Hit("a", 10), Hit("b", 9), Hit("c", 0) };
        var methods = new MethodRerankResult(new Dictionary<string, FileMethodScore> { ["a"] = Method("a", 0), ["b"] = Method("b", 1) }, 2, 0, false, false);
        var results = FileScoreFusion.Fuse(candidates, new Dictionary<string, float>(), methods, "refund", 3);
        results[0].Hit.Path.Should().Be("b");
        results.Select(r => r.Hit.Path).Should().Contain("c");
        results.Single(r => r.Hit.Path == "c").Method.Should().BeNull();
    }

    [Fact]
    public void FlatScoresReduceMethodInfluenceAndLexicalOrderBreaksTies()
    {
        var candidates = new[] { Hit("b", 1), Hit("a", 1), Hit("c", 0) };
        var files = candidates.ToDictionary(h => h.Path, h => Method(h.Path, 0.6f));
        var methods = new MethodRerankResult(files, 3, 0, false, false);
        var regular = FileScoreFusion.Fuse(candidates, new Dictionary<string, float>(), methods, "", 3);
        var flat = FileScoreFusion.Fuse(candidates, new Dictionary<string, float>(), methods with { Flat = true }, "", 3);
        flat.Select(r => r.Hit.Path).Should().Equal("b", "a", "c");
        flat[0].Hit.Score.Should().BeGreaterThan(regular[0].Hit.Score);
        flat[2].Hit.Score.Should().BeLessThan(regular[2].Hit.Score);
    }

    [Fact]
    public void CompressedMethodScoresAreSpreadAcrossTheCandidatePool()
    {
        // Cosine-derived scores arrive in a narrow band. Two files with identical lexical scores
        // must still be separated by the full method weight, not by the raw 0.12. With no metadata
        // the method weight is redistributed to 0.40 over a denominator of 0.95.
        var candidates = new[] { Hit("a", 5), Hit("b", 5) };
        var files = new Dictionary<string, FileMethodScore> { ["a"] = Method("a", 0.62f), ["b"] = Method("b", 0.74f) };
        var methods = new MethodRerankResult(files, 2, 0, false, false);

        var results = FileScoreFusion.Fuse(candidates, new Dictionary<string, float>(), methods, "", 2);

        results[0].Hit.Path.Should().Be("b");
        var gap = results[0].Hit.Score - results[1].Hit.Score;
        gap.Should().BeApproximately(0.40f / 0.95f, 0.01f);
    }

    [Fact]
    public void MethodScoresTooCloseToRankAreLeftAlone()
    {
        var candidates = new[] { Hit("a", 5), Hit("b", 5) };
        var files = new Dictionary<string, FileMethodScore> { ["a"] = Method("a", 0.700f), ["b"] = Method("b", 0.705f) };
        var methods = new MethodRerankResult(files, 2, 0, false, false);

        var results = FileScoreFusion.Fuse(candidates, new Dictionary<string, float>(), methods, "", 2);

        (results[0].Hit.Score - results[1].Hit.Score).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void StructuralBoostIsCappedAndCannotRescueVeryLowSimilarity()
    {
        FileScoreFusion.StructuralBonus(Method("Refund", 1), ["refund"], 1).Should().BeApproximately(0.08f, 0.00001f);
        FileScoreFusion.StructuralBonus(Method("Refund", 0.1f), ["refund"], 1).Should().Be(0);
    }
}
