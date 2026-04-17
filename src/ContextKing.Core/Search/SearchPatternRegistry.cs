namespace ContextKing.Core.Search;

/// <summary>
/// Resolves <see cref="ISearchPatternProvider"/> instances for file extensions.
/// </summary>
public static class SearchPatternRegistry
{
    private static readonly ISearchPatternProvider[] Providers =
    [
        CSharpSearchPatterns.Instance,
        TypeScriptSearchPatterns.Instance,
    ];

    /// <summary>
    /// Builds a combined regex pattern (for git grep -E) that matches the given
    /// <paramref name="name"/> as the specified <paramref name="type"/> across all
    /// languages with registered providers.
    /// </summary>
    /// <returns>
    /// A single extended-regex pattern combining all language-specific patterns with |,
    /// or null if <paramref name="type"/> is <see cref="SearchType.File"/>.
    /// </returns>
    public static string? BuildPattern(SearchType type, string name)
    {
        if (type == SearchType.File)
            return null;

        var allPatterns = Providers
            .SelectMany(p => p.GetPatterns(type, name))
            .Distinct()
            .ToList();

        return allPatterns.Count switch
        {
            0 => null,
            1 => allPatterns[0],
            _ => string.Join("|", allPatterns.Select(p => $"({p})")),
        };
    }

    /// <summary>
    /// Builds a file-name glob for <see cref="SearchType.File"/> searches.
    /// Returns a pattern like "*Name*" for use with filename matching.
    /// </summary>
    public static string BuildFileGlob(string name) => $"*{name}*";
}
