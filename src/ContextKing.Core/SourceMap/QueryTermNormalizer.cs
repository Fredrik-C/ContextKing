namespace ContextKing.Core.SourceMap;

/// <summary>
/// Produces generic lexical variants for query terms without domain-specific mappings.
/// </summary>
internal static class QueryTermNormalizer
{
    public static IReadOnlyList<string> Expand(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var source = term.Trim().ToLowerInvariant();
        var variants = new HashSet<string>(StringComparer.Ordinal) { source };

        AddSuffixVariant(variants, source, "ing");
        AddSuffixVariant(variants, source, "ed");
        AddSuffixVariant(variants, source, "tion");
        AddSuffixVariant(variants, source, "ation");
        AddSuffixVariant(variants, source, "ment");
        AddPluralVariant(variants, source);

        return variants.ToArray();
    }

    public static IReadOnlyList<string> ExpandMany(IEnumerable<string> terms)
        => terms.SelectMany(Expand)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void AddSuffixVariant(HashSet<string> variants, string source, string suffix)
    {
        if (!source.EndsWith(suffix, StringComparison.Ordinal) || source.Length <= suffix.Length + 2)
            return;

        var root = source[..^suffix.Length];
        variants.Add(root);

        if (suffix is "tion" or "ation")
        {
            variants.Add(root + "e");
            if (root.EndsWith("i", StringComparison.Ordinal) && root.Length > 2)
                variants.Add(root[..^1] + "e");
        }
    }

    private static void AddPluralVariant(HashSet<string> variants, string source)
    {
        if (source.Length <= 3)
            return;

        if (source.EndsWith("ies", StringComparison.Ordinal))
        {
            variants.Add(source[..^3] + "y");
            return;
        }

        if (source.EndsWith("s", StringComparison.Ordinal) && !source.EndsWith("ss", StringComparison.Ordinal))
            variants.Add(source[..^1]);
    }
}
