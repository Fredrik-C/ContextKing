using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class PathTokenizerTests
{
    // ── TokenizePath ──────────────────────────────────────────────────────────

    [Fact]
    public void TokenizePath_SingleSegment_LowercasesToken()
        => PathTokenizer.TokenizePath("Payment").Should().Be("payment");

    [Fact]
    public void TokenizePath_MultipleSegments_JoinsDistinctTokens()
        => PathTokenizer.TokenizePath("src/Modules/Payment")
            .Should().Be("src modules payment");

    [Fact]
    public void TokenizePath_PascalCase_SplitsAtBoundaries()
        => PathTokenizer.TokenizePath("AdyenFeeCalculator")
            .Should().Be("adyen fee calculator");

    [Fact]
    public void TokenizePath_ConsecutiveUppercase_SplitsCorrectly()
        // "HTTPClient" → "http client"
        => PathTokenizer.TokenizePath("HTTPClient")
            .Should().Be("http client");

    [Fact]
    public void TokenizePath_InterfacePrefix_Stripped()
        => PathTokenizer.TokenizePath("IPaymentGateway")
            .Should().Be("payment gateway");

    [Fact]
    public void TokenizePath_DelimitersHyphenUnderscore_SplitIntoTokens()
        => PathTokenizer.TokenizePath("my-module_v2")
            .Should().Be("my module v2");

    [Fact]
    public void TokenizePath_DuplicateTokensAcrossSegments_Deduplicated()
        // "src/Payment/Payment" — "payment" appears twice
        => PathTokenizer.TokenizePath("src/Payment/Payment")
            .Should().Be("src payment");

    [Fact]
    public void TokenizePath_BackslashSeparator_TreatedSameAsForwardSlash()
        => PathTokenizer.TokenizePath(@"src\Modules\Payment")
            .Should().Be("src modules payment");

    [Fact]
    public void TokenizePath_EmptyPath_ReturnsEmptyString()
        => PathTokenizer.TokenizePath(string.Empty).Should().BeEmpty();

    // ── TokenizeFileName ─────────────────────────────────────────────────────

    [Fact]
    public void TokenizeFileName_RemovesCsExtension()
        => PathTokenizer.TokenizeFileName("StripeService.cs")
            .Should().Be("stripe service");

    [Fact]
    public void TokenizeFileName_InterfacePrefix_Stripped()
        => PathTokenizer.TokenizeFileName("IStripeClient.cs")
            .Should().Be("stripe client");

    [Fact]
    public void TokenizeFileName_NoExtension_TokenisesDirectly()
        => PathTokenizer.TokenizeFileName("FeeCalculator")
            .Should().Be("fee calculator");

    // ── TokenizeQuery ─────────────────────────────────────────────────────────

    [Fact]
    public void TokenizeQuery_WhitespaceSeparated_ProducesIndividualTokens()
        => PathTokenizer.TokenizeQuery("stripe payment")
            .Should().Equal("stripe", "payment");

    [Fact]
    public void TokenizeQuery_PascalCase_SplitsAtBoundaries()
        => PathTokenizer.TokenizeQuery("StripePayment")
            .Should().Equal("stripe", "payment");

    [Fact]
    public void TokenizeQuery_Deduplicates()
        => PathTokenizer.TokenizeQuery("payment stripe payment")
            .Should().Equal("payment", "stripe");

    [Fact]
    public void TokenizeQuery_MultipleWhitespaceTypes_Handled()
        => PathTokenizer.TokenizeQuery("stripe\tpayment\nreconciliation")
            .Should().Equal("stripe", "payment", "reconciliation");

    [Fact]
    public void TokenizeQuery_InterfacePrefix_Stripped()
        => PathTokenizer.TokenizeQuery("IStripeGateway")
            .Should().Equal("stripe", "gateway");
}
