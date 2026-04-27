using ContextKing.Cli.KeywordAtlas;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class SessionKeywordAtlasStoreTests
{
    [Fact]
    public void IsDirectionShift_ReturnsFalseForHighOverlapAndSameMust()
    {
        var atlas = new SessionKeywordAtlas(
            Query: "terminal card-present refund payment interac",
            QueryTerms: ["terminal", "card", "present", "refund", "payment", "interac"],
            MustTerms: ["stripe"],
            MatchedTerms: ["terminal", "refund"],
            UnmatchedTerms: [],
            GlobalHints: ["bbpos"],
            HighValueTerms: ["bbpos"],
            KeywordMap: [new SessionKeywordAtlasEntry("terminal", ["bbpos"])],
            CreatedAtUtc: DateTime.UtcNow);

        var shifted = SessionKeywordAtlasStore.IsDirectionShift(
            atlas,
            "terminal interac refund",
            mustTerms: ["stripe"],
            maxAge: TimeSpan.FromHours(2));

        shifted.Should().BeFalse();
    }

    [Fact]
    public void IsDirectionShift_ReturnsTrueWhenMustChanges()
    {
        var atlas = new SessionKeywordAtlas(
            Query: "terminal card-present refund payment interac",
            QueryTerms: ["terminal", "card", "present", "refund", "payment", "interac"],
            MustTerms: ["stripe"],
            MatchedTerms: ["terminal", "refund"],
            UnmatchedTerms: [],
            GlobalHints: ["bbpos"],
            HighValueTerms: ["bbpos"],
            KeywordMap: [new SessionKeywordAtlasEntry("terminal", ["bbpos"])],
            CreatedAtUtc: DateTime.UtcNow);

        var shifted = SessionKeywordAtlasStore.IsDirectionShift(
            atlas,
            "terminal interac refund",
            mustTerms: ["adyen"],
            maxAge: TimeSpan.FromHours(2));

        shifted.Should().BeTrue();
    }
}
