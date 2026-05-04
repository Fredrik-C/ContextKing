namespace ContextKing.Core.SourceMap;

/// <summary>
/// Second-pass reranker that improves scope concentration for ambiguous queries.
/// </summary>
internal sealed class ScopeConcentrationReranker
{
    private const int CandidateWindow = 40;

    public IReadOnlyList<ScoredFolderDetails> Rerank(
        IReadOnlyList<ScoredFolderDetails> scored,
        IReadOnlyList<string> queryTerms)
    {
        if (scored.Count <= 1)
            return scored;

        var windowSize = Math.Min(CandidateWindow, scored.Count);
        var head = scored.Take(windowSize).ToArray();
        var tail = scored.Skip(windowSize).ToArray();

        var domainCounts = head
            .GroupBy(x => DomainKey(x.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var entropy = NormalizedEntropy(domainCounts.Values, windowSize);

        var adjusted = head
            .Select(item =>
            {
                var domain = DomainKey(item.Path);
                var domainFrequency = domainCounts.TryGetValue(domain, out var count) ? count : 1;
                var score = item.Score;
                score += DomainClusterBonus(domainFrequency);
                score += PathPhraseCohesionBonus(item.Path, queryTerms);
                score -= DomainSpreadPenalty(domainFrequency, entropy);
                return item with { Score = score };
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();

        return [.. adjusted, .. tail];
    }

    private static float DomainClusterBonus(int domainFrequency)
    {
        if (domainFrequency <= 1)
            return 0f;

        return Math.Min(0.04f, MathF.Log2(domainFrequency) * 0.01f);
    }

    private static float DomainSpreadPenalty(int domainFrequency, float normalizedEntropy)
    {
        if (domainFrequency > 1 || normalizedEntropy <= 0.70f)
            return 0f;

        // When the head set is very spread out, penalize singleton domains slightly.
        return Math.Min(0.03f, (normalizedEntropy - 0.70f) * 0.10f);
    }

    private static float PathPhraseCohesionBonus(string path, IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count < 2)
            return 0f;

        var query = queryTerms.ToHashSet(StringComparer.Ordinal);
        var maxOverlap = 0;

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = PathTokenizer.TokenizePath(segment)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            var overlap = tokens.Count(query.Contains);
            if (overlap > maxOverlap)
                maxOverlap = overlap;
        }

        if (maxOverlap < 2)
            return 0f;

        return Math.Min(0.06f, 0.035f + (maxOverlap - 2) * 0.01f);
    }

    private static string DomainKey(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("src", StringComparison.OrdinalIgnoreCase))
            return $"{parts[0]}/{parts[1]}/{parts[2]}";

        if (parts.Length >= 2)
            return $"{parts[0]}/{parts[1]}";

        return path;
    }

    private static float NormalizedEntropy(IEnumerable<int> counts, int total)
    {
        var values = counts.Where(c => c > 0).ToArray();
        if (values.Length <= 1 || total <= 1)
            return 0f;

        var entropy = 0f;
        foreach (var c in values)
        {
            var p = c / (float)total;
            entropy -= p * MathF.Log2(p);
        }

        var maxEntropy = MathF.Log2(values.Length);
        return maxEntropy > 0f ? entropy / maxEntropy : 0f;
    }
}
