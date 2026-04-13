using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ContextKing.Core.Git;

/// <summary>
/// Wraps git CLI calls to discover worktree structure and enumerate tracked .cs files.
/// </summary>
public static class GitTracker
{
    /// <summary>
    /// Returns the absolute path of the worktree root (git rev-parse --show-toplevel).
    /// </summary>
    public static string GetWorktreeRoot(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        return RunGit("rev-parse --show-toplevel", dir).Trim();
    }

    /// <summary>
    /// Returns the abbreviated HEAD commit hash, or "unknown" if unavailable.
    /// </summary>
    public static string GetHead(string repoRoot)
    {
        try { return RunGit("rev-parse --short HEAD", repoRoot).Trim(); }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Returns the current branch name (e.g. "main", "feature/foo"),
    /// or "HEAD" when in detached-HEAD state, or "unknown" if git fails.
    /// </summary>
    public static string GetBranch(string repoRoot)
    {
        try { return RunGit("rev-parse --abbrev-ref HEAD", repoRoot).Trim(); }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Computes a short fingerprint (16 hex chars) covering the current branch name,
    /// the set of .cs filenames, and their content hashes.
    /// The fingerprint changes when a file is added, removed, renamed, OR modified.
    /// Used by <see cref="SourceMap.SourceMapBuilder.GetStatus"/> to detect staleness.
    /// </summary>
    public static string ComputeStateKey(string repoRoot, IReadOnlyList<string>? excludeSegments = null)
    {
        excludeSegments ??= ["Test", "Tests", "Specs"];

        // Staged/committed .cs files with blob hashes
        // git ls-files -s outputs: "<mode> <hash> <stage>\t<relpath>"
        var stagedOutput = RunGit("ls-files -s --abbrev=8 -- *.cs", repoRoot);
        var entries = new SortedSet<string>(StringComparer.Ordinal);

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
            var deleted = RunGit("ls-files --deleted -- *.cs", repoRoot);
            foreach (var line in deleted.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = line.TrimEnd('\r').Replace('\\', '/');
                entries.RemoveWhere(e => e.StartsWith($"{path}:", StringComparison.Ordinal));
            }
        }
        catch { /* git error — skip */ }

        // Add untracked .cs files with pseudo-hashes
        try
        {
            var others = RunGit("ls-files --others --exclude-standard -- *.cs", repoRoot);
            foreach (var line in others.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = line.TrimEnd('\r').Replace('\\', '/');
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExcluded(path, excludeSegments)) continue;

                var absPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absPath)) continue;
                var fi = new FileInfo(absPath);
                entries.Add($"{path}:wt:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}");
            }
        }
        catch { /* git error — skip */ }

        var branch = GetBranch(repoRoot);
        var text   = $"{branch}\n{string.Join('\n', entries)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>
    /// Returns all .cs files visible to the index, grouped by their relative folder path.
    /// "Visible" means: staged/committed files, minus files deleted in the working tree,
    /// plus untracked files present in the working tree.
    /// Key: forward-slash relative folder path (e.g. "src/Modules/Payment").
    /// Value: dictionary of filename → hash (git blob hash for tracked, wt:{mtime}:{size} for untracked).
    /// Folders whose path contains any <paramref name="excludeSegments"/> are omitted.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ListCsFilesByFolder(
        string repoRoot,
        IReadOnlyList<string>? excludeSegments = null)
    {
        excludeSegments ??= ["Test", "Tests", "Specs"];

        // 1. Staged/committed files with blob hashes
        // git ls-files -s outputs: "<mode> <hash> <stage>\t<relpath>"
        var stagedOutput = RunGit("ls-files -s --abbrev=8 -- *.cs", repoRoot);
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
            var deleted = RunGit("ls-files --deleted -- *.cs", repoRoot);
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

        // 3. Add untracked .cs files (working-tree only, not yet staged).
        //    Use wt:{mtime_ticks}:{size} as a pseudo-hash so content/filename changes are detected.
        try
        {
            var others = RunGit("ls-files --others --exclude-standard -- *.cs", repoRoot);
            foreach (var line in others.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var relPath = line.TrimEnd('\r').Replace('\\', '/');
                if (!relPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static string RunGit(string arguments, string workDir)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} exited with code {process.ExitCode}: {stderr.Trim()}");

        return stdout;
    }
}
