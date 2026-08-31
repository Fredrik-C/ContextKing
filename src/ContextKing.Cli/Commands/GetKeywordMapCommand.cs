using ContextKing.Cli.KeywordAtlas;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class GetKeywordMapCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return 0;
        }

        var query = reader.GetString("--query");
        var repo = reader.GetString("--repo");
        var mustTexts = reader.GetStringList("--must");
        var verbose = reader.HasFlag("--verbose");
        _ = reader.HasFlag("--quiet"); // Accepted for scripts; output is already concise.

        var topFoldersSet = reader.TryGetInt("--top", out var topFolders) && topFolders > 0;
        if (!topFoldersSet) topFolders = 8;

        var perKeywordSet = reader.TryGetInt("--per-keyword", out var perKeyword) && perKeyword > 0;
        if (!perKeywordSet) perKeyword = 3;

        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck get-keyword-map] Error: --query is required.");
            PrintHelp();
            return 1;
        }

        string repoRoot;
        try
        {
            repoRoot = GitTracker.GetWorktreeRoot(repo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck get-keyword-map] Error: {ex.Message}");
            return 1;
        }

        var status = SourceMapBuilder.GetStatus(repoRoot);
        if (status != IndexStatus.Fresh)
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    status == IndexStatus.Missing
                        ? "[ck get-keyword-map] No index found — building now (first-time setup)..."
                        : "[ck get-keyword-map] Index is stale — refreshing...");
            }

            var builder = new SourceMapBuilder();
            var progress = verbose
                ? new Progress<string>(msg => Console.Error.WriteLine($"[ck get-keyword-map] {msg}"))
                : null;
            await builder.BuildAsync(repoRoot, false, progress);
        }

        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);
        var searcher = new FileMapSearcher();
        var results = searcher.Search(
            dbPath,
            query,
            topFolders,
            minScore: 0f,
            mustTerms: mustTexts.Count > 0 ? mustTexts : null);

        if (results.Count == 0)
        {
            Console.Error.WriteLine("[ck get-keyword-map] No results found.");
            return 0;
        }

        var queryTerms = LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(query));
        var matchedTerms = queryTerms
            .Where(term => results.Any(r => FileTerms(r).Contains(term)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unmatchedTerms = queryTerms
            .Where(t => !matchedTerms.Contains(t, StringComparer.Ordinal))
            .ToArray();
        var seedTerms = matchedTerms.Length > 0 ? matchedTerms : queryTerms.ToArray();

        var map = BuildKeywordMap(results, seedTerms, queryTerms, perKeyword);
        var relatedHints = BuildGlobalHints(results, queryTerms, 3);
        var semanticHints = BuildSemanticHints(dbPath, query, mustTexts, queryTerms, relatedHints, verbose);
        var globalHints = relatedHints.Concat(semanticHints).Distinct(StringComparer.Ordinal).ToArray();
        var advice = KeywordIntentAdvisor.BuildAdvice(
            dbPath,
            queryTerms,
            matchedTerms,
            mustTexts,
            globalHints);

        PersistSessionAtlas(repoRoot, query, queryTerms, mustTexts, matchedTerms, unmatchedTerms, globalHints, map, advice);

        if (relatedHints.Count > 0)
            Console.WriteLine($"[ck get-keyword-map] related-keyword-hints: {FormatList(relatedHints)}");
        if (semanticHints.Count > 0)
            Console.WriteLine($"[ck get-keyword-map] semantic-keyword-hints: {FormatList(semanticHints)}");
        Console.WriteLine($"[ck get-keyword-map] suggested-next-step: {advice.SuggestedNextCommand}");

        if (verbose)
            WriteDiagnostics(matchedTerms, unmatchedTerms, globalHints, map, advice);

        return 0;
    }

    private static void WriteDiagnostics(
        IReadOnlyList<string> matchedTerms,
        IReadOnlyList<string> unmatchedTerms,
        IReadOnlyList<string> globalHints,
        IReadOnlyList<KeywordMapEntry> map,
        QueryCompositionAdvice advice)
    {
        Console.WriteLine($"[ck get-keyword-map] matched-query-keywords: {FormatList(matchedTerms)}");
        Console.WriteLine($"[ck get-keyword-map] unmatched-query-keywords: {FormatList(unmatchedTerms)}");
        Console.WriteLine($"[ck get-keyword-map] global-keyword-hints: {FormatList(globalHints)}");
        Console.WriteLine("[ck get-keyword-map] keyword-map:");
        foreach (var (seed, terms) in map)
            Console.WriteLine($"[ck get-keyword-map]   {seed}: {FormatList(terms)}");

        foreach (var term in advice.Terms)
        {
            Console.WriteLine(
                $"[ck get-keyword-map] evidence: {term.Term} role={term.Role.ToString().ToLowerInvariant()} " +
                $"df={term.GlobalDocumentFrequency} lift={term.LocalLiftScore:F3} " +
                $"matched={(term.IsMatchedQueryTerm ? "yes" : "no")}");
        }
    }

    private static IReadOnlyList<string> BuildSemanticHints(
        string dbPath,
        string query,
        IReadOnlyList<string> mustTerms,
        IReadOnlyCollection<string> queryTerms,
        IReadOnlyCollection<string> relatedHints,
        bool verbose)
    {
        try
        {
            using var embedder = ModelLocator.CreateEmbedder();
            var semanticResults = new SourceMapSearcher(embedder).SearchDetailed(
                dbPath,
                query,
                topK: 8,
                mustTexts: mustTerms.Count > 0 ? mustTerms : null);

            return SelectSemanticHints(semanticResults, queryTerms, relatedHints, 3);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidOperationException)
        {
            if (verbose)
                Console.Error.WriteLine($"[ck get-keyword-map] Semantic hints unavailable: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<string> SelectSemanticHints(
        IReadOnlyList<ScoredFolderDetails> semanticResults,
        IReadOnlyCollection<string> queryTerms,
        IReadOnlyCollection<string> relatedHints,
        int maxHints)
    {
        if (semanticResults.Count == 0 || maxHints <= 0)
            return [];

        var excluded = queryTerms.Concat(relatedHints).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return semanticResults
            .SelectMany(result => result.UnmatchedFolderTerms)
            .Where(term => IsUsefulKeyword(term, excluded, seen))
            .Take(maxHints)
            .ToArray();
    }

    internal static IReadOnlyList<KeywordMapEntry> BuildKeywordMap(
        IReadOnlyList<ScoredFile> results,
        IReadOnlyList<string> seedTerms,
        IReadOnlyCollection<string> excludedTerms,
        int perKeyword)
    {
        if (results.Count == 0 || seedTerms.Count == 0 || perKeyword <= 0)
            return [];

        var map = new List<KeywordMapEntry>(seedTerms.Count);

        foreach (var seed in seedTerms.Distinct(StringComparer.Ordinal))
        {
            var related = BuildFallbackRelatedTerms(results, seed, excludedTerms, perKeyword);
            map.Add(new KeywordMapEntry(seed, related));
        }

        return map;
    }

    private static IReadOnlyList<string> BuildFallbackRelatedTerms(
        IReadOnlyList<ScoredFile> results,
        string seed,
        IReadOnlyCollection<string> excludedTerms,
        int perKeyword)
    {
        var ranked = results.Take(Math.Min(8, results.Count)).ToArray();
        var lowestScore = ranked[^1].Score;
        var maxDocumentFrequency = Math.Max(3, ranked.Length - 1);
        var seedStats = new Dictionary<string, HintStats>(StringComparer.Ordinal);

        foreach (var (result, index) in ranked.Select((r, i) => (r, i)))
        {
            var fileTerms = FileTerms(result);
            if (!fileTerms.Contains(seed))
                continue;

            var folderWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);
            var seenInFolder = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (term, sourceWeight) in FileHintTerms(result))
            {
                if (!IsUsefulKeyword(term, excludedTerms, seenInFolder))
                    continue;

                if (!seedStats.TryGetValue(term, out var stats))
                    seedStats[term] = stats = new HintStats();

                stats.DocumentCount++;
                stats.ScoreWeight += folderWeight * rankWeight * sourceWeight;
                if (result.Score > stats.BestScore)
                    stats.BestScore = result.Score;
            }
        }

        return seedStats
            .Where(kvp => kvp.Value.DocumentCount <= maxDocumentFrequency)
            .OrderByDescending(kvp => HintScore(kvp.Value))
            .ThenBy(kvp => kvp.Value.DocumentCount)
            .ThenByDescending(kvp => kvp.Value.BestScore)
            .ThenByDescending(kvp => kvp.Key.Length)
            .ThenBy(kvp => StableHash(kvp.Key))
            .Take(perKeyword)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildGlobalHints(
        IReadOnlyList<ScoredFile> results,
        IReadOnlyCollection<string> excludedTerms,
        int maxHints)
    {
        if (results.Count == 0 || maxHints <= 0)
            return [];

        var ranked = results.Take(Math.Min(12, results.Count)).ToArray();
        var lowestScore = ranked[^1].Score;
        var stats = new Dictionary<string, HintStats>(StringComparer.Ordinal);

        foreach (var (result, index) in ranked.Select((r, i) => (r, i)))
        {
            var fileWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (term, sourceWeight) in FileHintTerms(result))
            {
                if (!IsUsefulKeyword(term, excludedTerms, seen))
                    continue;
                if (!stats.TryGetValue(term, out var hint))
                    stats[term] = hint = new HintStats();
                hint.DocumentCount++;
                hint.ScoreWeight += fileWeight * rankWeight * sourceWeight;
                if (result.Score > hint.BestScore)
                    hint.BestScore = result.Score;
            }
        }

        return stats
            .OrderByDescending(kvp => HintScore(kvp.Value))
            .ThenBy(kvp => kvp.Value.DocumentCount)
            .ThenByDescending(kvp => kvp.Key.Length)
            .ThenBy(kvp => StableHash(kvp.Key))
            .Take(maxHints)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    private static void PersistSessionAtlas(
        string repoRoot,
        string query,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> mustTerms,
        IReadOnlyList<string> matchedTerms,
        IReadOnlyList<string> unmatchedTerms,
        IReadOnlyList<string> globalHints,
        IReadOnlyList<KeywordMapEntry> keywordMap,
        QueryCompositionAdvice advice)
    {
        var roleGrounded = advice.Terms
            .Where(t => t.Role is KeywordRole.Anchor or KeywordRole.Discriminator or KeywordRole.Workflow)
            .Where(t => t.BroadRiskScore <= 0.85f)
            .Select(t => t.Term);

        var highValue = roleGrounded
            .Concat(globalHints)
            .Concat(keywordMap.SelectMany(x => x.Related))
            .Distinct(StringComparer.Ordinal)
            .Take(300)
            .ToArray();

        var atlas = new SessionKeywordAtlas(
            query,
            queryTerms,
            mustTerms,
            matchedTerms,
            unmatchedTerms,
            globalHints,
            highValue,
            keywordMap.Select(x => new SessionKeywordAtlasEntry(x.Seed, x.Related)).ToArray(),
            DateTime.UtcNow);

        SessionKeywordAtlasStore.Save(repoRoot, atlas);
    }

    private static HashSet<string> FileTerms(ScoredFile file)
    {
        return FileHintTerms(file).Select(x => x.Term).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Term, float SourceWeight)> FileHintTerms(ScoredFile file)
    {
        var folder = Path.GetDirectoryName(file.Path)?.Replace('\\', '/') ?? ".";
        foreach (var token in PathTokenizer.TokenizePath(folder).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return (token, 1.25f);
        foreach (var token in PathTokenizer.TokenizeFileName(Path.GetFileName(file.Path)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return (token, 2.25f);
        foreach (var token in ExtractLexicalTerms(file.TypeNames))
            yield return (token, 4f);
        foreach (var token in ExtractTerms(file.EmbeddingText))
            yield return (token, 1f);
        foreach (var token in ExtractLexicalTerms(file.MethodNames))
            yield return (token, 0.75f);
    }

    private static IEnumerable<string> ExtractLexicalTerms(string text)
    {
        foreach (var name in text.Split([';', ',', '.', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var token in PathTokenizer.MethodNameToPhrase(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return token.ToLowerInvariant();
        }
    }

    private static IEnumerable<string> ExtractTerms(string text)
    {
        var buffer = new char[64];
        var len = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (len < buffer.Length)
                    buffer[len++] = char.ToLowerInvariant(ch);
                continue;
            }

            if (len >= 3)
                yield return new string(buffer, 0, len);
            len = 0;
        }

        if (len >= 3)
            yield return new string(buffer, 0, len);
    }

    private static bool IsUsefulKeyword(string term, IReadOnlyCollection<string> excludedTerms, HashSet<string> seenInFolder)
        => term.Length >= 3
           && !excludedTerms.Contains(term)
           && !LowRankDictionary.Contains(term)
           && !IsNoisyOperationalToken(term)
           && seenInFolder.Add(term);

    private static bool IsNoisyOperationalToken(string term)
    {
        var t = term.ToLowerInvariant();
        if (t.Length > 24 && (t.StartsWith("enable", StringComparison.Ordinal) || t.StartsWith("featureflag", StringComparison.Ordinal)))
            return true;
        if (t.StartsWith("create", StringComparison.Ordinal) && t.EndsWith("parameters", StringComparison.Ordinal))
            return true;
        if (t is "feature" or "flags" or "components" or "component")
            return true;
        return false;
    }

    private static float HintScore(HintStats stats)
        => stats.ScoreWeight / MathF.Pow(stats.DocumentCount, 1.35f);

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

    internal readonly record struct KeywordMapEntry(string Seed, IReadOnlyList<string> Related);

    private sealed class HintStats
    {
        public int DocumentCount { get; set; }
        public float ScoreWeight { get; set; }
        public float BestScore { get; set; }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-keyword-map — show query keyword neighborhoods to improve precision

            Usage:
              ck get-keyword-map --query <text> [--must <text>] [--top <n>] [--per-keyword <n>] [--repo <path>] [--verbose]

            Options:
              --query <text>         Natural language description of the code area (required)
              --must <text>          Required provider/concept to focus on (repeatable)
              --top <n>              Number of top indexed files to analyze (default: 8)
              --per-keyword <n>      Max related keywords kept per seed keyword (default: 3)
              --repo <path>          Path to git repo root (default: git rev-parse from cwd)
              --verbose              Include diagnostic keyword evidence and index progress
              --quiet                Accepted for compatibility; concise output is default
              --help, -h             Show this help

            Output (stdout):
              - up to three type-name-weighted related keyword hints
              - up to three semantic keyword hints from the source-map index, when available
              - one compact, copyable `ck find-files` next step
              - use --verbose to inspect keyword evidence and the persisted map

            Also persists a session keyword atlas in .ck-index/session-keyword-atlas.json
            for stable keyword guidance across retries until direction shifts.
            """);
    }
}
