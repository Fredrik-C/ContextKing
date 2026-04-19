using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class IndexCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return 0;
        }

        var repo       = reader.GetString("--repo");
        var force      = reader.HasFlag("--force");
        var statusOnly = reader.HasFlag("--status");

        string repoRoot;
        try
        {
            repoRoot = GitTracker.GetWorktreeRoot(repo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck index] Error: {ex.Message}");
            return 1;
        }

        if (statusOnly)
        {
            var status = SourceMapBuilder.GetStatus(repoRoot);
            Console.WriteLine(status.ToString().ToLowerInvariant());
            return 0;
        }

        Console.Error.WriteLine($"[ck index] Repo: {repoRoot}");

        using var embedder = ModelLocator.CreateEmbedder();
        var builder  = new SourceMapBuilder(embedder);
        var progress = new Progress<string>(msg => Console.Error.WriteLine($"[ck index] {msg}"));

        await builder.BuildAsync(repoRoot, force, progress);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck index — build or update the semantic source-map index

            Usage:
              ck index [--repo <path>] [--force] [--status]

            Options:
              --repo <path>   Path to git repo root (default: git rev-parse from cwd)
              --force         Force full rebuild instead of incremental update
              --status        Print index status (fresh | stale | missing) and exit
              --help, -h      Show this help
            """);
    }
}
