using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;
using ContextKing.Cli;

namespace ContextKing.Cli.Commands;

internal static class SearchCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? query    = null;
        string? pattern  = null;
        string? repo     = null;
        int     topK     = 10;
        float   minScore = 0f;
        bool    topKSet  = false;
        bool    caseSensitive = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--query"     when i + 1 < args.Length: query   = args[++i]; break;
                case "--pattern"   when i + 1 < args.Length: pattern = args[++i]; break;
                case "--repo"      when i + 1 < args.Length: repo    = args[++i]; break;
                case "--top"       when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int k) && k > 0) { topK = k; topKSet = true; }
                    break;
                case "--min-score" when i + 1 < args.Length:
                    if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0f)
                        minScore = s;
                    break;
                case "--case-sensitive": caseSensitive = true; break;
                case "--help": case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        if (minScore > 0f && !topKSet)
            topK = int.MaxValue;

        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck search] Error: --query is required.");
            PrintHelp();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            Console.Error.WriteLine("[ck search] Error: --pattern is required.");
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
            Console.Error.WriteLine($"[ck search] Error: {ex.Message}");
            return 1;
        }

        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);

        // Auto-build index on first use if missing or stale
        var status = SourceMapBuilder.GetStatus(repoRoot);
        if (status != IndexStatus.Fresh)
        {
            Console.Error.WriteLine(
                status == IndexStatus.Missing
                    ? "[ck search] No index found — building now (first-time setup)..."
                    : "[ck search] Index is stale — refreshing...");

            using var buildEmbedder = ModelLocator.CreateEmbedder();
            var builder  = new SourceMapBuilder(buildEmbedder);
            var progress = new Progress<string>(msg => Console.Error.WriteLine($"[ck search] {msg}"));
            await builder.BuildAsync(repoRoot, false, progress);
        }

        using var searchEmbedder = ModelLocator.CreateEmbedder();
        var searcher       = new SourceMapSearcher(searchEmbedder);
        var scopedSearcher = new ScopedSearcher(searcher);

        var result = scopedSearcher.Search(
            dbPath, repoRoot, query, pattern, topK, minScore, ignoreCase: !caseSensitive);

        // Guard: detect repeated searches returning the same folders
        EmitDedupHintIfNeeded(repoRoot, result.Folders, pattern!);

        if (result.Matches.Count == 0)
        {
            // Still show the folders that were searched
            Console.Error.WriteLine($"[ck search] No matches for '{pattern}' in top {result.Folders.Count} folders.");
            foreach (var f in result.Folders)
                Console.Error.WriteLine($"  {f.Score:F4}\t{f.Path}");
            return 0;
        }

        // Group matches by folder, preserving folder score order
        var folderOrder = result.Folders.Select(f => f.Path).ToList();
        var matchesByFolder = result.Matches
            .GroupBy(m =>
            {
                // Find which folder this file belongs to
                foreach (var fp in folderOrder)
                    if (m.File.StartsWith(fp + "/", StringComparison.Ordinal) || m.File.StartsWith(fp + "\\", StringComparison.Ordinal))
                        return fp;
                // Fallback: use the file's directory
                var lastSlash = m.File.LastIndexOf('/');
                return lastSlash >= 0 ? m.File[..lastSlash] : ".";
            })
            .OrderBy(g => folderOrder.IndexOf(g.Key) is var idx && idx >= 0 ? idx : int.MaxValue)
            .ToList();

        foreach (var group in matchesByFolder)
        {
            var folder = result.Folders.FirstOrDefault(f => f.Path == group.Key);
            Console.WriteLine($"{folder.Score:F4}\t{group.Key}");
            foreach (var match in group)
                Console.WriteLine($"  {match.File}:{match.Line}: {match.Content}");
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck search — scoped keyword search (semantic folder ranking + git grep)

            Usage:
              ck search --query <scope-text> --pattern <keyword> [options]

            Combines semantic folder ranking with keyword search in one call.
            First ranks folders by semantic relevance to --query, then searches
            within the top folders for --pattern using git grep.

            Options:
              --query <text>      Semantic scope description (same as find-scope) (required)
              --pattern <text>    Keyword or regex to search for within scoped folders (required)
              --repo <path>       Path to git repo root (default: git rev-parse from cwd)
              --top <n>           Number of folders to search within (default: 10)
              --min-score <f>     Exclude folders with score below this threshold
              --case-sensitive    Make pattern matching case-sensitive (default: case-insensitive)
              --help, -h          Show this help

            Output (stdout):
              <score>\t<folder-path>
                <file>:<line>: <matching-content>

            Matches are grouped by folder, ordered by semantic relevance score.
            First call auto-builds the index if not present. Progress goes to stderr.
            """);
    }

    /// <summary>
    /// Detects when repeated ck search calls return the same top folders, which indicates
    /// the agent is re-running with rephrased --query text instead of changing --pattern.
    /// Also tracks total search count and nudges the agent to commit to signatures after
    /// too many searches.
    /// </summary>
    private static void EmitDedupHintIfNeeded(
        string repoRoot, IReadOnlyList<ScoredFolder> folders, string currentPattern)
    {
        try
        {
            var indexDir = Path.Combine(repoRoot, ".ck-index");
            var stateFile = Path.Combine(indexDir, "last-search-state.txt");

            var topFolders = folders.Take(5).Select(f => f.Path).Order().ToList();
            var currentKey = string.Join("|", topFolders);

            int searchCount = 1;

            if (File.Exists(stateFile))
            {
                var lines = File.ReadAllLines(stateFile);
                if (lines.Length >= 3 && int.TryParse(lines[2], out var prev))
                    searchCount = prev + 1;

                if (lines.Length >= 2)
                {
                    var previousKey = lines[0];
                    var previousPattern = lines[1];
                    if (previousKey == currentKey && previousPattern == currentPattern)
                    {
                        Console.Error.WriteLine(
                            "[ck hint] Same folders and pattern as previous search — rephrasing --query " +
                            "does not change results. Either change --pattern to search for a different " +
                            "keyword, or proceed to 'ck signatures' on the returned folders.");
                    }
                }
            }

            // Escalating nudge based on total search count in this session
            if (searchCount == 8)
            {
                Console.Error.WriteLine(
                    "[ck hint] You have run 8 searches. Stop searching and commit to the folders " +
                    "you have. Use 'ck signatures' on the most relevant folders, then " +
                    "'ck get-method-source' to read specific methods.");
            }
            else if (searchCount > 12)
            {
                Console.Error.WriteLine(
                    $"[ck hint] {searchCount} searches so far — this is excessive. Each search " +
                    "adds to your context window. Proceed with 'ck signatures' on the folders " +
                    "you already found.");
            }

            if (Directory.Exists(indexDir))
                File.WriteAllText(stateFile, $"{currentKey}\n{currentPattern}\n{searchCount}\n");
        }
        catch
        {
            // Best-effort hint — never fail the search.
        }
    }
}
