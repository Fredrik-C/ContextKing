using ContextKing.Core.Embedding;
using ContextKing.Core.Git;

namespace ContextKing.Cli;

/// <summary>
/// Resolves the BGE model directory and constructs <see cref="BgeEmbedder"/> instances.
/// Centralises all model-path heuristics so no CLI command needs to know the layout.
/// </summary>
internal static class ModelLocator
{
    /// <summary>Creates a <see cref="BgeEmbedder"/> pointed at the resolved model directory.</summary>
    internal static BgeEmbedder CreateEmbedder() => new(ResolveModelDir());

    /// <summary>
    /// Locates the <c>bge-small-en-v1.5</c> model directory using the following strategy:
    /// <list type="number">
    ///   <item><c>CK_MODEL_DIR</c> environment variable override (development / CI).</item>
    ///   <item>Git repo root → <c>.claude/models/bge-small-en-v1.5</c> (primary deploy layout).</item>
    ///   <item>Two directories above the exe → <c>models/bge-small-en-v1.5</c> (relative layout).</item>
    ///   <item>Walk up to 8 ancestors from the exe looking for <c>models/bge-small-en-v1.5</c>.</item>
    /// </list>
    /// </summary>
    internal static string ResolveModelDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("CK_MODEL_DIR");
        if (!string.IsNullOrEmpty(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        try
        {
            var repoRoot  = GitTracker.GetWorktreeRoot();
            var candidate = Path.Combine(repoRoot, ".claude", "models", "bge-small-en-v1.5");
            if (Directory.Exists(candidate)) return candidate;
        }
        catch { /* git unavailable — fall through */ }

        // Global user install: ~/.ck/models/bge-small-en-v1.5
        var homeDir     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalModel = Path.Combine(homeDir, ".ck", "models", "bge-small-en-v1.5");
        if (Directory.Exists(globalModel)) return globalModel;

        var exeDir   = AppContext.BaseDirectory;
        var relModel = Path.GetFullPath(
            Path.Combine(exeDir, "..", "..", "models", "bge-small-en-v1.5"));
        if (Directory.Exists(relModel)) return relModel;

        var dir = exeDir;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "models", "bge-small-en-v1.5");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the bge-small-en-v1.5 model directory. " +
            "Install ck globally (see install-global.sh) or set the CK_MODEL_DIR env var.");
    }
}
