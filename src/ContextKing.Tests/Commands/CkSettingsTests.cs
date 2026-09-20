using ContextKing.Cli;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class CkSettingsTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public void Load_MissingFindFilesSection_DefaultsSemanticRerankToTrue()
    {
        _repo.WriteFile(".ck.json", """{ "minVersion": "1.0.0", "brain": true }""");

        var settings = CkSettings.Load(_repo.Root);

        settings.FindFiles.SemanticRerank.Should().BeTrue();
        settings.FindFiles.OverfetchMultiplier.Should().Be(5);
        settings.FindFiles.MinOverfetch.Should().Be(50);
        settings.FindFiles.MaxOverfetch.Should().Be(100);
        settings.FindFiles.MethodRerank.Should().BeTrue();
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void MethodDefaultsAreEnabledAndUseIndependentFusionWeights()
    {
        _repo.WriteFile(".ck.json", """{"findFiles":{"methodRerank":true,"semanticRerank":false,"largeMethodThresholdChars":9000,"largeMethodExcerptChars":700,"maxMethodSourceChars":25000,"denseMethodThreshold":120,"denseMethodExcerptChars":600,"denseMethodMaxCards":240}}""");
        var settings = CkSettings.Load(_repo.Root).FindFiles;
        settings.MethodRerank.Should().BeTrue();
        settings.MaxOverfetch.Should().Be(100);
        settings.OverfetchTopK(20).Should().Be(100);
        settings.ToMethodFusionOptions().LexicalWeight.Should().Be(0.45f);
        settings.ToSemanticOptions().LexicalWeight.Should().Be(0.65f);
        settings.LargeMethodThresholdChars.Should().Be(9000);
        settings.LargeMethodExcerptChars.Should().Be(700);
        settings.MaxMethodSourceChars.Should().Be(25000);
        settings.DenseMethodThreshold.Should().Be(120);
        settings.DenseMethodExcerptChars.Should().Be(600);
        settings.DenseMethodMaxCards.Should().Be(240);
        new FindFilesSettings().MethodRerank.Should().BeTrue();
    }

    [Fact]
    public void InvalidFieldsDoNotResetValidSettingsAndHardCapsApply()
    {
        _repo.WriteFile(".ck.json", """{"findFiles":{"methodRerank":true,"maxMethodsTotal":999999,"methodCandidateFiles":999999,"maxBodyChars":999999,"maxMethodCardChars":999999,"maxMethodsPerFile":999999,"lexicalWeight":"bad","methodSemanticWeight":2,"structuralBoostMax":1,"overfetchMultiplier":"bad"}}""");
        var settings = CkSettings.Load(_repo.Root).FindFiles;
        settings.MethodRerank.Should().BeTrue();
        settings.MethodCandidateFiles.Should().Be(100);
        settings.MaxMethodsPerFile.Should().Be(100);
        settings.MaxMethodsTotal.Should().Be(2000);
        settings.MaxBodyChars.Should().Be(12000);
        settings.MaxMethodCardChars.Should().Be(16000);
        settings.MethodLexicalWeight.Should().Be(0.45f);
        settings.MethodSemanticWeight.Should().Be(1);
        settings.StructuralBoostMax.Should().Be(0.08f);
        settings.OverfetchTopK(int.MaxValue).Should().Be(int.MaxValue);
    }
}
