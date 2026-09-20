namespace ContextKing.Core.SourceMap;

/// <summary>
/// Selects the leading lexical cluster for expensive method-level reranking.
/// Results below a pronounced lexical drop remain available to file-level fusion.
/// </summary>
public static class MethodRerankCandidateSelector
{
    // A quarter-score loss is large enough to separate a leading lexical cluster
    // without treating normal rank decay as a cutoff.
    private const float PronouncedRelativeDrop = 0.25f;
    private const int MinimumCandidates = 2;

    public static IReadOnlyList<FileSearchHit> Select(IReadOnlyList<FileSearchHit> candidates)
    {
        if (candidates.Count <= MinimumCandidates)
            return candidates;

        var ranked = candidates
            .Select((hit, index) => (Hit: hit, Index: index))
            .OrderByDescending(x => x.Hit.LexicalScore)
            .ThenBy(x => x.Index)
            .ToArray();

        for (var index = MinimumCandidates; index < ranked.Length; index++)
        {
            var previous = ranked[index - 1].Hit.LexicalScore;
            var current = ranked[index].Hit.LexicalScore;
            if (previous <= 0 || current >= previous)
                continue;

            var relativeDrop = (previous - current) / previous;
            if (relativeDrop >= PronouncedRelativeDrop)
                return ranked.Take(index).Select(x => x.Hit).ToArray();
        }

        return ranked.Select(x => x.Hit).ToArray();
    }
}
