using ContextKing.Core;
using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;
using ContextKing.Core.SourceMap;
using ContextKing.Cli.KeywordAtlas;
using System.Text.RegularExpressions;

namespace ContextKing.Cli.Commands;

/// <summary>
/// ck expand-folder — lists all source files in a folder with their signatures,
/// optionally filtered by a regex pattern. Files that have no matching signatures
/// are excluded from output.
/// </summary>
internal static class ExpandFolderCommand
{
    private const int MaxFilesWithoutPattern = 30;
    private const int MaxMatchedFiles = 8;
    private const int MaxMatchedSignatures = 50;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int DefaultMaxSignaturesPerFile = 25;

    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        var pattern     = reader.GetString("--pattern");
        var allowBroad  = reader.HasFlag("--all");
        var limitSet = reader.TryGetInt("--limit", out var limit) && limit > 0;
        if (!limitSet) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;
        var offsetSet = reader.TryGetInt("--offset", out var offset) && offset >= 0;
        if (!offsetSet) offset = 0;
        var maxSignaturesSet = reader.TryGetInt("--max-signatures", out var maxSignaturesPerFile) && maxSignaturesPerFile >= 0;
        if (!maxSignaturesSet) maxSignaturesPerFile = DefaultMaxSignaturesPerFile;
        var positionals = reader.RemainingPositionals();
        var folderPath  = positionals.Count > 0 ? positionals[0] : null;

