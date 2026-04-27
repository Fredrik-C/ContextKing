using System.Text.Json;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.KeywordAtlas;

internal static class SessionKeywordAtlasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void Save(string repoRoot, SessionKeywordAtlas atlas)
    {
        var path = GetPath(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(atlas, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static SessionKeywordAtlas? Load(string repoRoot)
    {
        var path = GetPath(repoRoot);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionKeywordAtlas>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static SessionKeywordAtlas? LoadForFolder(string folderPath)
    {
        try
        {
            var repoRoot = GitTracker.GetWorktreeRoot(Directory.Exists(folderPath) ? folderPath : null);
            return Load(repoRoot);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsDirectionShift(
        SessionKeywordAtlas atlas,
        string query,
        IReadOnlyList<string> mustTerms,
        TimeSpan maxAge)
    {
        if (DateTime.UtcNow - atlas.CreatedAtUtc > maxAge)
            return true;

        var queryTerms = LowRankDictionary.FilterHighRank(PathTokenizer.TokenizeQuery(query));
        if (queryTerms.Count == 0)
            return false;

        var atlasTerms = atlas.QueryTerms.ToHashSet(StringComparer.Ordinal);
        var incomingTerms = queryTerms.ToHashSet(StringComparer.Ordinal);
        var overlap = atlasTerms.Count == 0
            ? 0f
            : (float)atlasTerms.Intersect(incomingTerms, StringComparer.Ordinal).Count() /
              Math.Max(1, atlasTerms.Union(incomingTerms, StringComparer.Ordinal).Count());

        var mustA = atlas.MustTerms.ToHashSet(StringComparer.Ordinal);
        var mustB = mustTerms.ToHashSet(StringComparer.Ordinal);
        var mustChanged = !mustA.SetEquals(mustB);

        return mustChanged || overlap < 0.35f;
    }

    private static string GetPath(string repoRoot) =>
        Path.Combine(repoRoot, ".ck-index", "session-keyword-atlas.json");
}
