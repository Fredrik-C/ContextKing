namespace ContextKing.Core.SourceMap;

/// <summary>
/// Immutable corpus-level token statistics derived from indexed folders.
/// </summary>
internal sealed class CorpusTokenStatistics
{
    private readonly Dictionary<string, int> _documentFrequency;

    private CorpusTokenStatistics(int totalDocuments, Dictionary<string, int> documentFrequency)
    {
        TotalDocuments = totalDocuments;
        _documentFrequency = documentFrequency;
    }

    public int TotalDocuments { get; }

    public IEnumerable<string> Terms => _documentFrequency.Keys;

    public int GetDocumentFrequency(string term)
        => _documentFrequency.TryGetValue(term, out var count) ? count : 0;

    public bool Contains(string term)
        => _documentFrequency.ContainsKey(term);

    public static CorpusTokenStatistics Build(IReadOnlyList<IndexedFolder> folders)
    {
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            var seen = folder.CombinedTokens
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var token in seen)
                documentFrequency[token] = documentFrequency.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        return new CorpusTokenStatistics(Math.Max(1, folders.Count), documentFrequency);
    }
}
