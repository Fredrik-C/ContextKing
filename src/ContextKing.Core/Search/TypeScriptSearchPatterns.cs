using System.Text.RegularExpressions;

namespace ContextKing.Core.Search;

/// <summary>
/// Generates git grep patterns for TypeScript/JavaScript symbol searches.
/// </summary>
public sealed class TypeScriptSearchPatterns : ISearchPatternProvider
{
    public static readonly TypeScriptSearchPatterns Instance = new();

    public IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx" };

    public IReadOnlyList<string> GetPatterns(SearchType type, string name)
    {
        var escaped = Regex.Escape(name);

        return type switch
        {
            SearchType.Class => [
                // class/interface/type/enum declarations
                $"(class|interface|type|enum)\\s+{escaped}",
            ],

            SearchType.Method => [
                // Function/method declarations and calls: name followed by (
                // Covers: function name(, async name(, name(, name = (
                $"{escaped}\\s*[(<]",
            ],

            SearchType.Member => [
                // Property/variable: name followed by :, =, ;, or ?:
                $"{escaped}\\s*[?]?\\s*[:=;]",
            ],

            SearchType.File => [],  // Handled by filename matching

            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
