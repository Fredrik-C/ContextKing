using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class FileMapSearcherCoOccurrenceTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _dbPath;

    public FileMapSearcherCoOccurrenceTests()
    {
        // Both files match "terminal" and "refund" in their member blob. Only Together has a
        // single member that does both; Apart spreads the two terms over unrelated members.
        _repo.WriteFile("src/Demo/Together.cs", """
            namespace Demo;
            public class Together
            {
                public void RequestTerminalRefund() { }
            }
            """);
        _repo.WriteFile("src/Demo/Apart.cs", """
            namespace Demo;
            public class Apart
            {
                public void UpdateTerminalSettings() { }
                public void SendRefundReceipt() { }
            }
            """);
        // A constructor repeats its type name, which must not be read as co-occurrence. These two
        // files carry the same path, file, type and member tokens; only the member kind differs.
        _repo.WriteFile("src/Demo/TerminalRefundContext.cs", """
            namespace Demo;
            public class TerminalRefundContext
            {
                public TerminalRefundContext() { }
            }
            """);
        _repo.WriteFile("src/Demo/TerminalRefundHolder.cs", """
            namespace Demo;
            public class TerminalRefundHolder
            {
                public void ApplyTerminalRefund() { }
            }
            """);
        _repo.StageAndCommit();

        new SourceMapBuilder().BuildAsync(_repo.Root).GetAwaiter().GetResult();
        _dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
    }

    [Fact]
    public void Search_TermsInsideOneMember_OutranksTermsSpreadOverMembers()
    {
        var results = new FileMapSearcher().Search(_dbPath, "terminal refund", topK: 10);

        var together = results.Single(x => x.Path.EndsWith("Together.cs", StringComparison.Ordinal));
        var apart = results.Single(x => x.Path.EndsWith("Apart.cs", StringComparison.Ordinal));
        together.Score.Should().BeGreaterThan(apart.Score);
    }

    [Fact]
    public void Search_ConstructorRepeatingTypeName_EarnsNoCoOccurrenceBonus()
    {
        var results = new FileMapSearcher().Search(_dbPath, "terminal refund", topK: 10);

        var constructorOnly = results.Single(x => x.Path.EndsWith("TerminalRefundContext.cs", StringComparison.Ordinal));
        var realMember = results.Single(x => x.Path.EndsWith("TerminalRefundHolder.cs", StringComparison.Ordinal));
        constructorOnly.Score.Should().BeLessThan(realMember.Score);
    }

    [Fact]
    public void Search_SingleTermQuery_IsUnaffected()
    {
        var results = new FileMapSearcher().Search(_dbPath, "refund", topK: 10);

        results.Should().NotBeEmpty();
    }

    public void Dispose() => _repo.Dispose();
}
