using System.Text.Json;
using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;

namespace ContextKing.Core.Knowledge;

public sealed record ScoredSnippet(string Id, string Content, string? Tags, string? Folders, float Score);

/// <summary>
/// Cross-folder semantic search over the knowledge index.
/// Folder-scoped recall (no embedding) lives in <see cref="KnowledgeStore"/> directly.
/// </summary>
public sealed class KnowledgeSearcher(BgeEmbedder embedder)
{
    private const float ExactMatchBonus = 0.30f;

    /// <summary>
    /// Embeds <paramref name="query"/>, scores all indexed snippets, returns the top K.
    /// </summary>
    public IReadOnlyList<ScoredSnippet> SearchByQuery(string dbPath, string query, int topK = 10)
    {
        var rows = new SourceMapIndex(dbPath).LoadKnowledgeRows();
        if (rows.Count == 0) return [];

        var queryVec   = embedder.Embed(query);
        var queryTerms = query
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var scored = new List<ScoredSnippet>(rows.Count);
        foreach (var row in rows)
        {
            var semantic = CosineSimilarity(queryVec, row.Embedding);
            var exact    = TagMatchFraction(queryTerms, row.Tags, row.Folders);
            var score    = semantic + ExactMatchBonus * exact;
            scored.Add(new ScoredSnippet(row.Id, row.Content, row.Tags, row.Folders, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Count <= topK ? scored : scored[..topK];
    }

    private static float TagMatchFraction(string[] queryTerms, string? tagsJson, string? foldersJson)
    {
        if (queryTerms.Length == 0) return 0f;

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendFromJson(tagsJson, targets);
        AppendFromJson(foldersJson, targets);

        int matched = queryTerms.Count(t =>
            targets.Contains(t) ||
            targets.Any(tgt => tgt.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return (float)matched / queryTerms.Length;
    }

    private static void AppendFromJson(string? json, HashSet<string> targets)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var val = el.GetString();
                if (val is not null) targets.Add(val);
            }
        }
        catch { /* malformed JSON */ }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}
