using System.Security.Cryptography;
using System.Text.Json;
using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;

namespace ContextKing.Core.Knowledge;

/// <summary>
/// Embeds knowledge snippets and stores them in the existing SQLite index.
/// Rebuilds the knowledge table only when <c>snippets.jsonl</c> has changed
/// (detected via SHA-256 of the full file).
/// </summary>
public sealed class KnowledgeIndexBuilder(BgeEmbedder embedder)
{
    private const string MetaKey = "knowledge_snippets_hash";

    public bool IsUpToDate(string dbPath, string repoRoot)
    {
        var path = KnowledgeStore.SnippetsPath(repoRoot);
        if (!File.Exists(path)) return true;
        if (!File.Exists(dbPath)) return false;

        try
        {
            var stored  = new SourceMapIndex(dbPath).ReadMeta(MetaKey);
            var current = HashFile(path);
            return string.Equals(stored, current, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Builds the knowledge index only if stale or missing.</summary>
    public void BuildIfNeeded(string dbPath, string repoRoot)
    {
        if (!IsUpToDate(dbPath, repoRoot))
            Build(dbPath, repoRoot);
    }

    public void Build(string dbPath, string repoRoot)
    {
        var snippetsPath = KnowledgeStore.SnippetsPath(repoRoot);
        if (!File.Exists(snippetsPath)) return;

        var snippets = new KnowledgeStore(repoRoot).ReadAll();
        var index    = new SourceMapIndex(dbPath);
        index.EnsureKnowledgeSchema();

        var rows = snippets
            .Select(s => new KnowledgeRow(
                s.Id,
                s.Content,
                SourceMapIndex.EncodeEmbedding(embedder.Embed(s.Content)),
                s.Tags.Count    > 0 ? JsonSerializer.Serialize(s.Tags)    : null,
                s.Folders.Count > 0 ? JsonSerializer.Serialize(s.Folders) : null,
                s.Source,
                s.CreatedAt))
            .ToList();

        index.ReplaceAllKnowledge(rows);
        index.WriteMeta(MetaKey, HashFile(snippetsPath));
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }
}