        if (folderPath is null)
        {
            Console.Error.WriteLine("[ck expand-folder] Error: folder path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        folderPath = folderPath.TrimEnd('/', '\\');

        if (!Directory.Exists(folderPath))
        {
            Console.Error.WriteLine($"[ck expand-folder] Error: directory not found: '{folderPath}'");
            return Task.FromResult(1);
        }

        Regex? filterRegex = null;
        if (pattern is not null)
        {
            // Normalize shell-escaped alternation: agents often write "Foo\|Bar" in bash
            // double-quoted strings. In .NET regex \| means a literal pipe, not alternation.
            // Treat \| as | so the intuitive meaning works.
            var normalizedPattern = pattern.Replace(@"\|", "|");
            try
            {
                filterRegex = new Regex(normalizedPattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"[ck expand-folder] Error: invalid pattern '{pattern}': {ex.Message}");
                return Task.FromResult(1);
            }
        }

        var allFiles = Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(SupportedLanguages.IsSupported)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allFiles.Count == 0)
        {
            Console.Error.WriteLine($"[ck expand-folder] No supported source files found in '{folderPath}'");
            return Task.FromResult(0);
        }

        // Guard against accidentally passing a top-level or very broad folder.
        // At this scale, ck find-scope is the right starting point.
        if (allFiles.Count > 300)
        {
            Console.Error.WriteLine(
                $"[ck expand-folder] WARNING: '{folderPath}' contains {allFiles.Count} source files — " +
                "this is too broad for expand-folder.");
            if (pattern is null && !allowBroad)
            {
                Console.Error.WriteLine(
                    "[ck expand-folder] Run 'ck find-scope --query \"<what you are looking for>\"' " +
                    "to narrow to a relevant sub-folder first, then expand that sub-folder.");
                return Task.FromResult(1);
            }
            if (!allowBroad)
                Console.Error.WriteLine(
                    "[ck expand-folder] Proceeding with --pattern filter, but will refuse if the matched output is still broad.");
        }

        if (pattern is null && allFiles.Count > MaxFilesWithoutPattern && !allowBroad)
        {
            Console.Error.WriteLine(
                $"[ck expand-folder] Refusing unfiltered expansion of {allFiles.Count} files in '{folderPath}'.");
            Console.Error.WriteLine(
                "[ck expand-folder] Add --pattern with at least one precise symbol/domain word, " +
                "or rerun ck find-scope with a narrower query. Use --all only when broad output is intentional.");
            return Task.FromResult(1);
        }

        // Capture all signature output into a string buffer
        var csFiles = allFiles.Where(SupportedLanguages.IsCSharp).ToList();
        var tsFiles = allFiles.Where(SupportedLanguages.IsTypeScript).ToList();

        var captured = new StringWriter();
        if (csFiles.Count > 0)
            SignatureExtractor.Extract(csFiles, captured, Console.Error);
        if (tsFiles.Count > 0)
            TsSignatureExtractor.Extract(tsFiles, captured, Console.Error);

        // Parse captured lines and group by normalised file path.
        // Line format: filepath:line\tcontainingType\tmemberName\tsignature
        var byFile = new Dictionary<string, List<SignatureEntry>>(StringComparer.Ordinal);

        foreach (var raw in captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var firstTab = line.IndexOf('\t');
            if (firstTab < 0) continue;

            var location = line[..firstTab];          // "filepath:line"
            var rest     = line[(firstTab + 1)..];    // "containingType\tmemberName\tsignature"

            var lastColon = location.LastIndexOf(':');
            if (lastColon <= 0) continue;

            var filePath = location[..lastColon];
            if (!int.TryParse(location[(lastColon + 1)..], out var lineNum)) continue;

            if (!byFile.TryGetValue(filePath, out var entries))
                byFile[filePath] = entries = [];

            entries.Add(new SignatureEntry(lineNum, rest));
        }

        var patternTerms = pattern is null ? [] : PatternTerms(pattern);
        var atlas = SessionKeywordAtlasStore.LoadForFolder(folderPath);

        if (!allowBroad &&
            pattern is not null &&
            allFiles.Count > 40 &&
            patternTerms.Count < 2)
        {
            Console.Error.WriteLine("[ck expand-folder] Pattern is under-specified for this folder size; returning paged shortlist.");
            Console.Error.WriteLine($"[ck expand-folder] matched-pattern-keywords: {FormatList(patternTerms)}");
            var atlasHints = AtlasKeywordHints(atlas, patternTerms, 16);
            Console.Error.WriteLine($"[ck expand-folder] add-keyword-hints: {FormatList(atlasHints)}");
            Console.Error.WriteLine("[ck expand-folder] Use at least two precise terms (for example concept + workflow, or provider + symbol).");
        }

        var matchedByFile = new List<(string File, List<SignatureEntry> Entries)>();
        foreach (var file in allFiles)
        {
            var normalized = file.Replace('\\', '/');
            if (!byFile.TryGetValue(normalized, out var entries) || entries.Count == 0)
                continue;

            // Apply pattern filter against "containingType\tmemberName\tsignature"
            var matched = filterRegex is null
                ? entries
                : entries.Where(e => filterRegex.IsMatch(e.rest)).ToList();

            if (matched.Count == 0)
                continue;

            matchedByFile.Add((normalized, matched));
        }

        if (matchedByFile.Count == 0)
        {
            // Print to stdout (not stderr) so the agent sees this even when stderr is suppressed.
            Console.WriteLine(
                filterRegex is not null
                    ? $"[ck expand-folder] No signatures matched pattern '{pattern}' in '{folderPath}' ({allFiles.Count} files scanned)"
                    : $"[ck expand-folder] No signatures found in '{folderPath}'");

            var hints = KeywordHints(byFile.SelectMany(kvp => kvp.Value), patternTerms);
            var mergedHints = MergeHints(hints, AtlasKeywordHints(atlas, patternTerms, 16), 20);
            if (mergedHints.Count > 0)
                Console.WriteLine($"[ck expand-folder] keyword-hints: {string.Join(", ", mergedHints)}");

            return Task.FromResult(0);
        }

        var matchedSignatureCount = matchedByFile.Sum(x => x.Entries.Count);
        var tooBroad = !allowBroad && IsTooBroad(pattern, patternTerms, allFiles.Count, matchedByFile.Count, matchedSignatureCount);
        if (tooBroad)
        {
            Console.Error.WriteLine("[ck expand-folder] Pattern is too broad for safe expansion. Returning paged shortlist.");
            Console.Error.WriteLine($"[ck expand-folder] matched-files={matchedByFile.Count} matched-signatures={matchedSignatureCount} scanned-files={allFiles.Count}");
            Console.Error.WriteLine($"[ck expand-folder] matched-pattern-keywords: {FormatList(patternTerms)}");
            var hints = KeywordHints(matchedByFile.SelectMany(x => x.Entries), patternTerms);
            var mergedHints = MergeHints(hints, AtlasKeywordHints(atlas, patternTerms, 20), 24);
            Console.Error.WriteLine($"[ck expand-folder] add-keyword-hints: {FormatList(mergedHints)}");
            Console.Error.WriteLine("[ck expand-folder] Rerun with a more precise --pattern, for example combine provider + workflow + DTO/method term. Use --all only when broad output is intentional.");
        }

        var rankedMatches = RankMatches(matchedByFile, patternTerms);
        var page = rankedMatches.Skip(offset).Take(limit).ToArray();
        var hasMore = offset + page.Length < rankedMatches.Count;
        var nextOffset = hasMore ? offset + page.Length : -1;
        Console.Error.WriteLine(
            $"[ck expand-folder] pagination: offset={offset} limit={limit} returned={page.Length} total_estimate={rankedMatches.Count} has_more={hasMore.ToString().ToLowerInvariant()}" +
            (hasMore ? $" next_offset={nextOffset}" : string.Empty));

        // Emit results in ranked order.
        bool anyOutput = false;
        foreach (var (normalized, matched) in page)
        {
            if (anyOutput)
                Console.WriteLine();

            Console.WriteLine(normalized);
            var entries = maxSignaturesPerFile == 0
                ? matched
                : matched.Take(maxSignaturesPerFile).ToList();
            foreach (var (lineNo, rest) in entries)
                Console.WriteLine($"  {lineNo}\t{rest}");
            if (maxSignaturesPerFile > 0 && matched.Count > maxSignaturesPerFile)
                Console.WriteLine($"  ... +{matched.Count - maxSignaturesPerFile} signatures (use --max-signatures 0 for full per-file output)");

            anyOutput = true;
        }

        return Task.FromResult(0);
    }

