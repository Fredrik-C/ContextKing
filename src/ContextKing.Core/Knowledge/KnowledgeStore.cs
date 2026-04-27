using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextKing.Core.Knowledge;

/// <summary>
/// Reads and writes <c>.ck-knowledge/snippets.jsonl</c>.
/// All writes are append-only except Delete, which rewrites the file.
/// </summary>
public sealed class KnowledgeStore(string repoRoot)
{
    public static string SnippetsPath(string repoRoot) =>
        Path.Combine(repoRoot, ".ck-knowledge", "snippets.jsonl");

    public string FilePath => SnippetsPath(repoRoot);

    public bool Exists => File.Exists(FilePath);

    public IReadOnlyList<KnowledgeSnippet> ReadAll()
    {
        if (!File.Exists(FilePath)) return [];

        var snippets = new List<KnowledgeSnippet>();
        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var snippet = JsonSerializer.Deserialize<KnowledgeSnippet>(line, JsonOptions);
                if (snippet is not null) snippets.Add(snippet);
            }
            catch { /* malformed line — skip */ }
        }
        return snippets;
    }

    /// <summary>
    /// Returns snippets whose <c>folders</c> field overlaps with <paramref name="folderPath"/>,
    /// sorted newest-first. Matching is prefix-tolerant in both directions.
    /// </summary>
    public IReadOnlyList<KnowledgeSnippet> ReadByFolder(string folderPath)
    {
        var normalised = NormalisePath(folderPath);
        return ReadAll()
            .Where(s => s.Folders.Any(f => FolderMatches(NormalisePath(f), normalised)))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public void Append(KnowledgeSnippet snippet)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(snippet, JsonOptions) + "\n");
    }

    public bool Delete(string id)
    {
        if (!File.Exists(FilePath)) return false;
        var all    = ReadAll();
        var before = all.Count;
        var kept   = all.Where(s => s.Id != id).ToList();
        if (kept.Count == before) return false;
        ReplaceAll(kept);
        return true;
    }

    public void ReplaceAll(IReadOnlyList<KnowledgeSnippet> snippets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var lines = snippets.Select(s => JsonSerializer.Serialize(s, JsonOptions));
        File.WriteAllText(FilePath, string.Join("\n", lines) + (snippets.Count > 0 ? "\n" : ""));
    }

    private static bool FolderMatches(string snippetFolder, string queryFolder) =>
        string.Equals(snippetFolder, queryFolder, StringComparison.OrdinalIgnoreCase)
        || snippetFolder.StartsWith(queryFolder + "/", StringComparison.OrdinalIgnoreCase)
        || queryFolder.StartsWith(snippetFolder + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalisePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
