namespace ContextKing.Core.Embedding;

/// <summary>Optional batching capability; existing embedders remain compatible.</summary>
public interface IBatchTextEmbedder : ITextEmbedder
{
    IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts);
}

/// <summary>Retrieval models may require different query and document prefixes.</summary>
public interface IQueryTextEmbedder : ITextEmbedder
{
    float[] EmbedQuery(string text);
}