    private static bool IsTooBroad(
        string? pattern,
        IReadOnlyList<string> patternTerms,
        int scannedFiles,
        int matchedFiles,
        int matchedSignatures)
    {
        if (pattern is null)
            return scannedFiles > MaxFilesWithoutPattern;

        if (patternTerms.Count == 0 && scannedFiles > 8)
            return true;

        return matchedFiles > MaxMatchedFiles || matchedSignatures > MaxMatchedSignatures;
    }

    private static IReadOnlyList<string> PatternTerms(string pattern)
    {
        var normalized = Regex.Replace(pattern.Replace(@"\|", "|"), @"[^\p{L}\p{Nd}_]+", " ");
        return LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(normalized));
    }

    private static IReadOnlyList<string> KeywordHints(IEnumerable<SignatureEntry> entries, IReadOnlyList<string> patternTerms)
    {
        var existing = patternTerms.ToHashSet(StringComparer.Ordinal);
        var stats = new Dictionary<string, HintStats>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var tokens = PathTokenizer
                .TokenizeQuery(Regex.Replace(entry.rest, @"[^\p{L}\p{Nd}_]+", " "))
                .Where(t => IsUsefulHint(t, existing))
                .ToArray();

            foreach (var group in tokens.GroupBy(t => t, StringComparer.Ordinal))
            {
                var term = group.Key;
                if (!stats.TryGetValue(term, out var hintStats))
                    stats[term] = hintStats = new HintStats();

                hintStats.DocumentCount++;
                hintStats.OccurrenceCount += group.Count();
            }
        }

        return stats
            .OrderByDescending(kvp => HintScore(kvp.Value))
            .ThenBy(kvp => kvp.Value.DocumentCount)
            .ThenBy(kvp => StableHash(kvp.Key))
            .Take(16)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    private static float HintScore(HintStats stats)
        => stats.OccurrenceCount / MathF.Pow(stats.DocumentCount, 1.35f);

    private static bool IsUsefulHint(string token, HashSet<string> existing)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !existing.Contains(token)
           && !LowRankDictionary.Contains(token);

    private sealed class HintStats
    {
        public int DocumentCount { get; set; }
        public int OccurrenceCount { get; set; }
    }

    private static IReadOnlyList<string> AtlasKeywordHints(
        SessionKeywordAtlas? atlas,
        IReadOnlyList<string> existingTerms,
        int maxHints)
    {
        if (atlas is null)
            return [];

        var existing = existingTerms.ToHashSet(StringComparer.Ordinal);
        return atlas.HighValueTerms
            .Where(t => !existing.Contains(t))
            .Take(maxHints)
            .ToArray();
    }

    private static IReadOnlyList<string> MergeHints(
        IReadOnlyList<string> localHints,
        IReadOnlyList<string> atlasHints,
        int maxHints)
    {
        var merged = new List<string>(maxHints);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hint in atlasHints.Concat(localHints))
        {
            if (seen.Add(hint))
                merged.Add(hint);
            if (merged.Count >= maxHints)
                break;
        }

        return merged;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
                hash = hash * 31 + c;
            return hash;
        }
    }

    private static string FormatList(IReadOnlyList<string> terms)
        => terms.Count == 0 ? "-" : string.Join(", ", terms);

    private readonly record struct SignatureEntry(int lineNo, string rest);

    private static List<(string File, List<SignatureEntry> Entries)> RankMatches(
        List<(string File, List<SignatureEntry> Entries)> matches,
        IReadOnlyList<string> patternTerms)
    {
        var queryTerms = patternTerms.ToHashSet(StringComparer.Ordinal);

        return matches
            .Select(match =>
            {
                var combined = string.Join(' ', match.Entries.Select(e => e.rest));
                var tokens = PathTokenizer.TokenizeQuery(combined).ToHashSet(StringComparer.Ordinal);
                var overlap = queryTerms.Count == 0 ? 0 : queryTerms.Count(t => tokens.Contains(t));
                var overlapRatio = queryTerms.Count == 0 ? 0f : overlap / (float)queryTerms.Count;
                var score = overlapRatio * 2.5f + MathF.Min(1.5f, MathF.Log2(match.Entries.Count + 1));
                return (match, score);
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.match.File, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.match)
            .ToList();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck expand-folder — list files in a folder with their matching signatures

            Usage:
              ck expand-folder [--pattern <regex>] [--limit <n>] [--offset <n>] [--max-signatures <n>] [--all] <folder>

            Arguments:
              <folder>            Path to the folder to expand (recursive)

            Options:
              --pattern <regex>   Case-insensitive regex to filter signatures.
                                  Matched against containingType, memberName, and signature text.
                                  Files with no matching signatures are excluded from output.
                                  If omitted, all signatures in all files are shown only for small folders.
                                  Broad matches are refused with keyword hints.
              --all               Allow broad output intentionally.
              --limit <n>         Page size for matched files (default: 20, max: 50)
              --offset <n>        Page offset into ranked matched files (default: 0)
              --max-signatures <n>  Max signatures per file (default: 25, 0 = unlimited)
              --help, -h          Show this help

            Output (stdout):
              <file-path>
                <line>  <containingType>  <memberName>  <signature>

              One block per file that has at least one matching signature.
              Pagination metadata is printed to stderr.

            Examples:
              ck expand-folder src/Modules/Payment/Adyen/
              ck expand-folder --pattern "Refund" src/Modules/Payment/Adyen/
              ck expand-folder --pattern "async Task" src/Modules/Payment/
            """);
    }
}
