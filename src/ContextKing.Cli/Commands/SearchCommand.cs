using ContextKing.Core.Git;
using ContextKing.Core.Search;
using ContextKing.Core.SourceMap;
using ContextKing.Cli;

namespace ContextKing.Cli.Commands;

internal static class SearchCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? query    = null;
        string? pattern  = null;
        string? name     = null;
        string? typeStr  = null;
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
                case "--name"      when i + 1 < args.Length: name    = args[++i]; break;
                case "--type"      when i + 1 < args.Length: typeStr = args[++i]; break;
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

        // Determine search mode: typed (--type + --name) or raw (--pattern)
        SearchType? searchType = null;
        if (typeStr is not null)
        {
            if (!TryParseSearchType(typeStr, out var parsed))
            {
                Console.Error.WriteLine($"[ck search] Error: unknown --type '{typeStr}'. Valid types: class, method, member, file.");
                return 1;
            }
            searchType = parsed;
        }

        bool hasTyped  = searchType is not null || name is not null;
        bool hasRaw    = pattern is not null;

        if (!hasTyped && !hasRaw)
        {
            Console.Error.WriteLine("[ck search] Error: provide either --name (with optional --type) or --pattern.");
            PrintHelp();
            return 1;
        }

        if (hasTyped && hasRaw)
        {
            Console.Error.WriteLine("[ck search] Error: --pattern cannot be combined with --type/--name. Use one mode or the other.");
            return 1;
        }

        if (hasTyped && string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("[ck search] Error: --name is required when using --type.");
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

        ScopedSearchResult result;

        if (searchType is not null)
        {
            result = scopedSearcher.SearchTyped(
                dbPath, repoRoot, query, searchType.Value, name!,
                topK, minScore, ignoreCase: !caseSensitive);

            var effectivePattern = SearchPatternRegistry.BuildPattern(searchType.Value, name!) ?? name!;
            EmitDedupHintIfNeeded(repoRoot, result.Folders, effectivePattern);
        }
        else
        {
            // --name without --type: use name as a plain keyword pattern
            var effectivePattern = pattern ?? name!;
            result = scopedSearcher.Search(
                dbPath, repoRoot, query, effectivePattern,
                topK, minScore, ignoreCase: !caseSensitive);

            EmitDedupHintIfNeeded(repoRoot, result.Folders, effectivePattern);
        }

        if (result.Matches.Count == 0)
        {
            var searchTerm = name ?? pattern;
            Console.Error.WriteLine($"[ck search] No matches for '{searchTerm}' in top {result.Folders.Count} folders.");
            foreach (var f in result.Folders)
                Console.Error.WriteLine($"  {f.Score:F4}\t{f.Path}");
            return 0;
        }

        // Group matches by folder, preserving folder score order
        var folderOrder = result.Folders.Select(f => f.Path).ToList();
        var matchesByFolder = result.Matches
            .GroupBy(m =>
            {
                foreach (var fp in folderOrder)
                    if (m.File.StartsWith(fp + "/", StringComparison.Ordinal) || m.File.StartsWith(fp + "\\", StringComparison.Ordinal))
                        return fp;
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

    private static bool TryParseSearchType(string value, out SearchType result)
    {
        result = value.ToLowerInvariant() switch
        {
            "class"  => SearchType.Class,
            "method" => SearchType.Method,
            "member" => SearchType.Member,
            "file"   => SearchType.File,
            _ => (SearchType)(-1),
        };
        return (int)result >= 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck search — scoped keyword search (semantic folder ranking + git grep)

            Usage:
              ck search --query <scope-text> --name <symbol> [--type <kind>] [options]
              ck search --query <scope-text> --pattern <regex> [options]

            Two modes:
              Typed search (preferred):
                --name <symbol>     Symbol name to search for (e.g. "TerminalGateway")
                --type <kind>       Symbol type: class, method, member, file
                                    Generates language-aware regex automatically.
                                    If omitted with --name, searches as a plain keyword.

              Raw pattern (fallback):
                --pattern <regex>   Raw regex for git grep (when you need full control)

            Combines semantic folder ranking with keyword search in one call.
            First ranks folders by semantic relevance to --query, then searches
            within the top folders.

            Options:
              --query <text>      Semantic scope description (same as find-scope) (required)
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
