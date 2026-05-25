using ContextKing.Core.Git;
using ContextKing.Core.Knowledge;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class RecallCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp) { PrintHelp(); return 0; }

        var folder = reader.GetString("--folder");
        var query  = reader.GetString("--query");
        var repo   = reader.GetString("--repo");
        if (!reader.TryGetInt("--top", out var topK) || topK <= 0) topK = 10;

        if (string.IsNullOrWhiteSpace(folder) && string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck recall] Error: --folder or --query is required.");
            PrintHelp();
            return 1;
        }

        string repoRoot;
        try { repoRoot = GitTracker.GetWorktreeRoot(repo); }
        catch (Exception ex) { Console.Error.WriteLine($"[ck recall] Error: {ex.Message}"); return 1; }

        if (!CkConfig.IsBrainEnabled(repoRoot)) return 0;

        var store = new KnowledgeStore(repoRoot);
        if (!store.Exists) return 0;
        var allSnippets = store.ReadAll();
        if (allSnippets.Count == 0) return 0;

        var refreshed = new KnowledgeFreshnessEvaluator(repoRoot)
            .RefreshAll(allSnippets, out var knowledgeChanged);
        if (knowledgeChanged)
            store.ReplaceAll(refreshed);

        // ── Folder mode: read directly from JSONL, no embedding needed ──────────
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var snippets = refreshed
                .Where(s => s.Folders.Any(f => FolderMatches(f, folder!)))
                .OrderByDescending(s => s.CreatedAt)
                .ToArray();

            if (snippets.Length == 0) return 0;

            foreach (var s in snippets)
            {
                var date = s.CreatedAt.Length >= 10 ? s.CreatedAt[..10] : s.CreatedAt;
                var tags = s.Tags.Count > 0 ? $"  tags:{string.Join(",", s.Tags)}" : string.Empty;
                var status = s.Validity is null ? string.Empty : $"  status:{s.Validity.Status}";
                Console.WriteLine($"[{date}] id:{s.Id}{tags}{status}");
                Console.WriteLine(s.Content);
                Console.WriteLine();
            }
            return 0;
        }

        // ── Query mode: semantic search via knowledge index ──────────────────────
        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("[ck recall] No source-map index found — run ck index or ck find-files first to build it.");
            return 1;
        }

        using var embedder = ModelLocator.CreateEmbedder();
        var builder = new KnowledgeIndexBuilder(embedder);
        if (!builder.IsUpToDate(dbPath, repoRoot))
        {
            Console.Error.WriteLine("[ck recall] Building knowledge index...");
            builder.Build(dbPath, repoRoot);
        }

        var searcher = new KnowledgeSearcher(embedder);
        var results  = searcher.SearchByQuery(dbPath, query!, topK);

        if (results.Count == 0) return 0;

        foreach (var r in results)
        {
            Console.WriteLine($"{r.Score:F4}\tid:{r.Id}");
            Console.WriteLine(r.Content);
            Console.WriteLine();
        }

        // Suppress CS1998: method is async to match command signature convention
        await Task.CompletedTask;
        return 0;
    }

    private static bool FolderMatches(string snippetFolder, string queryFolder)
    {
        var snippet = NormalisePath(snippetFolder);
        var query = NormalisePath(queryFolder);
        return string.Equals(snippet, query, StringComparison.OrdinalIgnoreCase)
            || snippet.StartsWith(query + "/", StringComparison.OrdinalIgnoreCase)
            || query.StartsWith(snippet + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalisePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck recall — retrieve institutional knowledge for a folder or query

            Usage:
              ck recall --folder <path> [--repo <path>]
              ck recall --query <text>  [--top <n>] [--repo <path>]

            Options:
              --folder <path>   Return all knowledge snippets for this folder (newest first).
                                No index required. Preferred mode: use after confirming a
                                target folder via ck expand-folder or ck signatures.
              --query <text>    Semantic cross-folder search. Requires the knowledge index
                                (auto-built from .ck-knowledge/snippets.jsonl).
              --top <n>         Max results for --query mode (default: 10)
              --repo <path>     Path to git repo root (default: git rev-parse from cwd)
              --help, -h        Show this help

            Output:
              --folder: [date] id:<uuid>  tags:<t1,t2>
                        <content>

              --query:  <score>\tid:<uuid>
                        <content>

            Silent when no snippets exist — not an error.
            """);
    }
}
