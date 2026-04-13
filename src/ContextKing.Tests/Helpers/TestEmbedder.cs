using ContextKing.Core.Embedding;

namespace ContextKing.Tests.Helpers;

/// <summary>
/// Locates the BGE model directory relative to the test assembly and creates
/// <see cref="BgeEmbedder"/> instances for integration tests.
/// </summary>
internal static class TestEmbedder
{
    private static readonly Lazy<string> _modelDir = new(FindModelDir);

    internal static string ModelDir => _modelDir.Value;

    internal static string VocabPath => Path.Combine(ModelDir, "vocab.txt");

    internal static BgeEmbedder Create() => new(ModelDir);

    private static string FindModelDir()
    {
        var env = Environment.GetEnvironmentVariable("CK_MODEL_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        // Walk up from the test assembly to find models/bge-small-en-v1.5
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "models", "bge-small-en-v1.5");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate bge-small-en-v1.5. Set CK_MODEL_DIR env var or run from the repo root.");
    }
}

/// <summary>
/// xUnit class fixture that shares a single <see cref="BgeEmbedder"/> across all
/// tests in a class, avoiding repeated model loads.
/// </summary>
public sealed class EmbedderFixture : IDisposable
{
    public BgeEmbedder Embedder { get; } = TestEmbedder.Create();
    public void Dispose() => Embedder.Dispose();
}
