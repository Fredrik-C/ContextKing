using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;

namespace ContextKing.Core.Knowledge;

/// <summary>
/// Embeds knowledge snippets and stores them in the existing SQLite index.
/// Rebuilds the knowledge table only when the aggregate knowledge JSONL content changes.
/// </summary>
public sealed class KnowledgeIndexBuilder(BgeEmbedder embedder)
{
    private const string MetaKey = "knowledge_snippets_hash";

    public bool IsUpToDate(string dbPath, string repoRoot)
    {
        if (!File.Exists(dbPath)) return false;

        try
        {
            var stored  = new SourceMapIndex(dbPath).ReadMeta(MetaKey);
            var store = new KnowledgeStore(repoRoot);
            if (store.KnowledgeFiles().Count == 0 && stored is null)
                return true;

            var current = HashKnowledge(repoRoot);
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
        index.WriteMeta(MetaKey, HashKnowledge(repoRoot));
    }

    private static string HashKnowledge(string repoRoot)
    {
        var input = new KnowledgeStore(repoRoot).AggregateHashInput();
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }
}
