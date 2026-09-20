using ContextKing.Core.Embedding;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace ContextKing.Tests.Embedding;

public class CodeEmbeddingModelTests(ITestOutputHelper output)
{
    [CodeModelSmokeFact]
    public void ActualArtifactHasStableNormalizedVectorsAndRetrievesCode()
    {
        using var model = new CodeEmbeddingModel(Environment.GetEnvironmentVariable("CK_CODE_MODEL_TEST_DIR")!);
        var texts = new[]
        {
            "def factorial(n):\n    return 1 if n <= 1 else n * factorial(n - 1)",
            "def render_invoice(invoice):\n    return template.render(invoice)",
            "void RetryTerminalRefund() { try { provider.Refund(terminal); } catch (TimeoutException) { RetryAsync(); } }",
            "void RefundCard() { provider.Refund(card); }"
        };
        var batch = model.EmbedBatch(texts);
        using var assertions = new AssertionScope();
        batch.Should().HaveCount(texts.Length);
        for (var i = 0; i < texts.Length; i++)
        {
            batch[i].Length.Should().Be(model.Manifest.Dimensions);
            batch[i].All(float.IsFinite).Should().BeTrue();
            Math.Sqrt(batch[i].Sum(x => (double)x * x)).Should().BeApproximately(1, 0.00001);
            // Dynamic INT8 scales can vary with the other items in a batch.
            var agreement = Cosine(batch[i], model.Embed(texts[i]));
            output.WriteLine($"batch/single cosine[{i}]={agreement:F6}");
            agreement.Should().BeGreaterThan(model.Manifest.BatchAgreementMinCosine);
        }
        model.Embed(texts[0]).Should().Equal(model.Embed(texts[0]));
        var factorial = model.EmbedQuery("Calculate the factorial of a number");
        Cosine(factorial, batch[0]).Should().BeGreaterThan(Cosine(factorial, batch[1]));
        var refund = model.EmbedQuery("Find terminal refund handling that retries after transient provider errors.");
        Cosine(refund, batch[2]).Should().BeGreaterThan(Cosine(refund, batch[3]));
    }

    [Fact]
    public void NormalizationRejectsInvalidModelOutputs()
    {
        var invalid = () => CodeEmbeddingModel.Normalize([float.NaN, 1]);
        invalid.Should().Throw<InvalidDataException>();
        var zero = () => CodeEmbeddingModel.Normalize([0, 0]);
        zero.Should().Throw<InvalidDataException>();
        var vector = new[] { 3f, 4f };
        CodeEmbeddingModel.Normalize(vector);
        vector.Should().Equal(0.6f, 0.8f);
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b).Sum(x => (double)x.First * x.Second);
}

public sealed class CodeModelSmokeFactAttribute : FactAttribute
{
    public CodeModelSmokeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CK_CODE_MODEL_TEST_DIR")))
            Skip = "Set CK_CODE_MODEL_TEST_DIR to a checksummed optional code model pack to run actual-artifact tests.";
    }
}
