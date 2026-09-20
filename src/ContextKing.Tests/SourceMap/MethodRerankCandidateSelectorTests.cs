using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class MethodRerankCandidateSelectorTests
{
    [Fact]
    public void Select_StopsAtTheFirstPronouncedLexicalDrop()
    {
        var selected = MethodRerankCandidateSelector.Select([
            Hit("A.cs", 100), Hit("B.cs", 92), Hit("C.cs", 84), Hit("D.cs", 45), Hit("E.cs", 40)
        ]);

        selected.Select(x => x.Path).Should().Equal("A.cs", "B.cs", "C.cs");
    }

    [Fact]
    public void Select_KeepsAllCandidatesWhenLexicalScoresDecayGradually()
    {
        var selected = MethodRerankCandidateSelector.Select([
            Hit("A.cs", 100), Hit("B.cs", 92), Hit("C.cs", 85), Hit("D.cs", 78)
        ]);

        selected.Select(x => x.Path).Should().Equal("A.cs", "B.cs", "C.cs", "D.cs");
    }

    [Fact]
    public void Select_PreservesTwoCandidatesEvenWhenTheFirstGapIsLarge()
    {
        var selected = MethodRerankCandidateSelector.Select([
            Hit("A.cs", 100), Hit("B.cs", 30), Hit("C.cs", 20)
        ]);

        selected.Select(x => x.Path).Should().Equal("A.cs", "B.cs");
    }

    private static FileSearchHit Hit(string path, float lexicalScore) =>
        new(path, lexicalScore, lexicalScore, null, 1, 1, "", path, "", "", "", []);
}
