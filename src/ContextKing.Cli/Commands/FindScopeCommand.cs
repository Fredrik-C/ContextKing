using ContextKing.Cli.KeywordAtlas;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;
using System.Globalization;

namespace ContextKing.Cli.Commands;

internal static class FindScopeCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return 0;
        }

        var query     = reader.GetString("--query");
        var repo      = reader.GetString("--repo");
        var mustTexts = reader.GetStringList("--must");
        var explain   = reader.HasFlag("--explain");
        var verbose   = reader.HasFlag("--verbose");
        _ = reader.HasFlag("--quiet"); // Accepted for scripts; find-scope is quiet by default.
        var topKSet   = reader.TryGetInt("--top", out var topK) && topK > 0;
        if (!topKSet) topK = 15;

        var minScoreSet = reader.TryGetFloat("--min-score", out var parsedMin) && parsedMin >= 0f;
        var minScore = minScoreSet ? parsedMin : 0.85f;
        if (minScoreSet)
        {
            // When --min-score is the primary filter and --top was not explicitly set,
            // remove the count cap so the threshold alone controls how many results come back.
            if (!topKSet && minScore > 0f)
                topK = int.MaxValue;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck find-scope] Error: --query is required.");
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
            Console.Error.WriteLine($"[ck find-scope] Error: {ex.Message}");
            return 1;
        }

        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);

        // Auto-build index on first use if missing or stale.
        var status = SourceMapBuilder.GetStatus(repoRoot);
        if (status != IndexStatus.Fresh)
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    status == IndexStatus.Missing
                        ? "[ck find-scope] No index found — building now (first-time setup)..."
                        : "[ck find-scope] Index is stale — refreshing...");
            }

            using var buildEmbedder = ModelLocator.CreateEmbedder();
            var builder  = new SourceMapBuilder(buildEmbedder);
            var progress = verbose
                ? new Progress<string>(msg => Console.Error.WriteLine($"[ck find-scope] {msg}"))
                : null;
            await builder.BuildAsync(repoRoot, false, progress);
        }

        using var searchEmbedder = ModelLocator.CreateEmbedder();
        var searcher = new SourceMapSearcher(searchEmbedder);
        var results  = searcher.SearchDetailed(dbPath, query, topK, minScore,
            mustTexts.Count > 0 ? mustTexts : null);

        if (results.Count == 0)
        {
            Console.Error.WriteLine("[ck find-scope] No results found.");
            return 0;
        }

        PrintNarrowingGuidanceIfNeeded(query, topK, results, dbPath, repoRoot, mustTexts);

        foreach (var r in results)
        {
            Console.Write($"{r.Score.ToString("F4", CultureInfo.InvariantCulture)}\t{r.Path}");
            if (explain)
            {
                var terms = r.MatchedTerms.Count == 0 ? "-" : string.Join(',', r.MatchedTerms);
                var hints = r.UnmatchedFolderTerms.Count == 0 ? "-" : string.Join(',', r.UnmatchedFolderTerms);
                Console.Write(
                    $"\tsemantic={r.SemanticScore.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\texact={r.ExactBonus.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tmust={r.MustAdjustment.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tnoise={r.NoisePenalty.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tfiles={r.FileCount}" +
                    $"\ttokens={r.TokenCount}" +
                    $"\tmatched={terms}" +
                    $"\thints={hints}");
            }
            Console.WriteLine();
        }

        return 0;
    }

    private static void PrintNarrowingGuidanceIfNeeded(
        string query,
        int topK,
        IReadOnlyList<ScoredFolderDetails> results,
        string dbPath,
        string repoRoot,
        IReadOnlyList<string> mustTexts)
    {
        if (results.Count < 5 || topK < 5) return;

        var checkedResults = results.Take(Math.Min(8, results.Count)).ToArray();
        var scoreSpread    = checkedResults[0].Score - checkedResults[^1].Score;
        var highNoiseCount = checkedResults.Count(r => r.NoisePenalty >= 0.10f || r.FileCount >= 40 || r.TokenCount >= 180);
        var distinctAreas  = checkedResults
            .Select(r => AreaKey(r.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var queryTerms   = LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(query));
        var matchedTerms = checkedResults.SelectMany(r => r.MatchedTerms).Distinct(StringComparer.Ordinal).ToArray();
        var unmatchedQuery = queryTerms.Where(t => !matchedTerms.Contains(t, StringComparer.Ordinal)).ToArray();
        var rankingEngine = new KeywordRankingEngine(dbPath);
        var hintGroups = SelectKeywordHintGroups(checkedResults, queryTerms, 16, rankingEngine);

        var weakQueryCoverage = queryTerms.Count >= 4 && matchedTerms.Length < Math.Min(3, queryTerms.Count);
        var tooBroad          = scoreSpread <= 0.10f || highNoiseCount >= 3 || (distinctAreas >= 4 && weakQueryCoverage);
        if (!tooBroad) return;

        Console.WriteLine("[ck find-scope] Scope is too broad or ambiguous. Do not expand all returned folders.");
        Console.WriteLine("[ck find-scope] Rerun with more precise domain, provider, type, workflow, or symbol keywords, or add --must for the required provider/concept.");
        Console.WriteLine($"[ck find-scope] matched-query-keywords: {FormatList(matchedTerms)}");
        Console.WriteLine($"[ck find-scope] unmatched-query-keywords: {FormatList(unmatchedQuery)}");
        PrintHintGroup("exact-symbol-hints", hintGroups.Symbols);
        PrintHintGroup("provider-hints", hintGroups.Providers);
        PrintHintGroup("workflow-hints", hintGroups.Workflows);
        PrintHintGroup("other-hints", hintGroups.Other);
        Console.WriteLine($"[ck find-scope] keyword-hints-from-wide-scope: {FormatList(hintGroups.All)}");

        var atlas = SessionKeywordAtlasStore.Load(repoRoot);
        if (atlas is not null && !SessionKeywordAtlasStore.IsDirectionShift(atlas, query, mustTexts, TimeSpan.FromHours(2)))
        {
            var atlasHints = atlas.HighValueTerms
                .Where(t => !queryTerms.Contains(t, StringComparer.Ordinal))
                .Take(20)
                .ToArray();
            if (atlasHints.Length > 0)
                Console.WriteLine($"[ck find-scope] session-keyword-atlas-hints: {FormatList(atlasHints)}");
        }
        else
        {
            Console.WriteLine("[ck find-scope] no active session keyword atlas. Run ck get-keyword-map early and reuse it as your keyword source of truth until direction shifts.");
        }

        Console.WriteLine("[ck find-scope] top-folders-below are for choosing a narrower query, not for bulk expansion.");
    }

    internal static HintGroups SelectKeywordHintGroups(
        IReadOnlyList<ScoredFolderDetails> results,
        IReadOnlyCollection<string> excludedTerms,
        int maxHints = 16,
        KeywordRankingEngine? rankingEngine = null)
    {
        if (results.Count == 0 || maxHints <= 0)
            return new HintGroups([], [], [], []);

        if (rankingEngine is not null)
        {
            var rankedEngineHints = rankingEngine.RankScopedHints(results, excludedTerms, maxHints * 4);
            var symbolBucket = new List<string>(maxHints);
            var providerBucket = new List<string>(maxHints);
            var workflowBucket = new List<string>(maxHints);
            var otherBucket = new List<string>(maxHints);

            foreach (var candidate in rankedEngineHints)
            {
                switch (ClassifyHint(candidate.Term, stats: null))
                {
                    case HintCategory.Symbol:
                        AddHint(symbolBucket, candidate.Term, maxHints);
                        break;
                    case HintCategory.Provider:
                        AddHint(providerBucket, candidate.Term, maxHints);
                        break;
                    case HintCategory.Workflow:
                        AddHint(workflowBucket, candidate.Term, maxHints);
                        break;
                    default:
                        AddHint(otherBucket, candidate.Term, maxHints);
                        break;
                }
            }

            return new HintGroups(symbolBucket.ToArray(), providerBucket.ToArray(), workflowBucket.ToArray(), otherBucket.ToArray());
        }

        var stats = new Dictionary<string, HintStats>(StringComparer.Ordinal);
        var ranked = results.Take(Math.Min(8, results.Count)).ToArray();
        var lowestScore = ranked[^1].Score;
        var maxDocumentFrequency = Math.Max(2, ranked.Length / 3);

        foreach (var (result, index) in ranked.Select((r, i) => (r, i)))
        {
            var seenInFolder = new HashSet<string>(StringComparer.Ordinal);
            var folderWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);

            foreach (var term in result.UnmatchedFolderTerms)
            {
                if (term.Length < 3 || excludedTerms.Contains(term) || !seenInFolder.Add(term))
                    continue;

                if (!stats.TryGetValue(term, out var hintStats))
                    stats[term] = hintStats = new HintStats();

                hintStats.DocumentCount++;
                hintStats.ScoreWeight += folderWeight * rankWeight;
                if (result.Score > hintStats.BestScore)
                    hintStats.BestScore = result.Score;
            }
        }

        var ordered = stats
            .Where(kvp => kvp.Value.DocumentCount <= maxDocumentFrequency)
            .Select(kvp => new HintCandidate(kvp.Key, kvp.Value, ClassifyHint(kvp.Key, kvp.Value)))
            .OrderBy(kvp => kvp.Category)
            .ThenByDescending(kvp => HintScore(kvp.Stats))
            .ThenBy(kvp => kvp.Stats.DocumentCount)
            .ThenByDescending(kvp => kvp.Stats.BestScore)
            .ThenByDescending(kvp => kvp.Term.Length)
            .ThenBy(kvp => StableHash(kvp.Term))
            .Take(maxHints * 4)
            .ToArray();

        var symbols = new List<string>(maxHints);
        var providers = new List<string>(maxHints);
        var workflows = new List<string>(maxHints);
        var other = new List<string>(maxHints);

        foreach (var candidate in ordered)
        {
            switch (candidate.Category)
            {
                case HintCategory.Symbol:
                    AddHint(symbols, candidate.Term, maxHints);
                    break;
                case HintCategory.Provider:
                    AddHint(providers, candidate.Term, maxHints);
                    break;
                case HintCategory.Workflow:
                    AddHint(workflows, candidate.Term, maxHints);
                    break;
                default:
                    AddHint(other, candidate.Term, maxHints);
                    break;
            }
        }

        return new HintGroups(
            symbols.ToArray(),
            providers.ToArray(),
            workflows.ToArray(),
            other.ToArray());
    }

    internal static IReadOnlyList<string> SelectKeywordHints(
        IReadOnlyList<ScoredFolderDetails> results,
        IReadOnlyCollection<string> excludedTerms,
        int maxHints = 16)
        => SelectKeywordHintGroups(results, excludedTerms, maxHints).All;

    private static float HintScore(HintStats stats)
        => stats.ScoreWeight / MathF.Pow(stats.DocumentCount, 1.35f);

    private static void AddHint(List<string> bucket, string term, int maxHints)
    {
        if (bucket.Count < maxHints)
            bucket.Add(term);
    }

    private readonly record struct HintCandidate(string Term, HintStats Stats, HintCategory Category);

    private static HintCategory ClassifyHint(string term, HintStats? stats)
    {
        if (IsSymbolCandidate(term, stats))
            return HintCategory.Symbol;

        if (LooksProviderLike(term, stats))
            return HintCategory.Provider;

        if (WorkflowTerms.Contains(term) || LooksWorkflowish(term))
            return HintCategory.Workflow;

        return HintCategory.Other;
    }

    private static bool IsSymbolCandidate(string term, HintStats? stats)
    {
        var docCount = stats?.DocumentCount ?? 1;
        return term.Length >= 7
               && docCount <= 2
               && !WorkflowTerms.Contains(term)
               && !LowRankDictionary.Contains(term)
               && (term.Any(char.IsDigit) || term.Contains('_') || term.Length >= 10);
    }

    private static bool LooksProviderLike(string term, HintStats? stats)
    {
        var docCount = stats?.DocumentCount ?? 1;
        return docCount <= 2
               && term.Length is >= 4 and <= 18
               && term.All(char.IsLetter)
               && !WorkflowTerms.Contains(term)
               && !LowRankDictionary.Contains(term);
    }

    private static bool LooksWorkflowish(string term)
        => term.EndsWith("request", StringComparison.Ordinal)
           || term.EndsWith("response", StringComparison.Ordinal)
           || term.EndsWith("command", StringComparison.Ordinal)
           || term.EndsWith("handler", StringComparison.Ordinal)
           || term.EndsWith("component", StringComparison.Ordinal)
           || term.EndsWith("processor", StringComparison.Ordinal)
           || term.EndsWith("gateway", StringComparison.Ordinal)
           || term.EndsWith("notification", StringComparison.Ordinal)
           || term.EndsWith("parameters", StringComparison.Ordinal)
           || term.EndsWith("parameter", StringComparison.Ordinal)
           || term.EndsWith("refund", StringComparison.Ordinal)
           || term.EndsWith("capture", StringComparison.Ordinal)
           || term.EndsWith("authorization", StringComparison.Ordinal)
           || term.EndsWith("payment", StringComparison.Ordinal)
           || term.EndsWith("queue", StringComparison.Ordinal)
           || term.EndsWith("event", StringComparison.Ordinal);

    private static void PrintHintGroup(string label, IReadOnlyList<string> terms)
    {
        if (terms.Count > 0)
            Console.WriteLine($"[ck find-scope] {label}: {FormatList(terms)}");
    }

    private static readonly HashSet<string> WorkflowTerms = new(StringComparer.Ordinal)
    {
        "refund",
        "payment",
        "authorization",
        "capture",
        "notification",
        "command",
        "processor",
        "gateway",
        "component",
        "request",
        "response",
        "queue",
        "event",
        "balance",
        "charge",
        "dispute",
        "payout",
        "reversal",
        "merchant",
        "account",
    };

    internal sealed class HintStats
    {
        public int DocumentCount { get; set; }
        public float ScoreWeight { get; set; }
        public float BestScore { get; set; }
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

    private enum HintCategory
    {
        Symbol,
        Provider,
        Workflow,
        Other,
    }

    internal readonly record struct HintGroups(
        IReadOnlyList<string> Symbols,
        IReadOnlyList<string> Providers,
        IReadOnlyList<string> Workflows,
        IReadOnlyList<string> Other)
    {
        public IReadOnlyList<string> All => Symbols.Concat(Providers).Concat(Workflows).Concat(Other).Take(16).ToArray();
    }

    private static string AreaKey(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? path : string.Join('/', parts.Take(3));
    }

    private static string FormatList(IReadOnlyList<string> terms)
        => terms.Count == 0 ? "-" : string.Join(", ", terms);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck find-scope — semantic search to find the most relevant folder(s)

            Usage:
              ck find-scope --query <text> [--must <text>] [--repo <path>] [--top <n>] [--min-score <f>] [--explain] [--verbose]

            Options:
              --query <text>      Natural language description of the code area (required)
              --must <text>       Provider/concept to focus on. Boosts folders that contain this
                                  term and penalises folders about competing concepts detected via
                                  embedding similarity (e.g. --must "stripe" boosts Stripe folders
                                  and suppresses Adyen folders without naming them). Can be repeated.
              --repo <path>       Path to git repo root (default: git rev-parse from cwd)
              --top <n>           Hard cap on result count (default: 15; ignored when
                                  --min-score is set without --top)
              --min-score <f>     Exclude folders with score below this threshold (0.0–1.15,
                                  default: 0.85).
                                  When specified without --top, returns ALL folders above the
                                  threshold. Combine with --top to cap a score-filtered list.
                                  Recommended: 0.5 for impact analysis, 0.65 for targeted search.
              --explain           Add scoring breakdown columns: semantic, exact, must, noise,
                                  files, tokens, matched exact terms, and hint terms.
              --verbose           Print index build/refresh progress to stderr. Default output is
                                  quiet so first-use indexing does not consume agent context.
              --quiet             Accepted for compatibility; quiet is the default.
              --help, -h          Show this help

            Output (stdout):
              <score>\t<relative-folder-path>
              One line per result, score is cosine similarity [0..1] + exact-match bonus (≤0.30).
              If the result set is broad or ambiguous, a narrowing diagnostic is printed first
              with matched query terms, unmatched query terms, and keyword hints from the wide scope.

            First call auto-builds the index if not present. Progress goes to stderr.
            """);
    }
}
