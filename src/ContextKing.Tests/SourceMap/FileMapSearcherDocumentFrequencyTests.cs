using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class FileMapSearcherDocumentFrequencyTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _dbPath;

    public FileMapSearcherDocumentFrequencyTests()
    {
        // "Settlement" occurs in five member names and nowhere in any path or file name;
        // "Quibble" occurs in one. Neither term appears in a path, so document frequency
        // can only distinguish them if member tokens are counted case-insensitively.
        for (var i = 1; i <= 5; i++)
            WriteIndexedClass($"src/Demo/Alpha{i}.cs", $"Alpha{i}", "ProcessSettlement");
        WriteIndexedClass("src/Demo/Beta.cs", "Beta", "ProcessQuibble");
        _repo.StageAndCommit();

        new SourceMapBuilder().BuildAsync(_repo.Root).GetAwaiter().GetResult();
        _dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
    }

    [Fact]
    public void Search_TermCommonInMemberNames_ScoresLowerThanRareTerm()
    {
        var results = new FileMapSearcher().Search(_dbPath, "settlement quibble", topK: 10);

        var rare = results.Single(x => x.Path.Contains("Beta"));
        var common = results.First(x => x.Path.Contains("Alpha"));
        rare.Score.Should().BeGreaterThan(common.Score);
        results.First().Path.Should().Contain("Beta");
    }

    private void WriteIndexedClass(string relativePath, string typeName, string methodName)
    {
        _repo.WriteFile(relativePath, $$"""
            namespace Demo;
            public class {{typeName}}
            {
                public void {{methodName}}() { }
            }
            """);
    }

    public void Dispose() => _repo.Dispose();
}
