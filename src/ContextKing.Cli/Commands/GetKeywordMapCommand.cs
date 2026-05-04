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
        if (!topFoldersSet) topFolders = 12;

        var perKeywordSet = reader.TryGetInt("--per-keyword", out var perKeyword) && perKeyword > 0;
        if (!perKeywordSet) perKeyword = 50;

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

            using var buildEmbedder = ModelLocator.CreateEmbedder();
            var builder = new SourceMapBuilder(buildEmbedder);
            var progress = verbose
                ? new Progress<string>(msg => Console.Error.WriteLine($"[ck get-keyword-map] {msg}"))
                : null;
            await builder.BuildAsync(repoRoot, false, progress);
        }

        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);
        using var searchEmbedder = ModelLocator.CreateEmbedder();
        var searcher = new SourceMapSearcher(searchEmbedder);
        var results = searcher.SearchDetailed(
            dbPath,
            query,
            topFolders,
            minScore: 0f,
            mustTexts.Count > 0 ? mustTexts : null);

        if (results.Count == 0)
        {
            Console.Error.WriteLine("[ck get-keyword-map] No results found.");
            return 0;
        }

        var queryTerms = LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(query));
        var matchedTerms = results
            .SelectMany(r => r.MatchedTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unmatchedTerms = queryTerms
            .Where(t => !matchedTerms.Contains(t, StringComparer.Ordinal))
            .ToArray();
        var seedTerms = matchedTerms.Length > 0 ? matchedTerms : queryTerms.ToArray();

        var ranker = new KeywordRankingEngine(dbPath);
        var map = BuildKeywordMap(results, seedTerms, queryTerms, perKeyword, ranker);
        var globalHints = ranker
            .RankScopedHints(results, queryTerms, Math.Max(24, perKeyword * 2))
            .Select(x => x.Term)
            .ToArray();
        var advice = KeywordIntentAdvisor.BuildAdvice(
            dbPath,
            queryTerms,
            matchedTerms,
            mustTexts,
            globalHints);

        PersistSessionAtlas(repoRoot, query, queryTerms, mustTexts, matchedTerms, unmatchedTerms, globalHints, map, advice);

        Console.WriteLine($"[ck get-keyword-map] matched-query-keywords: {FormatList(matchedTerms)}");
        Console.WriteLine($"[ck get-keyword-map] unmatched-query-keywords: {FormatList(unmatchedTerms)}");
        Console.WriteLine($"[ck get-keyword-map] global-keyword-hints: {FormatList(globalHints)}");
        Console.WriteLine("[ck get-keyword-map] keyword-map:");

        foreach (var (seed, terms) in map)
            Console.WriteLine($"[ck get-keyword-map]   {seed}: {FormatList(terms)}");

        var anchorTerms = advice.Terms.Where(t => t.Role == KeywordRole.Anchor).Select(t => t.Term).Distinct(StringComparer.Ordinal).ToArray();
        var discriminatorTerms = advice.Terms.Where(t => t.Role == KeywordRole.Discriminator).Select(t => t.Term).Distinct(StringComparer.Ordinal).ToArray();
        var workflowTerms = advice.Terms.Where(t => t.Role == KeywordRole.Workflow).Select(t => t.Term).Distinct(StringComparer.Ordinal).ToArray();
        var noiseTerms = advice.Terms.Where(t => t.Role == KeywordRole.Noise).Select(t => t.Term).Distinct(StringComparer.Ordinal).ToArray();

        Console.WriteLine($"[ck get-keyword-map] role-anchor: {FormatList(anchorTerms)}");
        Console.WriteLine($"[ck get-keyword-map] role-discriminator: {FormatList(discriminatorTerms)}");
        Console.WriteLine($"[ck get-keyword-map] role-workflow: {FormatList(workflowTerms)}");
        Console.WriteLine($"[ck get-keyword-map] role-noise: {FormatList(noiseTerms)}");
        Console.WriteLine("[ck get-keyword-map] term-evidence:");
        foreach (var term in advice.Terms.Take(16))
        {
            Console.WriteLine(
                $"[ck get-keyword-map]   {term.Term}: role={term.Role.ToString().ToLowerInvariant()} " +
                $"df={term.GlobalDocumentFrequency} " +
                $"dfp={term.DocumentFrequencyPercentile:F3} " +
                $"lift={term.LocalLiftScore:F3} " +
                $"conc={term.ScopeConcentrationScore:F3} " +
                $"broad={term.BroadRiskScore:F3} " +
                $"matched={(term.IsMatchedQueryTerm ? "yes" : "no")}");
        }
        Console.WriteLine($"[ck get-keyword-map] suggested-must: {advice.SuggestedMust ?? "-"}");
        for (var i = 0; i < advice.SuggestedQueries.Count; i++)
            Console.WriteLine($"[ck get-keyword-map] suggested-query-{i + 1}: {advice.SuggestedQueries[i]}");
        Console.WriteLine($"[ck get-keyword-map] suggested-next-step: {advice.SuggestedNextCommand}");

        return 0;
    }

    internal static IReadOnlyList<KeywordMapEntry> BuildKeywordMap(
        IReadOnlyList<ScoredFolderDetails> results,
        IReadOnlyList<string> seedTerms,
        IReadOnlyCollection<string> excludedTerms,
        int perKeyword)
        => BuildKeywordMap(results, seedTerms, excludedTerms, perKeyword, ranker: null);

    private static IReadOnlyList<KeywordMapEntry> BuildKeywordMap(
        IReadOnlyList<ScoredFolderDetails> results,
        IReadOnlyList<string> seedTerms,
        IReadOnlyCollection<string> excludedTerms,
        int perKeyword,
        KeywordRankingEngine? ranker)
    {
        if (results.Count == 0 || seedTerms.Count == 0 || perKeyword <= 0)
            return [];

        var map = new List<KeywordMapEntry>(seedTerms.Count);

        foreach (var seed in seedTerms.Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<string> related;

            if (ranker is not null)
            {
                related = ranker
                    .RankRelatedTerms(results, seed, excludedTerms, perKeyword)
                    .Select(r => r.Term)
                    .ToArray();
            }
            else
            {
                // Fallback used by unit tests that call this helper directly.
                related = BuildFallbackRelatedTerms(results, seed, excludedTerms, perKeyword);
            }

            map.Add(new KeywordMapEntry(seed, related));
        }

        return map;
    }

    private static IReadOnlyList<string> BuildFallbackRelatedTerms(
        IReadOnlyList<ScoredFolderDetails> results,
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
            if (!result.MatchedTerms.Contains(seed, StringComparer.Ordinal))
                continue;

            var folderWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);
            var seenInFolder = new HashSet<string>(StringComparer.Ordinal);

            foreach (var term in FullFolderTerms(result))
            {
                if (!IsUsefulKeyword(term, excludedTerms, seenInFolder))
                    continue;

                if (!seedStats.TryGetValue(term, out var stats))
                    seedStats[term] = stats = new HintStats();

                stats.DocumentCount++;
                stats.ScoreWeight += folderWeight * rankWeight;
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

    private static IEnumerable<string> FullFolderTerms(ScoredFolderDetails result)
    {
        if (!string.IsNullOrWhiteSpace(result.CombinedTokens))
        {
            foreach (var token in result.CombinedTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return token;
            yield break;
        }

        foreach (var token in result.UnmatchedFolderTerms)
            yield return token;
    }

    private static bool IsUsefulKeyword(string term, IReadOnlyCollection<string> excludedTerms, HashSet<string> seenInFolder)
        => term.Length >= 3
           && !excludedTerms.Contains(term)
           && !LowRankDictionary.Contains(term)
           && seenInFolder.Add(term);

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
              --top <n>              Number of top semantic folders to analyze (default: 12)
              --per-keyword <n>      Max related keywords returned per seed keyword (default: 50, adaptive)
              --repo <path>          Path to git repo root (default: git rev-parse from cwd)
              --verbose              Print index build/refresh progress to stderr
              --quiet                Accepted for compatibility; concise output is default
              --help, -h             Show this help

            Output (stdout):
              - matched/unmatched query keywords
              - global keyword hints from the scoped result set
              - keyword-map: one row per seed keyword (seed -> related keywords)

            Also persists a session keyword atlas in .ck-index/session-keyword-atlas.json
            for stable keyword guidance across retries until direction shifts.
            """);
    }
}
