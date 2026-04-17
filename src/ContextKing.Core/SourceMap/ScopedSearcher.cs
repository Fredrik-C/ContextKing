using System.Diagnostics;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Combines semantic folder ranking (<see cref="SourceMapSearcher"/>) with keyword search
/// (git grep) within the top-scoring folders. One tool call replaces find-scope + N grep calls.
/// </summary>
public sealed class ScopedSearcher(SourceMapSearcher searcher)
{
    /// <summary>
    /// Finds the most relevant folders for <paramref name="query"/> (semantic ranking),
    /// then searches within each for <paramref name="pattern"/> using git grep.
    /// </summary>
    public ScopedSearchResult Search(
        string dbPath,
        string repoRoot,
        string query,
        string pattern,
        int topK = 10,
        float minScore = 0f,
        bool ignoreCase = true)
    {
        var folders = searcher.Search(dbPath, query, topK, minScore);
        if (folders.Count == 0)
            return new ScopedSearchResult([], []);

        var matches = new List<ScopedMatch>();

        foreach (var folder in folders)
        {
            var folderMatches = GitGrep(repoRoot, folder.Path, pattern, ignoreCase);
            matches.AddRange(folderMatches.Select(m => m with { FolderScore = folder.Score }));
        }

        return new ScopedSearchResult(folders, matches);
    }

    private static List<ScopedMatch> GitGrep(
        string repoRoot, string folderPath, string pattern, bool ignoreCase)
    {
        var argList = new List<string> { "grep", "-n", "--no-color" };
        if (ignoreCase) argList.Add("-i");
        argList.AddRange(["-e", pattern, "--", $"{folderPath}/"]);

        try
        {
            var output = RunGit(argList, repoRoot);
            var results = new List<ScopedMatch>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // git grep output: <file>:<line-number>:<content>
                var firstColon = line.IndexOf(':');
                if (firstColon < 0) continue;

                var secondColon = line.IndexOf(':', firstColon + 1);
                if (secondColon < 0) continue;

                var file = line[..firstColon];
                if (!int.TryParse(line[(firstColon + 1)..secondColon], out var lineNum)) continue;
                var content = line[(secondColon + 1)..].TrimEnd('\r');

                results.Add(new ScopedMatch(file, lineNum, content.Trim(), 0f));
            }

            return results;
        }
        catch
        {
            // git grep returns exit code 1 when no matches — not an error
            return [];
        }
    }

    private static string RunGit(IReadOnlyList<string> arguments, string workDir)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && process.ExitCode != 1)
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}");

        return stdout;
    }
}

/// <summary>A single grep match within a scoped folder.</summary>
public readonly record struct ScopedMatch(string File, int Line, string Content, float FolderScore);

/// <summary>Result of a scoped search: the ranked folders and the matches found within them.</summary>
public readonly record struct ScopedSearchResult(
    IReadOnlyList<ScoredFolder> Folders,
    IReadOnlyList<ScopedMatch> Matches);
