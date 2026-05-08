using ContextKing.Core.Embedding;

namespace ContextKing.Cli;

internal sealed class EmbedderPool : IDisposable
{
    private readonly BgeEmbedder[] _embedders;
    private bool _disposed;

    internal EmbedderPool(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _embedders = Enumerable.Range(0, size)
            .Select(_ => ModelLocator.CreateEmbedder())
            .ToArray();
    }

    internal IReadOnlyList<BgeEmbedder> Embedders => _embedders;
    internal BgeEmbedder Primary => _embedders[0];

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var embedder in _embedders)
            embedder.Dispose();
        _disposed = true;
    }
}
