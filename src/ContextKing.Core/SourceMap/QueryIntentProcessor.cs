namespace ContextKing.Core.SourceMap;

internal enum QueryIntent
{
    Auto = 0,
    Flow = 1,
    Edit = 2,
    Definition = 3,
    Usage = 4
}

internal sealed record ProcessedQuery(
    QueryIntent Intent,
    IReadOnlyList<string> BaseTerms,
    IReadOnlyList<string> ExpandedTerms);

/// <summary>
/// Repository-agnostic query processing:
/// - lightweight intent classification
/// - conservative lexical expansion using universal software vocabulary
/// </summary>
internal static class QueryIntentProcessor
{
    private static readonly string[] UniversalLayerTerms =
    [
        "api", "dto", "mapper", "parser", "factory", "processor", "handler",
        "service", "gateway", "adapter", "client", "repository", "model",
        "entity", "component", "notification", "event", "request", "response"
    ];

    private static readonly string[] DefinitionTerms =
    [
        "dto", "model", "entity", "record", "interface", "contract", "schema"
    ];

    public static ProcessedQuery Process(string query, CorpusTokenStatistics corpus)
    {
        var baseTerms = PathTokenizer.TokenizeQuery(query);
        var intent = Classify(baseTerms);
        var expanded = Expand(baseTerms, intent, corpus);
        return new ProcessedQuery(intent, baseTerms, expanded);
    }

    private static QueryIntent Classify(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return QueryIntent.Auto;

        var t = terms.ToHashSet(StringComparer.Ordinal);

        if (t.Overlaps(["where", "how", "flow", "path", "handled", "handling", "pipeline"]))
            return QueryIntent.Flow;
        if (t.Overlaps(["change", "fix", "update", "modify", "add", "remove", "rename", "implement"]))
            return QueryIntent.Edit;
        if (t.Overlaps(["class", "interface", "record", "enum", "type", "dto", "model", "entity"]))
            return QueryIntent.Definition;
        if (t.Overlaps(["reference", "references", "usage", "used", "calls", "caller", "callers"]))
            return QueryIntent.Usage;

        return QueryIntent.Auto;
    }

    private static IReadOnlyList<string> Expand(IReadOnlyList<string> baseTerms, QueryIntent intent, CorpusTokenStatistics corpus)
    {
        var expanded = new HashSet<string>(baseTerms, StringComparer.Ordinal);

        foreach (var term in baseTerms)
        {
            foreach (var variant in QueryTermNormalizer.Expand(term))
                expanded.Add(variant);
        }

        // Intent-aware layer expansion, but only with terms present in current corpus.
        // This keeps the mechanism repository-agnostic and avoids hardcoded domain bias.
        if (intent is QueryIntent.Flow or QueryIntent.Usage)
        {
            foreach (var term in UniversalLayerTerms.Take(10))
            {
                if (corpus.Contains(term))
                    expanded.Add(term);
            }
        }
        else if (intent is QueryIntent.Definition)
        {
            foreach (var term in DefinitionTerms)
            {
                if (corpus.Contains(term))
                    expanded.Add(term);
            }
        }

        return expanded.ToArray();
    }
}
