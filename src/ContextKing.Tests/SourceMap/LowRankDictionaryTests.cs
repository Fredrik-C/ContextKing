using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class LowRankDictionaryTests
{
    // ── Contains ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("retry")]
    [InlineData("first")]
    [InlineData("to")]
    [InlineData("and")]
    [InlineData("src")]
    [InlineData("tests")]
    [InlineData("async")]
    [InlineData("result")]
    [InlineData("dto")]
    [InlineData("service")]
    [InlineData("handler")]
    [InlineData("controller")]
    [InlineData("repository")]
    [InlineData("v1")]
    public void Contains_KnownLowRankWords_ReturnsTrue(string token)
        => LowRankDictionary.Contains(token).Should().BeTrue($"'{token}' should be low-rank");

    [Theory]
    [InlineData("payment")]
    [InlineData("reservation")]
    [InlineData("inventory")]
    [InlineData("adyen")]
    [InlineData("stripe")]
    [InlineData("gateway")]
    [InlineData("ledger")]
    [InlineData("fiscal")]
    [InlineData("allocation")]
    public void Contains_DomainSpecificWords_ReturnsFalse(string token)
        => LowRankDictionary.Contains(token).Should().BeFalse($"'{token}' is domain-specific and should NOT be low-rank");

    [Fact]
    public void Contains_IsCaseSensitive_LowercaseOnly()
    {
        // Dictionary entries are all lowercase; uppercase variants must not match.
        LowRankDictionary.Contains("Get").Should().BeFalse();
        LowRankDictionary.Contains("Service").Should().BeFalse();
    }

    [Fact]
    public void Count_IsReasonable()
        // Sanity check: the dictionary must have at least 50 entries and fewer than 500.
        => LowRankDictionary.Count.Should().BeInRange(50, 500);

    // ── FilterHighRank ────────────────────────────────────────────────────────

    [Fact]
    public void FilterHighRank_RemovesLowRankTerms()
    {
        var filtered = LowRankDictionary.FilterHighRank(["get", "payment", "retry", "reservation"]);
        filtered.Should().BeEquivalentTo(["payment", "reservation"],
            "low-rank terms 'get' and 'retry' should be removed");
    }

    [Fact]
    public void FilterHighRank_AllLowRank_ReturnsEmpty()
    {
        var filtered = LowRankDictionary.FilterHighRank(["get", "add", "retry"]);
        filtered.Should().BeEmpty();
    }

    [Fact]
    public void FilterHighRank_AllHighRank_ReturnsAllTerms()
    {
        var filtered = LowRankDictionary.FilterHighRank(["payment", "gateway", "adyen"]);
        filtered.Should().BeEquivalentTo(["payment", "gateway", "adyen"]);
    }

    [Fact]
    public void FilterHighRank_EmptyInput_ReturnsEmpty()
    {
        var filtered = LowRankDictionary.FilterHighRank([]);
        filtered.Should().BeEmpty();
    }
}
