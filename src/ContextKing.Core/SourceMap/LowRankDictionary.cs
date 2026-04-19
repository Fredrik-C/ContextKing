using System.Reflection;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Provides the set of low-rank tokens that are excluded from the exact-match bonus
/// in <see cref="SourceMapSearcher"/>.
///
/// Low-rank tokens are generic terms that appear in a large fraction of leaf folders
/// across any codebase (common CRUD verbs, stopwords, universal repo-layout segments,
/// architectural-pattern names, etc.).  Because they are not discriminative, allowing
/// them to contribute to the exact-match score would inflate scores for wrong folders
/// and dilute the bonus for genuinely informative query terms.
///
/// The canonical list lives in <c>lowrank_dictionary.txt</c>, embedded as an assembly
/// resource.  One token per line; blank lines and <c>#</c>-comments are ignored.
/// </summary>
public static class LowRankDictionary
{
    private static readonly Lazy<HashSet<string>> _tokens = new(Load, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Returns <c>true</c> when <paramref name="token"/> is a low-rank term.</summary>
    public static bool Contains(string token)
        => _tokens.Value.Contains(token);

    /// <summary>Filters <paramref name="queryTerms"/>, removing any low-rank terms.</summary>
    public static IReadOnlyList<string> FilterHighRank(IEnumerable<string> queryTerms)
        => queryTerms.Where(t => !_tokens.Value.Contains(t)).ToList();

    /// <summary>Number of tokens in the dictionary (exposed for diagnostics / tests).</summary>
    public static int Count => _tokens.Value.Count;

    // ── Loading ───────────────────────────────────────────────────────────────

    private static HashSet<string> Load()
    {
        var set            = new HashSet<string>(StringComparer.Ordinal);
        var resourceName   = "ContextKing.Core.SourceMap.lowrank_dictionary.txt";
        var assembly       = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                "Ensure lowrank_dictionary.txt is marked as EmbeddedResource in the .csproj.");

        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            set.Add(trimmed);
        }

        return set;
    }
}
