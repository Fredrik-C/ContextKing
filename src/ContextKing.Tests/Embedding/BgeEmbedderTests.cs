using ContextKing.Core.Embedding;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Embedding;

public class BgeEmbedderTests : IDisposable
{
    private readonly BgeEmbedder _embedder = TestEmbedder.Create();

    [Fact]
    public void Embed_Returns384DimensionalVector()
        => _embedder.Embed("stripe payment").Should().HaveCount(384);

    [Fact]
    public void Embed_IsL2Normalised()
    {
        var vec  = _embedder.Embed("reconciliation");
        var norm = MathF.Sqrt(vec.Sum(x => x * x));
        norm.Should().BeApproximately(1f, precision: 1e-5f);
    }

    [Fact]
    public void Embed_SameInputSameOutput()
    {
        var a = _embedder.Embed("payment gateway");
        var b = _embedder.Embed("payment gateway");
        a.Should().Equal(b);
    }

    [Fact]
    public void Embed_DifferentInputsDifferentVectors()
    {
        var a = _embedder.Embed("stripe payment");
        var b = _embedder.Embed("inventory management");
        a.Should().NotEqual(b);
    }

    [Fact]
    public void Embed_SemanticallySimilarInputsHighCosine()
    {
        var a = _embedder.Embed("stripe payment reconciliation");
        var b = _embedder.Embed("stripe payout disbursement");
        var c = _embedder.Embed("user profile authentication");

        float simAB = Dot(a, b);
        float simAC = Dot(a, c);

        // Stripe-related pair should score higher than an unrelated pair
        simAB.Should().BeGreaterThan(simAC);
    }

    [Fact]
    public void Embed_EmptyString_ReturnsNonNullVector()
    {
        var vec = _embedder.Embed(string.Empty);
        vec.Should().HaveCount(384);
    }

    [Fact]
    public void Embed_IsThreadSafe()
    {
        // Run 8 parallel embeds of the same text and verify all produce the same result
        var reference = _embedder.Embed("payment");
        var results   = new float[8][];

        Parallel.For(0, 8, i => results[i] = _embedder.Embed("payment"));

        foreach (var r in results)
            r.Should().Equal(reference);
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public void Dispose() => _embedder.Dispose();
}
