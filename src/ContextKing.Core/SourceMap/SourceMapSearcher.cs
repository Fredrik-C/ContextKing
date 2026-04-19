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

    /// <summary>Score added when a folder explicitly contains a must token.</summary>
    private const float MustBonus = 0.15f;

    /// <summary>
    /// Score subtracted when a folder does NOT contain any must token but its
    /// embedding is very similar to the must embedding — a signal that the folder
    /// is about a competing concept (e.g. Adyen when the must token is "stripe").
    /// </summary>
    private const float CompetingPenalty = 0.20f;

    /// <summary>
    /// Cosine similarity threshold between a folder's embedding and the must
    /// embedding above which a non-must folder is classified as a competing folder.
    /// Conservative default: high enough to only catch clearly related concepts.
    /// </summary>
    private const float CompetingThreshold = 0.82f;

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
    /// <para>
    /// When <paramref name="mustTexts"/> is provided, each text is embedded and averaged into a
    /// "must embedding". Folders whose <c>combined_tokens</c> contain any must token receive a
    /// <see cref="MustBonus"/> boost. Folders that do not contain a must token but whose embedding
    /// similarity to the must embedding exceeds <see cref="CompetingThreshold"/> receive a
    /// <see cref="CompetingPenalty"/> — this automatically suppresses competing-provider folders
    /// (e.g. Adyen folders when the must text is "stripe") without the caller needing to name them.
    /// Semantically neutral folders (generic payment infra, shared utilities) sit below the
    /// threshold and are returned unchanged.
    /// </para>
    /// </summary>
    public IReadOnlyList<ScoredFolder> Search(
        string dbPath,
        string query,
        int topK = 10,
        float minScore = 0f,
        IReadOnlyList<string>? mustTexts = null)
    {
        var folders = new SourceMapIndex(dbPath).LoadIndexedFolders();
        if (folders.Count == 0) return [];

        var queryVec   = embedder.Embed(query);
        var queryTerms = PathTokenizer.TokenizeQuery(query);

        // Strip low-rank terms before computing the exact-match fraction so that
        // generic words (CRUD verbs, stopwords, structural segments, etc.) do not
        // inflate scores for folders that happen to contain them everywhere.
        // Semantic similarity (cosine) still applies to the full query.
        var highRankTerms = LowRankDictionary.FilterHighRank(queryTerms);

        // Prepare must-token state when --must is given.
        float[]? mustEmbedding = null;
        HashSet<string>? mustTermSet = null;
        if (mustTexts is { Count: > 0 })
            (mustEmbedding, mustTermSet) = BuildMustState(mustTexts);

        var scored = new List<ScoredFolder>(folders.Count);
        foreach (var folder in folders)
        {
            var semantic = CosineSimilarity(queryVec, folder.Embedding);
            var exact    = MatchFraction(highRankTerms, folder.CombinedTokens);
            var score    = semantic + ExactMatchBonus * exact;

            if (mustEmbedding is not null && mustTermSet is not null)
                score += MustAdjustment(folder, mustEmbedding, mustTermSet);

            scored.Add(new ScoredFolder(folder.Path, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (minScore > 0f)
            scored.RemoveAll(s => s.Score < minScore);

        return scored.Count <= topK ? scored : scored[..topK];
    }

    // ── Must-token helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Embeds each must text, averages the resulting vectors, and L2-normalises
    /// the average. Also tokenises the must texts for exact-match detection.
    /// </summary>
    private (float[] mustEmbedding, HashSet<string> mustTermSet) BuildMustState(
        IReadOnlyList<string> mustTexts)
    {
        // Embed each text and accumulate
        var dim  = embedder.Embed(mustTexts[0]).Length;
        var avg  = new float[dim];
        foreach (var text in mustTexts)
        {
            var v = embedder.Embed(text);
            for (int i = 0; i < dim; i++)
                avg[i] += v[i];
        }

        // L2-normalise (averaging normalised vectors leaves the sum un-normalised)
        var norm = MathF.Sqrt(avg.Sum(x => x * x));
        if (norm > 0f)
            for (int i = 0; i < dim; i++)
                avg[i] /= norm;

        // Build the exact-match token set for must terms
        var mustTermSet = mustTexts
            .SelectMany(t => PathTokenizer.TokenizeQuery(t))
            .ToHashSet(StringComparer.Ordinal);

        return (avg, mustTermSet);
    }

    /// <summary>
    /// Returns the score adjustment for one folder under the --must constraint:
    /// <list type="bullet">
    ///   <item>+<see cref="MustBonus"/> if the folder contains any must token.</item>
    ///   <item>-<see cref="CompetingPenalty"/> if the folder does not contain a must token
    ///         but is highly similar to the must embedding (competing concept).</item>
    ///   <item>0 for semantically neutral folders.</item>
    /// </list>
    /// </summary>
    private static float MustAdjustment(
        IndexedFolder folder,
        float[] mustEmbedding,
        HashSet<string> mustTermSet)
    {
        var folderTokens = folder.CombinedTokens
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        if (mustTermSet.Any(t => folderTokens.Contains(t)))
            return MustBonus;

        // No must token present — check if this folder is about a competing concept
        var simToMust = CosineSimilarity(mustEmbedding, folder.Embedding);
        return simToMust > CompetingThreshold ? -CompetingPenalty : 0f;
    }

    // ── Scoring helpers ───────────────────────────────────────────────────────

    /// <summary>Fraction of high-rank query terms found in the folder's combined token string [0, 1].
    /// Returns 0 when there are no high-rank terms (avoids dividing by zero and prevents
    /// all-low-rank queries from granting an artificial exact-match boost).</summary>
    private static float MatchFraction(IReadOnlyList<string> highRankTerms, string combinedTokens)
    {
        if (highRankTerms.Count == 0 || string.IsNullOrEmpty(combinedTokens)) return 0f;

        var folderTokens = combinedTokens
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        int matched = 0;
        foreach (var term in highRankTerms)
            if (folderTokens.Contains(term)) matched++;

        return (float)matched / highRankTerms.Count;
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
