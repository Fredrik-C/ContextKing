using System.Text.RegularExpressions;

namespace ContextKing.Cli;

/// <summary>
/// Cross-platform glob expansion for file patterns on the command line.
/// Single responsibility: convert a glob (with <c>*</c>, <c>**</c>, <c>?</c>, character classes)
/// into a list of matching files on disk. Kept in the CLI project because it exists to
/// paper over shells (notably PowerShell) that do not expand globs for native executables.
/// </summary>
internal static class GlobMatcher
{
    /// <summary><c>true</c> when <paramref name="value"/> contains glob metacharacters.</summary>
    public static bool IsGlob(string value)
        => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

    /// <summary>
    /// Returns file paths under the deepest non-wildcard ancestor of <paramref name="pattern"/>
    /// that match <paramref name="pattern"/>. Paths are returned relative to the current
    /// working directory when possible, otherwise absolute.
    /// </summary>
    public static List<string> Expand(string pattern)
    {
        var fullPattern     = Path.GetFullPath(pattern);
        var fullPatternNorm = fullPattern.Replace('\\', '/');

        var firstWildcardIndex = fullPatternNorm.IndexOfAny(['*', '?', '[', ']']);
        if (firstWildcardIndex < 0)
            return [pattern];

        var slashBeforeWildcard = fullPatternNorm.LastIndexOf('/', firstWildcardIndex);
        var root = slashBeforeWildcard <= 0
            ? Path.GetPathRoot(fullPattern) ?? Directory.GetCurrentDirectory()
            : fullPatternNorm[..slashBeforeWildcard];

        root = root.Replace('/', Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
            return [];

        var regex = GlobToRegex(fullPatternNorm);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => regex.IsMatch(path.Replace('\\', '/')))
            .Select(path =>
            {
                var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
                return rel.StartsWith("..") ? path : rel;
            })
            .ToList();
    }

    private static Regex GlobToRegex(string pattern)
    {
        // Convert a normalized glob pattern to regex:
        // ** -> any depth, * -> any non-separator chars, ? -> single non-separator char.
        const string DoubleStarToken = "__CK_DOUBLESTAR__";
        var rx = Regex.Escape(pattern)
            .Replace(@"\*\*", DoubleStarToken)
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]")
            .Replace(DoubleStarToken, ".*");

        return new Regex($"^{rx}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
