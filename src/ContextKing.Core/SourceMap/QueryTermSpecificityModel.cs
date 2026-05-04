namespace ContextKing.Core.SourceMap;

/// <summary>
/// Computes per-query token weights from corpus document frequency so that
/// ubiquitous structural terms contribute less than rare discriminative terms.
/// </summary>
internal sealed class QueryTermSpecificityModel
{
    private readonly Dictionary<string, float> _weights;
    private readonly float _totalWeight;

    private QueryTermSpecificityModel(Dictionary<string, float> weights, float totalWeight)
    {
        _weights = weights;
        _totalWeight = totalWeight;
    }

    public static QueryTermSpecificityModel Build(
        IReadOnlyList<IndexedFolder> folders,
        IReadOnlyList<string> queryTerms)
        => Build(CorpusTokenStatistics.Build(folders), queryTerms);

    public static QueryTermSpecificityModel Build(
        CorpusTokenStatistics corpus,
        IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count == 0)
            return new QueryTermSpecificityModel(new Dictionary<string, float>(StringComparer.Ordinal), 0f);

        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        var totalWeight = 0f;
        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            var df = corpus.GetDocumentFrequency(term);
            var idf = MathF.Log((corpus.TotalDocuments + 1f) / (df + 1f));
            var support = Math.Clamp(MathF.Log2(df + 1f) / 3f, 0.25f, 1f);
            // Keep bounded positive weights to avoid over-amplifying tiny corpora.
            var weight = Math.Clamp((idf + 0.35f) * support, 0.10f, 2.40f);
            weights[term] = weight;
            totalWeight += weight;
        }

        return new QueryTermSpecificityModel(weights, totalWeight);
    }

    public float ExactMatchFraction(IReadOnlyList<string> matchedTerms)
    {
        if (_totalWeight <= 0f || matchedTerms.Count == 0)
            return 0f;

        var matchedWeight = 0f;
        foreach (var term in matchedTerms)
        {
            if (_weights.TryGetValue(term, out var weight))
                matchedWeight += weight;
        }

        return Math.Clamp(matchedWeight / _totalWeight, 0f, 1f);
    }
}
