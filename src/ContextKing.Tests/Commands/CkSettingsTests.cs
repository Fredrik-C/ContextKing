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
        settings.FindFiles.MaxOverfetch.Should().Be(200);
    }

    public void Dispose() => _repo.Dispose();
}
