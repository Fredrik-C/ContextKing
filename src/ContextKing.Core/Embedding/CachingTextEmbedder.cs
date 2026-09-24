namespace ContextKing.Core.Embedding;

/// <summary>
/// Serves card vectors from <see cref="EmbeddingCache"/> and embeds only the misses. Queries are
/// passed straight through: there is one per run, and it must keep the model's query prefix.
/// Does not own the inner embedder, which is shared for the lifetime of the process.
/// </summary>
public sealed class CachingTextEmbedder(ITextEmbedder inner, EmbeddingCache cache)
    : IBatchTextEmbedder, IQueryTextEmbedder
{
    public float[] Embed(string text) => EmbedBatch([text])[0];

    public float[] EmbedQuery(string text) =>
        inner is IQueryTextEmbedder queryEmbedder ? queryEmbedder.EmbedQuery(text) : inner.Embed(text);

    public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];

        var keys = texts.Select(cache.Key).ToArray();
        var known = cache.Get(keys);

        // Distinct: one file's members can repeat across candidate lists, and a repeated text
        // embeds to the same vector, so it is worth exactly one inference.
        var missingKeys = new List<string>(texts.Count);
        var missingTexts = new List<string>(texts.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < texts.Count; i++)
            if (!known.ContainsKey(keys[i]) && seen.Add(keys[i]))
            {
                missingKeys.Add(keys[i]);
                missingTexts.Add(texts[i]);
            }

        if (missingTexts.Count > 0)
        {
            var embedded = inner is IBatchTextEmbedder batchEmbedder
                ? batchEmbedder.EmbedBatch(missingTexts)
                : missingTexts.Select(inner.Embed).ToArray();
            if (embedded.Count != missingTexts.Count)
                throw new InvalidOperationException("Embedder returned a different number of vectors than requested.");

            var fresh = new List<(string, float[])>(missingKeys.Count);
            for (var i = 0; i < missingKeys.Count; i++)
            {
                known[missingKeys[i]] = embedded[i];
                fresh.Add((missingKeys[i], embedded[i]));
            }
            cache.Put(fresh);
        }

        return keys.Select(key => known[key]).ToArray();
    }
}
