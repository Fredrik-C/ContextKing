using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core.Git;

namespace ContextKing.Core.Knowledge;

/// <summary>
/// Reads every knowledge JSONL file under <c>.ck-knowledge/</c>.
/// New snippets are appended to a session-specific file to avoid merge conflicts.
/// </summary>
public sealed class KnowledgeStore(string repoRoot)
{
    private const string LegacyFileName = "snippets.jsonl";
    private const string SessionDirectoryName = "sessions";
    private const string SessionIdEnvironmentVariable = "CK_SESSION_ID";
    private readonly string _sessionId = ResolveCurrentSessionId(repoRoot);

    public static string KnowledgeDirectory(string repoRoot) =>
        Path.Combine(repoRoot, ".ck-knowledge");

    public static string SnippetsPath(string repoRoot) =>
        Path.Combine(KnowledgeDirectory(repoRoot), LegacyFileName);

    public static string SessionSnippetsDirectory(string repoRoot) =>
        Path.Combine(KnowledgeDirectory(repoRoot), SessionDirectoryName);

    public static string SessionSnippetsPath(string repoRoot, string sessionId) =>
        Path.Combine(SessionSnippetsDirectory(repoRoot), $"{SanitizeSessionId(sessionId)}.jsonl");

    public string FilePath => CurrentSessionFilePath();

    public bool Exists => KnowledgeFiles().Count > 0;

    public IReadOnlyList<KnowledgeSnippet> ReadAll()
        => ReadAllWithPaths().Select(x => x.Snippet).ToArray();

    internal IReadOnlyList<KnowledgeSnippetRecord> ReadAllWithPaths()
    {
        var files = KnowledgeFiles();
        if (files.Count == 0) return [];

        var snippets = new List<KnowledgeSnippetRecord>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var snippet = JsonSerializer.Deserialize<KnowledgeSnippet>(line, JsonOptions);
                    if (snippet is not null) snippets.Add(new KnowledgeSnippetRecord(file, snippet));
                }
                catch { /* malformed line — skip */ }
            }
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
        var sessionSnippet = snippet.SessionId is not null
            ? snippet
            : snippet with { SessionId = CurrentSessionId() };
        File.AppendAllText(FilePath, JsonSerializer.Serialize(sessionSnippet, JsonOptions) + "\n");
    }

    public bool Delete(string id)
    {
        var records = ReadAllWithPaths();
        if (records.Count == 0) return false;

        var matchedFiles = records
            .Where(r => r.Snippet.Id == id)
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matchedFiles.Length == 0) return false;

        foreach (var file in matchedFiles)
        {
            var kept = records
                .Where(r => string.Equals(r.Path, file, StringComparison.OrdinalIgnoreCase))
                .Where(r => r.Snippet.Id != id)
                .Select(r => r.Snippet)
                .ToArray();
            WriteFile(file, kept);
        }

        return true;
    }

    public void ReplaceAll(IReadOnlyList<KnowledgeSnippet> snippets)
    {
        var existingPathById = ReadAllWithPaths()
            .GroupBy(r => r.Snippet.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);

        var filesToRewrite = existingPathById.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var snippet in snippets)
        {
            var path = existingPathById.TryGetValue(snippet.Id, out var existingPath)
                ? existingPath
                : CurrentSessionFilePath(snippet.SessionId);
            filesToRewrite.Add(path);
        }

        foreach (var file in filesToRewrite)
        {
            var fileSnippets = snippets
                .Where(s => existingPathById.TryGetValue(s.Id, out var existingPath)
                    ? string.Equals(existingPath, file, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(CurrentSessionFilePath(s.SessionId), file, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            WriteFile(file, fileSnippets);
        }
    }

    public IReadOnlyList<string> KnowledgeFiles()
    {
        var dir = KnowledgeDirectory(repoRoot);
        if (!Directory.Exists(dir)) return [];

        return Directory
            .EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            .Where(path => !IsInsideIndexDirectory(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string AggregateHashInput()
    {
        var files = KnowledgeFiles();
        if (files.Count == 0) return string.Empty;

        var parts = new List<string>(files.Count);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(KnowledgeDirectory(repoRoot), file)
                .Replace('\\', '/');
            parts.Add(relative + "\n" + File.ReadAllText(file));
        }

        return string.Join("\n---ck-knowledge-file---\n", parts);
    }

    private string CurrentSessionFilePath(string? sessionId = null) =>
        SessionSnippetsPath(repoRoot, sessionId ?? _sessionId);

    private string CurrentSessionId() => _sessionId;

    /// <summary>
    /// Resolves the session id used to name the per-session JSONL file. Resolution order:
    /// <list type="number">
    ///   <item>The <c>CK_SESSION_ID</c> environment variable, when the harness provides a true
    ///         per-session identifier.</item>
    ///   <item>The current git branch — a stable fallback so that every <c>ck learn</c> on the
    ///         same branch (across separate CLI invocations) appends to one file instead of
    ///         spawning a fresh file per entry.</item>
    ///   <item>The HEAD commit, when in detached-HEAD state.</item>
    ///   <item>A unique timestamp+GUID, only when there is no git context at all.</item>
    /// </list>
    /// </summary>
    private static string ResolveCurrentSessionId(string repoRoot)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(SessionIdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return SanitizeSessionId(fromEnvironment);

        var branch = GitTracker.GetCurrentBranch(repoRoot);
        if (!string.IsNullOrWhiteSpace(branch))
            return SanitizeSessionId($"branch-{branch}");

        var head = GitTracker.GetHead(repoRoot);
        if (!string.IsNullOrWhiteSpace(head) && head != "unknown")
            return SanitizeSessionId($"detached-{head}");

        return $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"[..34];
    }

    private static void WriteFile(string filePath, IReadOnlyList<KnowledgeSnippet> snippets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var lines = snippets.Select(s => JsonSerializer.Serialize(s, JsonOptions));
        File.WriteAllText(filePath, string.Join("\n", lines) + (snippets.Count > 0 ? "\n" : ""));
    }

    private static bool IsInsideIndexDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, ".ck-index", StringComparison.OrdinalIgnoreCase));

    private static bool FolderMatches(string snippetFolder, string queryFolder) =>
        string.Equals(snippetFolder, queryFolder, StringComparison.OrdinalIgnoreCase)
        || snippetFolder.StartsWith(queryFolder + "/", StringComparison.OrdinalIgnoreCase)
        || queryFolder.StartsWith(snippetFolder + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalisePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    private static string SanitizeSessionId(string sessionId)
    {
        var chars = sessionId
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray();
        var sanitized = new string(chars).Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized)
            ? Guid.NewGuid().ToString("N")
            : sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record KnowledgeSnippetRecord(string Path, KnowledgeSnippet Snippet);
