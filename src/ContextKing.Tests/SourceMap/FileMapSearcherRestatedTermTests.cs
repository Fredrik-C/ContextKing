using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class FileMapSearcherRestatedTermTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _dbPath;

    public FileMapSearcherRestatedTermTests()
    {
        // Two pairs, each differing only in whether a member name carries "refund".
        // In the first pair the term is already in the file and type name, in the second it is not.
        _repo.WriteFile("src/Demo/RefundAlpha.cs", """
            namespace Demo;
            public class RefundAlpha
            {
                public void RefundAlphaApply() { }
            }
            """);
        _repo.WriteFile("src/Demo/RefundBeta.cs", """
            namespace Demo;
            public class RefundBeta
            {
                public void Apply() { }
            }
            """);
        _repo.WriteFile("src/Demo/Gamma.cs", """
            namespace Demo;
            public class Gamma
            {
                public void RefundApply() { }
            }
            """);
        _repo.WriteFile("src/Demo/Delta.cs", """
            namespace Demo;
            public class Delta
            {
                public void Apply() { }
            }
            """);
        _repo.StageAndCommit();

        new SourceMapBuilder().BuildAsync(_repo.Root).GetAwaiter().GetResult();
        _dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
    }

    [Fact]
    public void Search_MemberRestatingFileName_CountsLessThanMemberIntroducingTheTerm()
    {
        var results = new FileMapSearcher().Search(_dbPath, "refund", topK: 10);

        float Score(string name) => results.Single(x => x.Path.EndsWith(name, StringComparison.Ordinal)).Score;

        // What the member name alone is worth in each pair. Gamma is the only file of the four
        // whose match comes solely from a member, so it also collects the coverage bonus of 1.5
        // that Delta, which matches nothing, does not.
        var restated = Score("RefundAlpha.cs") - Score("RefundBeta.cs");
        var introduced = Score("Gamma.cs") - Score("Delta.cs") - 1.5f;

        restated.Should().BeGreaterThan(0f);
        restated.Should().BeLessThan(introduced);
    }

    public void Dispose() => _repo.Dispose();
}
