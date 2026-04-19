using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

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
        var topKSet   = reader.TryGetInt("--top", out var topK) && topK > 0;
        if (!topKSet) topK = 10;

        var minScore = 0f;
        if (reader.TryGetFloat("--min-score", out var parsedMin) && parsedMin >= 0f)
        {
            minScore = parsedMin;
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
            Console.Error.WriteLine(
                status == IndexStatus.Missing
                    ? "[ck find-scope] No index found — building now (first-time setup)..."
                    : "[ck find-scope] Index is stale — refreshing...");

            using var buildEmbedder = ModelLocator.CreateEmbedder();
            var builder  = new SourceMapBuilder(buildEmbedder);
            var progress = new Progress<string>(msg => Console.Error.WriteLine($"[ck find-scope] {msg}"));
            await builder.BuildAsync(repoRoot, false, progress);
        }

        using var searchEmbedder = ModelLocator.CreateEmbedder();
        var searcher = new SourceMapSearcher(searchEmbedder);
        var results  = searcher.Search(dbPath, query, topK, minScore,
            mustTexts.Count > 0 ? mustTexts : null);

        if (results.Count == 0)
        {
            Console.Error.WriteLine("[ck find-scope] No results found.");
            return 0;
        }

        foreach (var r in results)
            Console.WriteLine($"{r.Score:F4}\t{r.Path}");

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck find-scope — semantic search to find the most relevant folder(s)

            Usage:
              ck find-scope --query <text> [--must <text>] [--repo <path>] [--top <n>] [--min-score <f>]

            Options:
              --query <text>      Natural language description of the code area (required)
              --must <text>       Provider/concept to focus on. Boosts folders that contain this
                                  term and penalises folders about competing concepts detected via
                                  embedding similarity (e.g. --must "stripe" boosts Stripe folders
                                  and suppresses Adyen folders without naming them). Can be repeated.
              --repo <path>       Path to git repo root (default: git rev-parse from cwd)
              --top <n>           Hard cap on result count (default: 10; ignored when
                                  --min-score is set without --top)
              --min-score <f>     Exclude folders with score below this threshold (0.0–1.15).
                                  When specified without --top, returns ALL folders above the
                                  threshold. Combine with --top to cap a score-filtered list.
                                  Recommended: 0.5 for impact analysis, 0.65 for targeted search.
              --help, -h          Show this help

            Output (stdout):
              <score>\t<relative-folder-path>
              One line per result, score is cosine similarity [0..1] + exact-match bonus (≤0.30).

            First call auto-builds the index if not present. Progress goes to stderr.
            """);
    }
}
