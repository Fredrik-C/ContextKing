using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;
using ContextKing.Cli;

namespace ContextKing.Cli.Commands;

internal static class FindScopeCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? query    = null;
        string? repo     = null;
        int     topK     = 10;
        float   minScore = 0f;
        bool    topKSet  = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--query"     when i + 1 < args.Length: query = args[++i]; break;
                case "--repo"      when i + 1 < args.Length: repo  = args[++i]; break;
                case "--top"       when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int k) && k > 0) { topK = k; topKSet = true; }
                    break;
                case "--min-score" when i + 1 < args.Length:
                    if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0f)
                        minScore = s;
                    break;
                case "--help": case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        // When --min-score is the primary filter and --top was not explicitly set,
        // remove the count cap so the threshold alone controls how many results come back.
        if (minScore > 0f && !topKSet)
            topK = int.MaxValue;

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

        // Auto-build index on first use if missing or stale
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
        var results  = searcher.Search(dbPath, query, topK, minScore);

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
              ck find-scope --query <text> [--repo <path>] [--top <n>] [--min-score <f>]

            Options:
              --query <text>      Natural language description of the code area (required)
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
