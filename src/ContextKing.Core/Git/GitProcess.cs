using System.Diagnostics;

namespace ContextKing.Core.Git;

/// <summary>
/// Thin wrapper over the <c>git</c> CLI. Single responsibility: spawn a git process,
/// capture stdout, translate non-zero exits into exceptions. Contains no domain
/// knowledge about source files, exclusions, or indexing state.
/// </summary>
internal static class GitProcess
{
    /// <summary>Runs <c>git <paramref name="arguments"/></c> in <paramref name="workDir"/> and returns stdout.</summary>
    /// <exception cref="InvalidOperationException">The process could not be started or exited non-zero.</exception>
    public static string Run(string arguments, string workDir)
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

    /// <summary>Returns the worktree root (<c>git rev-parse --show-toplevel</c>) for <paramref name="startDir"/>.</summary>
    public static string GetWorktreeRoot(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        return Run("rev-parse --show-toplevel", dir).Trim();
    }

    /// <summary>Abbreviated HEAD commit hash, or "unknown" if git fails.</summary>
    public static string GetHead(string repoRoot)
    {
        try { return Run("rev-parse --short HEAD", repoRoot).Trim(); }
        catch { return "unknown"; }
    }

    /// <summary>Current branch name, "HEAD" in detached state, or "unknown" if git fails.</summary>
    public static string GetBranch(string repoRoot)
    {
        try { return Run("rev-parse --abbrev-ref HEAD", repoRoot).Trim(); }
        catch { return "unknown"; }
    }
}
