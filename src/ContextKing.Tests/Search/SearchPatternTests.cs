using System.Text.RegularExpressions;
using ContextKing.Core.Search;

namespace ContextKing.Tests.Search;

public class SearchPatternTests
{
    [Theory]
    [InlineData("TerminalGateway", "class TerminalGateway")]
    [InlineData("TerminalGateway", "interface TerminalGateway")]
    [InlineData("TerminalGateway", "struct TerminalGateway")]
    [InlineData("TerminalGateway", "record TerminalGateway")]
    [InlineData("TerminalGateway", "enum TerminalGateway")]
    public void CSharp_Class_MatchesDeclarations(string name, string input)
    {
        var patterns = CSharpSearchPatterns.Instance.GetPatterns(SearchType.Class, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Theory]
    [InlineData("TerminalGateway", "var x = new TerminalGateway()")]
    [InlineData("TerminalGateway", "// TerminalGateway is used")]
    public void CSharp_Class_DoesNotMatchUsages(string name, string input)
    {
        var patterns = CSharpSearchPatterns.Instance.GetPatterns(SearchType.Class, name);
        Assert.False(patterns.Any(p => Regex.IsMatch(input, p)), $"Pattern incorrectly matched: {input}");
    }

    [Theory]
    [InlineData("ChargePayment", "public async Task ChargePayment(Request r)")]
    [InlineData("ChargePayment", "await gateway.ChargePayment(request);")]
    [InlineData("ChargePayment", "ChargePayment(")]
    public void CSharp_Method_MatchesDeclarationsAndCalls(string name, string input)
    {
        var patterns = CSharpSearchPatterns.Instance.GetPatterns(SearchType.Method, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Theory]
    [InlineData("RefundAmount", "public decimal RefundAmount { get; set; }")]
    [InlineData("RefundAmount", "RefundAmount = 100;")]
    [InlineData("RefundAmount", "decimal RefundAmount;")]
    public void CSharp_Member_MatchesPropertiesAndFields(string name, string input)
    {
        var patterns = CSharpSearchPatterns.Instance.GetPatterns(SearchType.Member, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Fact]
    public void CSharp_File_ReturnsEmptyPatterns()
    {
        var patterns = CSharpSearchPatterns.Instance.GetPatterns(SearchType.File, "Anything");
        Assert.Empty(patterns);
    }

    [Theory]
    [InlineData("PaymentService", "class PaymentService")]
    [InlineData("PaymentService", "interface PaymentService")]
    [InlineData("PaymentService", "type PaymentService")]
    [InlineData("PaymentService", "enum PaymentService")]
    public void TypeScript_Class_MatchesDeclarations(string name, string input)
    {
        var patterns = TypeScriptSearchPatterns.Instance.GetPatterns(SearchType.Class, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Theory]
    [InlineData("processPayment", "function processPayment(")]
    [InlineData("processPayment", "async processPayment(")]
    [InlineData("processPayment", "await processPayment(")]
    [InlineData("processPayment", "processPayment<T>(")]
    public void TypeScript_Method_MatchesDeclarationsAndCalls(string name, string input)
    {
        var patterns = TypeScriptSearchPatterns.Instance.GetPatterns(SearchType.Method, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Theory]
    [InlineData("amount", "amount: number")]
    [InlineData("amount", "amount = 100;")]
    [InlineData("amount", "amount?: string;")]
    public void TypeScript_Member_MatchesPropertiesAndVariables(string name, string input)
    {
        var patterns = TypeScriptSearchPatterns.Instance.GetPatterns(SearchType.Member, name);
        Assert.True(patterns.Any(p => Regex.IsMatch(input, p)), $"No pattern matched: {input}");
    }

    [Fact]
    public void Registry_BuildPattern_CombinesLanguages()
    {
        var pattern = SearchPatternRegistry.BuildPattern(SearchType.Class, "Foo");
        Assert.NotNull(pattern);
        // Should match both C# and TS class declarations
        Assert.True(Regex.IsMatch("class Foo", pattern));
        Assert.True(Regex.IsMatch("interface Foo", pattern));
        Assert.True(Regex.IsMatch("type Foo", pattern));  // TS-only
    }

    [Fact]
    public void Registry_BuildPattern_ReturnsNullForFile()
    {
        Assert.Null(SearchPatternRegistry.BuildPattern(SearchType.File, "Anything"));
    }

    [Fact]
    public void Registry_BuildFileGlob_ContainsName()
    {
        var glob = SearchPatternRegistry.BuildFileGlob("Gateway");
        Assert.Contains("Gateway", glob);
    }
}
