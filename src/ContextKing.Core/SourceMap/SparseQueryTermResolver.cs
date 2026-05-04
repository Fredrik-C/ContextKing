namespace ContextKing.Core.SourceMap;

/// <summary>
/// Resolves sparse query terms to nearby corpus terms using generic lexical similarity.
/// </summary>
internal sealed class SparseQueryTermResolver
{
    private const int SparseDfThreshold = 1;
    private const float MinSimilarity = 0.45f;
    private const int MinResolvedDf = 2;
    private const float HighConfidenceSimilarity = 0.82f;

    public IReadOnlyList<string> Resolve(IReadOnlyList<string> queryTerms, CorpusTokenStatistics corpus)
    {
        if (queryTerms.Count == 0)
            return [];

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            resolved.Add(term);
            if (corpus.GetDocumentFrequency(term) > SparseDfThreshold)
                continue;

            var best = FindBest(term, corpus);
            if (best is not null)
                resolved.Add(best);
        }

        return resolved.ToArray();
    }

    private static string? FindBest(string term, CorpusTokenStatistics corpus)
    {
        var bestTerm = default(string);
        var bestScore = 0f;
        var bestSimilarity = 0f;
        var bestDf = 0;

        foreach (var candidate in corpus.Terms)
        {
            if (candidate.Length < 4)
                continue;

            var similarity = Similarity(term, candidate);
            var support = Support(corpus.GetDocumentFrequency(candidate));
            var score = similarity * support;
            if (score < MinSimilarity || score <= bestScore)
                continue;

            bestScore = score;
            bestSimilarity = similarity;
            bestDf = corpus.GetDocumentFrequency(candidate);
            bestTerm = candidate;
        }

        if (bestTerm is null)
            return null;

        // Avoid resolving to singleton artifacts unless lexical similarity is very high.
        if (bestDf < MinResolvedDf && bestSimilarity < HighConfidenceSimilarity)
            return null;

        return bestTerm;
    }

    private static float Similarity(string a, string b)
    {
        // Blend prefix overlap and bigram Jaccard for robust generic lexical matching.
        var prefix = CommonPrefixLength(a, b) / (float)Math.Max(a.Length, b.Length);
        var jaccard = BigramJaccard(a, b);
        return 0.45f * prefix + 0.55f * jaccard;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i])
            i++;
        return i;
    }

    private static float BigramJaccard(string a, string b)
    {
        var aSet = Bigrams(a);
        var bSet = Bigrams(b);
        if (aSet.Count == 0 || bSet.Count == 0)
            return 0f;

        var intersection = aSet.Intersect(bSet, StringComparer.Ordinal).Count();
        var union = aSet.Count + bSet.Count - intersection;
        return union == 0 ? 0f : intersection / (float)union;
    }

    private static float Support(int documentFrequency)
    {
        if (documentFrequency <= 0)
            return 0.25f;

        return Math.Clamp(0.20f + 0.80f * (MathF.Log2(documentFrequency + 1f) / 4f), 0.25f, 1f);
    }

    private static HashSet<string> Bigrams(string input)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (input.Length < 2)
            return set;

        for (var i = 0; i < input.Length - 1; i++)
            set.Add(input.Substring(i, 2));

        return set;
    }
}
