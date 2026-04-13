using System.Diagnostics;

namespace ContextKing.Tests.Helpers;

/// <summary>
/// Creates a real git repository in a temporary directory for use in tests.
/// Disposed (deleted) automatically.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "ck-tests-" + Path.GetRandomFileName());

    public TempRepo()
    {
        Directory.CreateDirectory(Root);
        Git("init");
        Git("config --local user.email test@ck.test");
        Git("config --local user.name Test");
    }

    /// <summary>Writes a file relative to the repo root, creating parent directories.</summary>
    public string WriteFile(string relativePath, string content = "// placeholder")
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Deletes a file relative to the repo root from disk (not from git index).</summary>
    public void DeleteFile(string relativePath)
        => File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Stages all changes and creates a commit.</summary>
    public void StageAndCommit(string message = "test")
    {
        Git("add -A");
        Git($"commit -m \"{message}\"");
    }

    public void Git(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory       = Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new Exception(
                $"git {arguments} exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best effort */ }
    }
}
