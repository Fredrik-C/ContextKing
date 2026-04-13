using System.Text.RegularExpressions;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Converts file-system paths and C# file names into space-separated lowercase token strings
/// suitable for embedding. Handles camelCase, PascalCase, delimiters, and interface 'I' prefix.
/// </summary>
public static partial class PathTokenizer
{
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled)]
    private static partial Regex CamelCaseSplitter();

    [GeneratedRegex(@"[-_\.]", RegexOptions.Compiled)]
    private static partial Regex DelimiterSplitter();

    /// <summary>
    /// Tokenises a relative folder path (e.g. "src/Modules/Payment/Adyen").
    /// Returns unique, ordered tokens from all path segments.
    /// </summary>
    public static string TokenizePath(string relPath)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();

        foreach (var segment in relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var token in SplitSegment(segment))
            {
                if (seen.Add(token))
                    tokens.Add(token);
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Tokenises a free-text query using the same rules as paths and filenames.
    /// Whitespace is treated as a word delimiter; camelCase/PascalCase and
    /// punctuation are split the same way as path segments.
    /// Returns distinct lowercase tokens as a string array.
    /// </summary>
    public static string[] TokenizeQuery(string query)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();

        // Treat whitespace as a word boundary in addition to path separators
        foreach (var segment in query.Split(
            ['/', '\\', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var token in SplitSegment(segment))
            {
                if (seen.Add(token))
                    tokens.Add(token);
            }
        }

        return [.. tokens];
    }

    /// <summary>
    /// Tokenises a single file name (e.g. "AdyenFeeCalculator.cs").
    /// </summary>
    public static string TokenizeFileName(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        return string.Join(' ', SplitSegment(nameWithoutExt));
    }

    private static IEnumerable<string> SplitSegment(string segment)
    {
        // Strip leading interface 'I': IPaymentGateway → PaymentGateway
        if (segment.Length > 2 && segment[0] == 'I' && char.IsUpper(segment[1]))
            segment = segment[1..];

        // Split on explicit delimiters first
        var delimParts = DelimiterSplitter().Split(segment);

        foreach (var part in delimParts)
        {
            if (part.Length == 0) continue;

            // Split by camelCase / PascalCase boundaries
            foreach (var token in CamelCaseSplitter().Split(part))
            {
                var lower = token.ToLowerInvariant();
                if (lower.Length > 0)
                    yield return lower;
            }
        }
    }
}
