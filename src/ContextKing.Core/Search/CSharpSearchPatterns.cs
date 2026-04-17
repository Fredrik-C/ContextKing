using System.Text.RegularExpressions;

namespace ContextKing.Core.Search;

/// <summary>
/// Generates git grep patterns for C# symbol searches.
/// </summary>
public sealed class CSharpSearchPatterns : ISearchPatternProvider
{
    public static readonly CSharpSearchPatterns Instance = new();

    public IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };

    public IReadOnlyList<string> GetPatterns(SearchType type, string name)
    {
        var escaped = Regex.Escape(name);

        return type switch
        {
            SearchType.Class => [
                // class/interface/struct/record/enum declarations
                $"(class|interface|struct|record|enum)\\s+{escaped}",
            ],

            SearchType.Method => [
                // Method declarations and calls: name followed by (
                $"{escaped}\\s*\\(",
            ],

            SearchType.Member => [
                // Property/field: name followed by whitespace and {, ;, =, or type annotation
                $"{escaped}\\s*[{{;=]",
                // Also catch property declarations: Type Name { get; set; }
                $"\\s{escaped}\\s*\\{{",
            ],

            SearchType.File => [],  // File type is handled by filename matching, not grep

            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
