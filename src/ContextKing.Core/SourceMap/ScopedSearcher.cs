using System.Diagnostics;
using ContextKing.Core.Search;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Combines semantic folder ranking (<see cref="SourceMapSearcher"/>) with keyword search
/// (git grep) within the top-scoring folders. One tool call replaces find-scope + N grep calls.
/// </summary>
public sealed class ScopedSearcher(SourceMapSearcher searcher)
{
    /// <summary>
    /// Searches using a raw pattern (legacy/fallback path).
    /// </summary>
    public ScopedSearchResult Search(
        string dbPath,
        string repoRoot,
        string query,
        string pattern,
        int topK = 10,
        float minScore = 0f,
        bool ignoreCase = true,
        IReadOnlyList<string>? mustTexts = null)
    {
        var folders = searcher.Search(dbPath, query, topK, minScore, mustTexts);
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

    /// <summary>
    /// Type-aware search: generates the grep pattern from <paramref name="searchType"/>
    /// and <paramref name="name"/> using the language-specific pattern providers.
    /// For <see cref="SearchType.File"/>, matches filenames instead of content.
    /// </summary>
    public ScopedSearchResult SearchTyped(
        string dbPath,
        string repoRoot,
        string query,
        SearchType searchType,
        string name,
        int topK = 10,
        float minScore = 0f,
        bool ignoreCase = true,
        IReadOnlyList<string>? mustTexts = null)
    {
        var folders = searcher.Search(dbPath, query, topK, minScore, mustTexts);
        if (folders.Count == 0)
            return new ScopedSearchResult([], []);

        if (searchType == SearchType.File)
            return SearchByFilename(repoRoot, folders, name, ignoreCase);

        var pattern = SearchPatternRegistry.BuildPattern(searchType, name);
        if (pattern is null)
            return new ScopedSearchResult(folders, []);

        var matches = new List<ScopedMatch>();
        foreach (var folder in folders)
        {
            var folderMatches = GitGrepExtended(repoRoot, folder.Path, pattern, ignoreCase);
            matches.AddRange(folderMatches.Select(m => m with { FolderScore = folder.Score }));
        }

        return new ScopedSearchResult(folders, matches);
    }

    /// <summary>
    /// Fast path: repo-wide git grep for a typed pattern, skipping the semantic index entirely.
    /// Returns null if no matches found (caller should fall back to scoped search).
    /// </summary>
    public static ScopedSearchResult? SearchRepoWide(
        string repoRoot,
        SearchType searchType,
        string name,
        bool ignoreCase = true)
    {
        if (searchType == SearchType.File)
            return SearchRepoWideByFilename(repoRoot, name, ignoreCase);

        var pattern = SearchPatternRegistry.BuildPattern(searchType, name);
        if (pattern is null)
            return null;

        var matches = GitGrepExtendedRepoWide(repoRoot, pattern, ignoreCase);
        if (matches.Count == 0)
            return null;

        // Derive folder list from matches
        var folders = matches
            .Select(m =>
            {
                var lastSlash = m.File.LastIndexOf('/');
                return lastSlash >= 0 ? m.File[..lastSlash] : ".";
            })
            .Distinct(StringComparer.Ordinal)
            .Select(p => new ScoredFolder(p, 1.0f))
            .ToList();

        return new ScopedSearchResult(folders, matches);
    }

    private static ScopedSearchResult? SearchRepoWideByFilename(
        string repoRoot, string name, bool ignoreCase)
    {
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var output = RunGitSafe(["ls-files"], repoRoot);
        var matches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.TrimEnd('\r'))
            .Where(f => Path.GetFileNameWithoutExtension(f).Contains(name, comparison))
            .Select(f =>
            {
                var lastSlash = f.LastIndexOf('/');
                return new ScopedMatch(f, 0, $"[file] {Path.GetFileName(f)}", 1.0f);
            })
            .ToList();

        if (matches.Count == 0)
            return null;

        var folders = matches
            .Select(m =>
            {
                var lastSlash = m.File.LastIndexOf('/');
                return lastSlash >= 0 ? m.File[..lastSlash] : ".";
            })
            .Distinct(StringComparer.Ordinal)
            .Select(p => new ScoredFolder(p, 1.0f))
            .ToList();

        return new ScopedSearchResult(folders, matches);
    }

    private static List<ScopedMatch> GitGrepExtendedRepoWide(
        string repoRoot, string pattern, bool ignoreCase)
    {
        var argList = new List<string> { "grep", "-n", "--no-color", "-P" };
        if (ignoreCase) argList.Add("-i");
        argList.AddRange(["-e", pattern]);

        return ParseGrepOutput(RunGitSafe(argList, repoRoot));
    }

    /// <summary>
    /// For SearchType.File: lists tracked files in each folder whose name contains the search term.
    /// </summary>
    private static ScopedSearchResult SearchByFilename(
        string repoRoot,
        IReadOnlyList<ScoredFolder> folders,
        string name,
        bool ignoreCase)
    {
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var matches = new List<ScopedMatch>();

        foreach (var folder in folders)
        {
            var files = ListTrackedFiles(repoRoot, folder.Path);
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Contains(name, comparison))
                    matches.Add(new ScopedMatch(file, 0, $"[file] {Path.GetFileName(file)}", folder.Score));
            }
        }

        return new ScopedSearchResult(folders, matches);
    }

    private static List<string> ListTrackedFiles(string repoRoot, string folderPath)
    {
        try
        {
            var output = RunGit(["ls-files", "--", $"{folderPath}/"], repoRoot);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.TrimEnd('\r'))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<ScopedMatch> GitGrep(
        string repoRoot, string folderPath, string pattern, bool ignoreCase)
    {
        var argList = new List<string> { "grep", "-n", "--no-color" };
        if (ignoreCase) argList.Add("-i");
        argList.AddRange(["-e", pattern, "--", $"{folderPath}/"]);

        return ParseGrepOutput(RunGitSafe(argList, repoRoot));
    }

    /// <summary>
    /// Uses git grep with -P (Perl regex) for type-generated patterns that use \s, alternation, etc.
    /// </summary>
    private static List<ScopedMatch> GitGrepExtended(
        string repoRoot, string folderPath, string pattern, bool ignoreCase)
    {
        var argList = new List<string> { "grep", "-n", "--no-color", "-P" };
        if (ignoreCase) argList.Add("-i");
        argList.AddRange(["-e", pattern, "--", $"{folderPath}/"]);

        return ParseGrepOutput(RunGitSafe(argList, repoRoot));
    }

    private static List<ScopedMatch> ParseGrepOutput(string output)
    {
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

    private static string RunGitSafe(IReadOnlyList<string> arguments, string workDir)
    {
        try
        {
            return RunGit(arguments, workDir);
        }
        catch
        {
            return string.Empty;
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
