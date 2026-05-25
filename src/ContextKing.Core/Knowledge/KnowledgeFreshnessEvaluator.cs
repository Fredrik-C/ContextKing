using System.Security.Cryptography;
using System.Text;
using ContextKing.Core.Git;

namespace ContextKing.Core.Knowledge;

/// <summary>
/// Computes branch-agnostic, content-based freshness for folder-scoped knowledge snippets.
/// Designed for lazy backfill so legacy snippets remain fully readable.
/// </summary>
public sealed class KnowledgeFreshnessEvaluator(string repoRoot)
{
    private const int CurrentSchemaVersion = 2;

    public IReadOnlyList<KnowledgeSnippet> RefreshAll(
        IReadOnlyList<KnowledgeSnippet> snippets,
        out bool changed)
    {
        changed = false;
        if (snippets.Count == 0) return snippets;

        var filesByFolder = GitTracker.ListSourceFilesByFolder(repoRoot);
        var fileHashes = FlattenFileHashes(filesByFolder);

        var currentBranch = GitTracker.GetBranch(repoRoot);
        var currentHead = GitTracker.GetHead(repoRoot);

        var refreshed = new KnowledgeSnippet[snippets.Count];
        for (var i = 0; i < snippets.Count; i++)
        {
            var updated = RefreshSnippet(snippets[i], fileHashes, currentBranch, currentHead);
            refreshed[i] = updated;
            if (!ReferenceEquals(updated, snippets[i]) && updated != snippets[i])
                changed = true;
        }

        return refreshed;
    }

    private static KnowledgeSnippet RefreshSnippet(
        KnowledgeSnippet snippet,
        IReadOnlyDictionary<string, string> fileHashes,
        string currentBranch,
        string currentHead)
    {
        var normalizedFolders = snippet.Folders
            .Select(NormalizePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? currentScopeHash = null;
        string status;
        float confidence;

        if (normalizedFolders.Length == 0)
        {
            status = "unknown";
            confidence = 0.50f;
        }
        else
        {
            currentScopeHash = ComputeSemanticScopeHash(fileHashes, normalizedFolders);
            var storedScopeHash = snippet.Fingerprints?.SemanticScopeHash;

            if (string.IsNullOrWhiteSpace(storedScopeHash))
            {
                status = "fresh";
                confidence = 0.85f;
            }
            else if (string.Equals(storedScopeHash, currentScopeHash, StringComparison.Ordinal))
            {
                status = "fresh";
                confidence = 0.99f;
            }
            else
            {
                status = "review_needed";
                confidence = 0.40f;
            }
        }

        var now = DateTime.UtcNow.ToString("O");
        var nextValidity = new KnowledgeValidity
        {
            Status = status,
            Confidence = confidence,
            // only stamp when missing or status changed to avoid rewrite churn
            ValidatedAt = snippet.Validity?.Status == status
                ? snippet.Validity?.ValidatedAt ?? now
                : now
        };

        var storedScope = snippet.Fingerprints?.SemanticScopeHash;
        var baselineScopeHash = string.IsNullOrWhiteSpace(storedScope)
            ? currentScopeHash
            : storedScope;
        var contextHash = ComputeContextHash(currentBranch, currentHead, currentScopeHash);
        var nextFingerprints = new KnowledgeFingerprints
        {
            SemanticScopeHash = baselineScopeHash,
            AnchorSignatureHash = snippet.Fingerprints?.AnchorSignatureHash,
            ContextHash = contextHash
        };

        var nextOrigin = snippet.Origin ?? new KnowledgeOrigin
        {
            Branch = currentBranch,
            Head = currentHead
        };

        return snippet with
        {
            SchemaVersion = snippet.SchemaVersion ?? CurrentSchemaVersion,
            Validity = nextValidity,
            Fingerprints = nextFingerprints,
            Anchors = snippet.Anchors ?? new KnowledgeAnchors(),
            Origin = nextOrigin
        };
    }

    private static Dictionary<string, string> FlattenFileHashes(
        IReadOnlyDictionary<string, Dictionary<string, string>> filesByFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, files) in filesByFolder)
            foreach (var (fileName, hash) in files)
            {
                var path = folder == "." ? fileName : $"{folder}/{fileName}";
                map[NormalizePath(path)] = hash;
            }

        return map;
    }

    private static string ComputeSemanticScopeHash(
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyList<string> folders)
    {
        var entries = fileHashes
            .Where(kvp => folders.Any(f => IsInScope(kvp.Key, f)))
            .Select(kvp => $"{kvp.Key}:{kvp.Value}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        // include scoped folder set so the hash remains deterministic even when a scope has no files
        var folderHeader = string.Join('|', folders
            .Select(f => f.ToLowerInvariant())
            .OrderBy(x => x, StringComparer.Ordinal));
        var payload = $"folders:{folderHeader}\n{string.Join('\n', entries)}";
        return Hash16(payload);
    }

    private static bool IsInScope(string filePath, string folder)
    {
        if (folder == "." || folder == "/")
            return true;
        return filePath.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || filePath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeContextHash(string branch, string head, string? semanticScopeHash)
    {
        var payload = $"{branch}\n{head}\n{semanticScopeHash ?? string.Empty}";
        return Hash16(payload);
    }

    private static string Hash16(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalized = path.Replace('\\', '/').Trim();
        normalized = normalized.TrimEnd('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Length == 0 ? "." : normalized;
    }
}

