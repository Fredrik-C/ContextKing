using ContextKing.Core.SourceMap;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class QueryTermSpecificityModelTests
{
    [Fact]
    public void ExactMatchFraction_DownweightsUbiquitousTermsComparedToRareTerms()
    {
        var folders = new List<IndexedFolder>
        {
            new("src/Payment/Core", [1f], "payment integration id", "payment integration id", 1, 3),
            new("src/Payment/Other", [1f], "payment integration id", "payment integration id", 1, 3),
            new("src/Payment/Rare", [1f], "payment integration networktxreference", "payment integration networktxreference", 1, 3)
        };

        var model = QueryTermSpecificityModel.Build(
            folders,
            ["payment", "integration", "id", "networktxreference"]);

        var genericPayment = model.ExactMatchFraction(["payment"]);
        var genericIntegration = model.ExactMatchFraction(["integration"]);
        var genericId = model.ExactMatchFraction(["id"]);
        var onlyRare = model.ExactMatchFraction(["networktxreference"]);

        onlyRare.Should().BeGreaterThan(genericPayment,
            "a rare discriminative term should carry more match signal than each ubiquitous structural term");
        onlyRare.Should().BeGreaterThan(genericIntegration);
        onlyRare.Should().BeGreaterThan(genericId);
    }

    [Fact]
    public void ExactMatchFraction_EmptyMatchedTerms_ReturnsZero()
    {
        var model = QueryTermSpecificityModel.Build(
            [new IndexedFolder("src/Payment", [1f], "payment", "payment", 1, 1)],
            ["payment"]);

        model.ExactMatchFraction([]).Should().Be(0f);
    }
}
