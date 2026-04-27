using ContextKing.Core.Git;
using ContextKing.Core.Knowledge;

namespace ContextKing.Cli.Commands;

internal static class ForgetCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp) { PrintHelp(); return 0; }

        var id   = reader.GetString("--id");
        var repo = reader.GetString("--repo");

        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("[ck forget] Error: --id is required.");
            PrintHelp();
            return 1;
        }

        string repoRoot;
        try { repoRoot = GitTracker.GetWorktreeRoot(repo); }
        catch (Exception ex) { Console.Error.WriteLine($"[ck forget] Error: {ex.Message}"); return 1; }

        if (!CkConfig.IsBrainEnabled(repoRoot)) return 0;

        var deleted = new KnowledgeStore(repoRoot).Delete(id);
        if (!deleted)
            Console.Error.WriteLine($"[ck forget] Snippet '{id}' not found.");

        await Task.CompletedTask;
        return deleted ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck forget — remove a knowledge snippet from .ck-knowledge/snippets.jsonl

            Usage:
              ck forget --id <uuid> [--repo <path>]

            Options:
              --id <uuid>    The snippet ID to remove (required). Get the ID from ck recall output.
              --repo <path>  Path to git repo root (default: auto-detect)
              --help, -h     Show this help

            Use this when a snippet is stale: the code it describes has been refactored, the
            constraint no longer applies, or the information is incorrect.
            """);
    }
}
