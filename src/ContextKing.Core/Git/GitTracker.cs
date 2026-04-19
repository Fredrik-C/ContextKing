using ContextKing.Core.SourceMap;

namespace ContextKing.Core.Git;

/// <summary>
/// Enumerates source files visible to the index and derives staleness fingerprints.
/// Single responsibility: translate git CLI output into the folder → file → hash shape
/// the indexer consumes. All raw git invocations are delegated to <see cref="GitProcess"/>;
/// all fingerprint hashing is delegated to <see cref="StateKey"/>.
/// </summary>
public static class GitTracker
{
    /// <summary>Returns the absolute path of the worktree root.</summary>
    public static string GetWorktreeRoot(string? startDir = null)
        => GitProcess.GetWorktreeRoot(startDir);

    /// <summary>Returns the abbreviated HEAD commit hash, or "unknown" if unavailable.</summary>
    public static string GetHead(string repoRoot)
        => GitProcess.GetHead(repoRoot);

    /// <summary>
    /// Returns the current branch name (e.g. "main", "feature/foo"),
    /// or "HEAD" when in detached-HEAD state, or "unknown" if git fails.
    /// </summary>
    public static string GetBranch(string repoRoot)
        => GitProcess.GetBranch(repoRoot);

    private static readonly string SourceFilePathspec = SupportedLanguages.GitPathspec;

    /// <summary>
    /// Computes a short fingerprint (16 hex chars) covering the current branch name,
    /// the set of source filenames, and their content hashes.
    /// The fingerprint changes when a file is added, removed, renamed, OR modified.
    /// Used by <see cref="SourceMapBuilder.GetStatus"/> to detect staleness.
    /// </summary>
    public static string ComputeStateKey(string repoRoot, IReadOnlyList<string>? excludeSegments = null)
    {
        excludeSegments ??= DefaultExclusions;

        var entries = new SortedSet<string>(StringComparer.Ordinal);

        // Staged/committed source files with blob hashes
        // git ls-files -s outputs: "<mode> <hash> <stage>\t<relpath>"
        var stagedOutput = GitProcess.Run($"ls-files -s --abbrev=8 {SourceFilePathspec}", repoRoot);
        foreach (var line in stagedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;
            var meta    = line[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hash    = meta.Length >= 2 ? meta[1] : string.Empty;
            var relPath = line[(tabIdx + 1)..].TrimEnd('\r').Replace('\\', '/');
            if (!IsExcluded(relPath, excludeSegments))
                entries.Add($"{relPath}:{hash}");
        }

        // Remove tracked files deleted in the working tree
        try
        {
            var deleted = GitProcess.Run($"ls-files --deleted {SourceFilePathspec}", repoRoot);
            foreach (var line in deleted.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = line.TrimEnd('\r').Replace('\\', '/');
                entries.RemoveWhere(e => e.StartsWith($"{path}:", StringComparison.Ordinal));
            }
        }
        catch { /* git error — skip */ }

        // Add untracked source files with pseudo-hashes
        try
        {
            var others = GitProcess.Run($"ls-files --others --exclude-standard {SourceFilePathspec}", repoRoot);
            foreach (var line in others.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = line.TrimEnd('\r').Replace('\\', '/');
                if (!SupportedLanguages.IsSupported(path)) continue;
                if (IsExcluded(path, excludeSegments)) continue;

                var absPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absPath)) continue;
                var fi = new FileInfo(absPath);
                entries.Add($"{path}:wt:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}");
            }
        }
        catch { /* git error — skip */ }

        return StateKey.Compute(GetBranch(repoRoot), entries);
    }

    /// <summary>
    /// Returns all source files (.cs, .ts, .tsx) visible to the index, grouped by their relative folder path.
    /// "Visible" means: staged/committed files, minus files deleted in the working tree,
    /// plus untracked files present in the working tree.
    /// Key: forward-slash relative folder path (e.g. "src/Modules/Payment").
    /// Value: dictionary of filename → hash (git blob hash for tracked, wt:{mtime}:{size} for untracked).
    /// Folders whose path contains any <paramref name="excludeSegments"/> are omitted.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ListSourceFilesByFolder(
        string repoRoot,
        IReadOnlyList<string>? excludeSegments = null)
    {
        excludeSegments ??= DefaultExclusions;

        // 1. Staged/committed files with blob hashes
        // git ls-files -s outputs: "<mode> <hash> <stage>\t<relpath>"
        var stagedOutput = GitProcess.Run($"ls-files -s --abbrev=8 {SourceFilePathspec}", repoRoot);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var line in stagedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;
            var meta    = line[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hash    = meta.Length >= 2 ? meta[1] : string.Empty;
            var relPath = line[(tabIdx + 1)..].TrimEnd('\r').Replace('\\', '/');
            if (IsExcluded(relPath, excludeSegments)) continue;
            AddFile(result, relPath, hash);
        }

        // 2. Remove tracked files deleted in the working tree but not yet staged for deletion.
        //    These still appear in git ls-files -s but the file is gone from disk.
        try
        {
            var deleted = GitProcess.Run($"ls-files --deleted {SourceFilePathspec}", repoRoot);
            foreach (var line in deleted.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var relPath  = line.TrimEnd('\r').Replace('\\', '/');
                var lastSlash = relPath.LastIndexOf('/');
                var folder   = lastSlash >= 0 ? relPath[..lastSlash] : ".";
                var fileName = lastSlash >= 0 ? relPath[(lastSlash + 1)..] : relPath;

                if (result.TryGetValue(folder, out var files))
                {
                    files.Remove(fileName);
                    if (files.Count == 0) result.Remove(folder);
                }
            }
        }
        catch { /* git error — skip */ }

        // 3. Add untracked source files (working-tree only, not yet staged).
        //    Use wt:{mtime_ticks}:{size} as a pseudo-hash so content/filename changes are detected.
        try
        {
            var others = GitProcess.Run($"ls-files --others --exclude-standard {SourceFilePathspec}", repoRoot);
            foreach (var line in others.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var relPath = line.TrimEnd('\r').Replace('\\', '/');
                if (!SupportedLanguages.IsSupported(relPath)) continue;
                if (IsExcluded(relPath, excludeSegments)) continue;

                var absPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absPath)) continue;

                var fi          = new FileInfo(absPath);
                var pseudoHash  = $"wt:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
                AddFile(result, relPath, pseudoHash);
            }
        }
        catch { /* git error — skip */ }

        return result;
    }

    private static readonly string[] DefaultExclusions = ["Test", "Tests", "Specs"];

    private static void AddFile(
        Dictionary<string, Dictionary<string, string>> result,
        string relPath, string hash)
    {
        var lastSlash = relPath.LastIndexOf('/');
        var folder    = lastSlash >= 0 ? relPath[..lastSlash] : ".";
        var fileName  = lastSlash >= 0 ? relPath[(lastSlash + 1)..] : relPath;

        if (!result.TryGetValue(folder, out var files))
        {
            files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[folder] = files;
        }

        files[fileName] = hash;
    }

    internal static bool IsExcluded(string relPath, IReadOnlyList<string> excludeSegments)
    {
        var segments = relPath.Split('/');
        foreach (var seg in segments)
            foreach (var excl in excludeSegments)
                if (string.Equals(seg, excl, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }
}
