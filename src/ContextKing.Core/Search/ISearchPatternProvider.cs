namespace ContextKing.Core.Search;

/// <summary>
/// Generates git-grep-compatible regex patterns for a given symbol name and search type.
/// Each supported language implements this interface.
/// </summary>
public interface ISearchPatternProvider
{
    /// <summary>File extensions this provider handles (e.g. ".cs", ".ts").</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>
    /// Returns one or more regex patterns that match declarations or usages of
    /// <paramref name="name"/> for the given <paramref name="type"/>.
    /// Patterns must be valid for <c>git grep -E</c> (extended regex).
    /// </summary>
    IReadOnlyList<string> GetPatterns(SearchType type, string name);
}
