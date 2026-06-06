using ContextKing.Core.Git;
using ContextKing.Core.Knowledge;

namespace ContextKing.Cli.Commands;

internal static class LearnCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp) { PrintHelp(); return 0; }

        var content    = reader.GetString("--content");
        var tagsRaw    = reader.GetString("--tags");
        var foldersRaw = reader.GetString("--folders");
        var source     = reader.GetString("--source") ?? "agent";
        var repo       = reader.GetString("--repo");

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.Error.WriteLine("[ck learn] Error: --content is required.");
            PrintHelp();
            return 1;
        }

        string repoRoot;
        try { repoRoot = GitTracker.GetWorktreeRoot(repo); }
        catch (Exception ex) { Console.Error.WriteLine($"[ck learn] Error: {ex.Message}"); return 1; }

        if (!CkConfig.IsBrainEnabled(repoRoot)) return 0;

        var tags    = ParseCommaSeparated(tagsRaw);
        var folders = ParseCommaSeparated(foldersRaw);
        var id      = Guid.NewGuid().ToString();

        var snippet = new KnowledgeSnippet
        {
            Id         = id,
            Content    = content.Trim(),
            Tags       = tags,
            Folders    = folders,
            Source     = source,
            CreatedAt  = DateTime.UtcNow.ToString("O"),
            SchemaVersion = 2,
        };

        new KnowledgeStore(repoRoot).Append(snippet);
        Console.WriteLine(id);

        await Task.CompletedTask;
        return 0;
    }

    private static IReadOnlyList<string> ParseCommaSeparated(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck learn — append a knowledge snippet to a session JSONL file under .ck-knowledge/

            Knowledge capture is OPTIONAL and is a no-op on most sessions. Run it only when you
            have reached a durable, non-obvious conclusion — never to describe the changes you made.

            Usage:
              ck learn --content "<text>" [--tags <t1,t2,...>] [--folders <f1,f2,...>]
                       [--source <human|agent>] [--repo <path>]

            Options:
              --content <text>       The conclusion to record (required when calling). Plain English,
                                     1–3 sentences. Capture only what a future engineer could NOT
                                     recover by reading the code. No file paths or symbol names.
              --tags <t1,t2,...>     Comma-separated keywords for exact-match boost during recall
              --folders <f1,f2,...>  Comma-separated folder paths this snippet applies to
              --source <name>        Source label (default: "agent")
              --repo <path>          Path to git repo root (default: auto-detect)
              --help, -h             Show this help

            Output:
              The new snippet's UUID (stdout). Creates .ck-knowledge/sessions/ if absent.

            Guidelines:
              Record: the WHY behind a non-obvious decision, gotchas/constraints that cost time,
                      cross-module relationships not visible in any single file.
              Skip:   a description/changelog of the changes you made (git history has that),
                      anything derivable by reading the code or via `ck signatures`
                      (method/symbol names, parameter types, file paths),
                      task-specific observations that don't generalise.
              Litmus: if your draft restates the diff or could be answered by opening the file,
                      it is not knowledge — skip it. Skipping is the common, correct outcome.
            """);
    }
}
