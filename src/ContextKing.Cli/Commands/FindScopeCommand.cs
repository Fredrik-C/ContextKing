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
        var limitSet  = reader.TryGetInt("--limit", out var limit) && limit > 0;
        if (!limitSet) limit = 10;
        if (limit > 20) limit = 20;
        var offsetSet = reader.TryGetInt("--offset", out var offset) && offset >= 0;
        if (!offsetSet) offset = 0;
        var maxPerAreaSet = reader.TryGetInt("--max-per-area", out var maxPerArea) && maxPerArea > 0;
        if (!maxPerAreaSet) maxPerArea = 3;

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
        var resultCap = Math.Max(topK, offset + limit + 40);
        if (resultCap < 200) resultCap = 200;
        var allResults = searcher.SearchDetailed(dbPath, query, resultCap, minScore,
            mustTexts.Count > 0 ? mustTexts : null);
        var sessionAtlas = SessionKeywordAtlasStore.Load(repoRoot);
        var queryTerms = LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(query));

        if (allResults.Count == 0)
        {
            Console.Error.WriteLine("[ck find-scope] No results found.");
            PrintGroundingHintsForNoMatch(query, queryTerms, mustTexts, sessionAtlas);
            return 0;
        }

        PrintNarrowingGuidanceIfNeeded(query, queryTerms, topK, allResults, dbPath, repoRoot, mustTexts, sessionAtlas);

        var pagedResults = ApplyPaging(
            allResults,
            offset,
            limit,
            diversify: offset == 0,
            maxPerArea: maxPerArea);
        var hasMore = offset + pagedResults.Count < allResults.Count;
        var nextOffset = hasMore ? offset + pagedResults.Count : -1;
        Console.Error.WriteLine(
            $"[ck find-scope] pagination: offset={offset} limit={limit} returned={pagedResults.Count} total_estimate={allResults.Count} has_more={hasMore.ToString().ToLowerInvariant()}" +
            (hasMore ? $" next_offset={nextOffset}" : string.Empty));

        foreach (var r in pagedResults)
        {
            Console.Write($"{r.Score.ToString("F4", CultureInfo.InvariantCulture)}\t{r.Path}");
            if (explain)
            {
                var terms = r.MatchedTerms.Count == 0 ? "-" : string.Join(',', r.MatchedTerms);
                var hints = r.UnmatchedFolderTerms.Count == 0 ? "-" : string.Join(',', r.UnmatchedFolderTerms);
                var reason = BuildMatchReason(r);
                Console.Write(
                    $"\tsemantic={r.SemanticScore.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\texact={r.ExactBonus.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tmust={r.MustAdjustment.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tnoise={r.NoisePenalty.ToString("F4", CultureInfo.InvariantCulture)}" +
                    $"\tfiles={r.FileCount}" +
                    $"\ttokens={r.TokenCount}" +
                    $"\tmatched={terms}" +
                    $"\thints={hints}" +
                    $"\treason={reason}");
            }
            Console.WriteLine();
        }

        return 0;
    }

    private static void PrintNarrowingGuidanceIfNeeded(
        string query,
        IReadOnlyList<string> queryTerms,
        int topK,
        IReadOnlyList<ScoredFolderDetails> results,
        string dbPath,
        string repoRoot,
        IReadOnlyList<string> mustTexts,
        SessionKeywordAtlas? sessionAtlas)
    {
        if (results.Count < 5 || topK < 5) return;

        var checkedResults = results.Take(Math.Min(8, results.Count)).ToArray();
        var scoreSpread    = checkedResults[0].Score - checkedResults[^1].Score;
        var highNoiseCount = checkedResults.Count(r => r.NoisePenalty >= 0.10f || r.FileCount >= 40 || r.TokenCount >= 180);
        var distinctAreas  = checkedResults
            .Select(r => AreaKey(r.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var matchedTerms = checkedResults.SelectMany(r => r.MatchedTerms).Distinct(StringComparer.Ordinal).ToArray();
        var unmatchedQuery = queryTerms.Where(t => !matchedTerms.Contains(t, StringComparer.Ordinal)).ToArray();
        var rankingEngine = new KeywordRankingEngine(dbPath);
        var hintGroups = SelectKeywordHintGroups(checkedResults, queryTerms, 16, rankingEngine);
        var advice = KeywordIntentAdvisor.BuildAdvice(
            dbPath,
            queryTerms,
            matchedTerms,
            mustTexts,
            hintGroups.All);
        var grounding = CalculateGroundingScore(queryTerms, matchedTerms, sessionAtlas);

        var weakQueryCoverage = queryTerms.Count >= 4 && matchedTerms.Length < Math.Min(3, queryTerms.Count);
        var spreadWide = scoreSpread <= 0.10f;
        var noisyWide = highNoiseCount >= 3;
        var fragmentedWide = distinctAreas >= 4 && weakQueryCoverage;
        var ambiguitySignals =
            (spreadWide ? 1 : 0) +
            (noisyWide ? 1 : 0) +
            (fragmentedWide ? 1 : 0);
        var tooBroad = ambiguitySignals >= 2;
        if (!tooBroad) return;

        Console.WriteLine("[ck find-scope] Scope is too broad or ambiguous. Do not expand all returned folders.");
        Console.WriteLine("[ck find-scope] Use a top-5 shortlist from results below, then rerun with refined keywords.");
        Console.WriteLine("[ck find-scope] Rerun with more precise domain, provider, type, workflow, or symbol keywords, or add --must for the required provider/concept.");
        Console.WriteLine($"[ck find-scope] matched-query-keywords: {FormatList(matchedTerms)}");
        Console.WriteLine($"[ck find-scope] unmatched-query-keywords: {FormatList(unmatchedQuery)}");
        PrintHintGroup("exact-symbol-hints", hintGroups.Symbols);
        PrintHintGroup("provider-hints", hintGroups.Providers);
        PrintHintGroup("workflow-hints", hintGroups.Workflows);
        PrintHintGroup("other-hints", hintGroups.Other);
        Console.WriteLine($"[ck find-scope] keyword-hints-from-wide-scope: {FormatList(hintGroups.All)}");
        if (advice.SuggestedQueries.Count > 0)
        {
            for (var i = 0; i < advice.SuggestedQueries.Count; i++)
                Console.WriteLine($"[ck find-scope] suggested-query-{i + 1}: {advice.SuggestedQueries[i]}");
        }
        Console.WriteLine($"[ck find-scope] suggested-next-step: {advice.SuggestedNextCommand}");
        Console.WriteLine($"[ck find-scope] grounding-score: {grounding.Score:F2} ({grounding.OverlapCount}/{grounding.TotalCount} query terms grounded)");
        if (grounding.Score < 0.50f && advice.SuggestedQueries.Count > 0)
            Console.WriteLine($"[ck find-scope] low-grounding-rewrite: {advice.SuggestedQueries[0]}");

        var atlas = sessionAtlas;
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

        Console.WriteLine("[ck find-scope] top-folders-below are for choosing a narrower top-5 query shortlist, not for bulk expansion.");
    }

    private static void PrintGroundingHintsForNoMatch(
        string query,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> mustTerms,
        SessionKeywordAtlas? atlas)
    {
        if (atlas is null || SessionKeywordAtlasStore.IsDirectionShift(atlas, query, mustTerms, TimeSpan.FromHours(2)))
            return;

        var groundedCandidates = atlas.HighValueTerms
            .Where(t => !queryTerms.Contains(t, StringComparer.Ordinal))
            .Take(12)
            .ToArray();
        if (groundedCandidates.Length == 0)
            return;

        var rewrite1 = queryTerms.Take(1).Concat(groundedCandidates.Take(2)).Distinct(StringComparer.Ordinal).ToArray();
        var rewrite2 = queryTerms.Take(1).Concat(groundedCandidates.Skip(2).Take(2)).Distinct(StringComparer.Ordinal).ToArray();

        if (rewrite1.Length > 0)
            Console.WriteLine($"[ck find-scope] no-match-rewrite-1: {string.Join(' ', rewrite1)}");
        if (rewrite2.Length > 0)
            Console.WriteLine($"[ck find-scope] no-match-rewrite-2: {string.Join(' ', rewrite2)}");
    }

    private static (float Score, int OverlapCount, int TotalCount) CalculateGroundingScore(
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> matchedTerms,
        SessionKeywordAtlas? atlas)
    {
        if (queryTerms.Count == 0)
            return (1f, 0, 0);

        var grounded = new HashSet<string>(matchedTerms, StringComparer.Ordinal);
        if (atlas is not null)
        {
            foreach (var term in atlas.HighValueTerms)
                grounded.Add(term);
            foreach (var term in atlas.MatchedTerms)
                grounded.Add(term);
        }

        var overlap = queryTerms.Count(t => grounded.Contains(t));
        return ((float)overlap / queryTerms.Count, overlap, queryTerms.Count);
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

    private static IReadOnlyList<ScoredFolderDetails> ApplyPaging(
        IReadOnlyList<ScoredFolderDetails> results,
        int offset,
        int limit,
        bool diversify,
        int maxPerArea)
    {
        if (results.Count == 0 || limit <= 0 || offset >= results.Count)
            return [];

        if (!diversify)
            return results.Skip(offset).Take(limit).ToArray();

        var selected = new List<ScoredFolderDetails>(offset + limit);
        var areaCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var overflow = new Queue<ScoredFolderDetails>();

        foreach (var item in results)
        {
            var area = AreaKey(item.Path);
            areaCounts.TryGetValue(area, out var count);
            if (count < maxPerArea)
            {
                areaCounts[area] = count + 1;
                selected.Add(item);
            }
            else
            {
                overflow.Enqueue(item);
            }

            if (selected.Count >= offset + limit)
                break;
        }

        while (selected.Count < offset + limit && overflow.Count > 0)
            selected.Add(overflow.Dequeue());

        return selected.Skip(offset).Take(limit).ToArray();
    }

    private static string BuildMatchReason(ScoredFolderDetails folder)
    {
        var matched = folder.MatchedTerms.Take(3).ToArray();
        if (matched.Length == 0)
            return "semantic match";

        return $"matched:{string.Join('+', matched)}";
    }

    private static string FormatList(IReadOnlyList<string> terms)
        => terms.Count == 0 ? "-" : string.Join(", ", terms);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck find-scope — semantic search to find the most relevant folder(s)

            Usage:
              ck find-scope --query <text> [--must <text>] [--repo <path>] [--limit <n>] [--offset <n>] [--max-per-area <n>] [--top <n>] [--min-score <f>] [--explain] [--verbose]

            Options:
              --query <text>      Natural language description of the code area (required)
              --must <text>       Provider/concept to focus on. Boosts folders that contain this
                                  term and penalises folders about competing concepts detected via
                                  embedding similarity (e.g. --must "stripe" boosts Stripe folders
                                  and suppresses Adyen folders without naming them). Can be repeated.
              --repo <path>       Path to git repo root (default: git rev-parse from cwd)
              --top <n>           Hard cap on result count (default: 15; ignored when
                                  --min-score is set without --top)
              --limit <n>         Page size for returned rows (default: 10, max: 20)
              --offset <n>        Page offset into ranked results (default: 0)
              --max-per-area <n>  First-page diversity cap per area key (default: 3)
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
              Pagination metadata is printed to stderr (offset, limit, returned, total_estimate, has_more, next_offset).
              If the result set is broad or ambiguous, a narrowing diagnostic is printed first
              with matched query terms, unmatched query terms, and keyword hints from the wide scope.

            First call auto-builds the index if not present. Progress goes to stderr.
            """);
    }
}
