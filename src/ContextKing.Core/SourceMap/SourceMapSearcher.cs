using ContextKing.Core.Embedding;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Scores indexed folders against a query using hybrid semantic + exact-match ranking.
/// Responsibility: query embedding → scoring → ranking.
/// All index access is delegated to <see cref="SourceMapIndex"/>.
/// </summary>
public sealed class SourceMapSearcher(BgeEmbedder embedder)
{
    /// <summary>
    /// Maximum bonus added to the semantic score when all query terms match the
    /// folder's combined tokens. Sized to reliably overcome the semantic advantage
    /// that deep-nested DTO folders gain from long shared path prefixes:
    /// a folder matching one more structural query term (e.g. "controller") should
    /// beat an adjacent-but-wrong folder even when that folder has a higher base
    /// cosine similarity. Empirically, BGE-small produces within-batch score spreads
    /// of 0.03–0.07; this bonus must be larger than that gap for a 1-token difference
    /// to decide the ranking.
    /// </summary>
    private const float ExactMatchBonus = 0.30f;

    /// <summary>
    /// Returns folders most relevant to <paramref name="query"/>, ordered by descending
    /// hybrid score. Score = cosine_similarity + ExactMatchBonus × (matched_query_terms / total_query_terms).
    /// <para>
    /// Filtering is controlled by two independent parameters that can be combined:
    /// <list type="bullet">
    ///   <item><paramref name="topK"/> — hard cap on result count (applied last).</item>
    ///   <item><paramref name="minScore"/> — score threshold; folders below it are excluded (applied first).
    ///         When <paramref name="minScore"/> &gt; 0 and <paramref name="topK"/> is at its default,
    ///         the caller should pass <see cref="int.MaxValue"/> for <paramref name="topK"/> so that
    ///         the threshold is the only filter.</item>
    /// </list>
    /// </para>
    /// </summary>
    public IReadOnlyList<ScoredFolder> Search(
        string dbPath,
        string query,
        int topK = 10,
        float minScore = 0f)
    {
        var folders = new SourceMapIndex(dbPath).LoadIndexedFolders();
        if (folders.Count == 0) return [];

        var queryVec   = embedder.Embed(query);
        var queryTerms = PathTokenizer.TokenizeQuery(query);

        var scored = new List<ScoredFolder>(folders.Count);
        foreach (var folder in folders)
        {
            var semantic = CosineSimilarity(queryVec, folder.Embedding);
            var exact    = MatchFraction(queryTerms, folder.CombinedTokens);
            scored.Add(new ScoredFolder(folder.Path, semantic + ExactMatchBonus * exact));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (minScore > 0f)
            scored.RemoveAll(s => s.Score < minScore);

        return scored.Count <= topK ? scored : scored[..topK];
    }

    // ── Scoring helpers ───────────────────────────────────────────────────────

    /// <summary>Fraction of query terms found in the folder's combined token string [0, 1].</summary>
    private static float MatchFraction(string[] queryTerms, string combinedTokens)
    {
        if (queryTerms.Length == 0 || string.IsNullOrEmpty(combinedTokens)) return 0f;

        var folderTokens = combinedTokens
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        int matched = 0;
        foreach (var term in queryTerms)
            if (folderTokens.Contains(term)) matched++;

        return (float)matched / queryTerms.Length;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }
}
