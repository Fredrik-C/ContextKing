namespace ContextKing.Core.SourceMap;

public sealed record FileSearchHit(
    string Path,
    float Score,
    float LexicalScore,
    float? SemanticScore,
    int TypeCount,
    int SignatureCount,
    string FolderPath,
    string FileName,
    string TypeNames,
    string EmbeddingText,
    string MethodNames,
    IReadOnlyList<string> MatchedTerms);

public readonly record struct ScoredFile(
    string Path,
    float Score,
    int TypeCount,
    int SignatureCount,
    string EmbeddingText,
    string MethodNames,
    string TypeNames = "")
{
    public static ScoredFile FromHit(FileSearchHit hit) =>
        new(hit.Path, hit.Score, hit.TypeCount, hit.SignatureCount, hit.EmbeddingText, hit.MethodNames, hit.TypeNames);
}

public sealed class FileMapSearcher
{
    public IReadOnlyList<ScoredFile> Search(
        string dbPath,
        string query,
        int topK = 20,
        float minScore = 0f,
        IReadOnlyList<string>? allowedFolders = null,
        IReadOnlyList<string>? mustTerms = null)
        => SearchHits(dbPath, query, topK, minScore, allowedFolders, mustTerms)
            .Select(ScoredFile.FromHit)
            .ToArray();

    public IReadOnlyList<FileSearchHit> SearchHits(
        string dbPath,
        string query,
        int topK = 20,
        float minScore = 0f,
        IReadOnlyList<string>? allowedFolders = null,
        IReadOnlyList<string>? mustTerms = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var index = new SourceMapIndex(dbPath);
        if (!index.Exists)
            return [];

        var files = index.LoadIndexedFiles();
        if (files.Count == 0)
            return [];

        HashSet<string>? allowed = null;
        if (allowedFolders is { Count: > 0 })
            allowed = allowedFolders
                .Select(NormalizePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var queryTerms = PathTokenizer.TokenizeQuery(query)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (queryTerms.Length == 0)
            return [];
        var docFreq = BuildDocumentFrequency(files);
        var totalDocs = Math.Max(1, files.Count);
        var scored = new List<FileSearchHit>(Math.Min(topK, files.Count));

        foreach (var file in files)
        {
            if (allowed is not null && !IsAllowed(file.Path, file.FolderPath, allowed))
                continue;

            var (score, matchedTerms) = LexicalScore(file, queryTerms, docFreq, totalDocs, mustTerms);
            if (score < minScore)
                continue;

            scored.Add(new FileSearchHit(
                file.Path,
                score,
                score,
                null,
                file.TypeCount,
                file.SignatureCount,
                file.FolderPath,
                Path.GetFileName(file.Path),
                file.TypeNames,
                file.EmbeddingText,
                file.MethodNames,
                matchedTerms));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
    }

    private static bool IsAllowed(string path, string folderPath, HashSet<string> allowed)
    {
        var normPath = NormalizePath(path);
        var normFolder = NormalizePath(folderPath);
        foreach (var root in allowed)
        {
            if (normPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                || normFolder.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normFolder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizePath(string p) => p.Replace('\\', '/').TrimStart('.').TrimStart('/').TrimEnd('/');

    private static Dictionary<string, int> BuildDocumentFrequency(IReadOnlyList<IndexedFile> files)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var terms = BuildTermSet(f);
            foreach (var term in terms)
                df[term] = df.TryGetValue(term, out var c) ? c + 1 : 1;
        }
        return df;
    }

    private static HashSet<string> BuildTermSet(IndexedFile file)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in PathTokenizer.TokenizePath(file.FolderPath).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t.Length >= 3) terms.Add(t);
        foreach (var t in PathTokenizer.TokenizeFileName(Path.GetFileName(file.Path)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t.Length >= 3) terms.Add(t);
        foreach (var t in SplitLex(file.TypeNames))
            if (t.Length >= 3) terms.Add(t);
        foreach (var t in SplitLex(file.MethodNames))
            if (t.Length >= 3) terms.Add(t);
        return terms;
    }

    private static IEnumerable<string> SplitLex(string text)
    {
        var tokens = text.Split([';', ',', '.', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            foreach (var part in PathTokenizer.MethodNameToPhrase(token).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return part;
        }
    }

    private static (float Score, IReadOnlyList<string> MatchedTerms) LexicalScore(
        IndexedFile file,
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, int> docFreq,
        int totalDocs,
        IReadOnlyList<string>? mustTerms)
    {
        var pathText = file.FolderPath.ToLowerInvariant();
        var fileText = Path.GetFileName(file.Path).ToLowerInvariant();
        var typeText = file.TypeNames.ToLowerInvariant();
        var methodText = file.MethodNames.ToLowerInvariant();

        float score = 0f;
        var matchedTerms = new List<string>(queryTerms.Count);
        foreach (var term in queryTerms)
        {
            var idf = docFreq.TryGetValue(term, out var df)
                ? MathF.Log(1f + (float)totalDocs / (1f + df))
                : MathF.Log(1f + totalDocs);
            var termHit = 0f;
            if (methodText.Contains(term, StringComparison.Ordinal)) termHit += 3.5f;
            if (typeText.Contains(term, StringComparison.Ordinal)) termHit += 2.5f;
            if (fileText.Contains(term, StringComparison.Ordinal)) termHit += 2.0f;
            if (pathText.Contains(term, StringComparison.Ordinal)) termHit += 1.2f;
            if (termHit > 0f) matchedTerms.Add(term);
            score += termHit * idf;
        }

        if (matchedTerms.Count > 0)
            score += 1.5f * ((float)matchedTerms.Count / queryTerms.Count);

        var normalizedMustTerms = mustTerms is { Count: > 0 }
            ? mustTerms
                .SelectMany(PathTokenizer.TokenizeQuery)
                .Where(term => term.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

        if (normalizedMustTerms.Length > 0)
        {
            var mustMatches = 0;
            foreach (var must in normalizedMustTerms)
            {
                if (pathText.Contains(must, StringComparison.Ordinal)
                    || fileText.Contains(must, StringComparison.Ordinal)
                    || typeText.Contains(must, StringComparison.Ordinal)
                    || methodText.Contains(must, StringComparison.Ordinal))
                {
                    mustMatches++;
                }
            }

            if (mustMatches > 0)
                score += 6.0f * ((float)mustMatches / normalizedMustTerms.Length);
        }

        return (score, matchedTerms);
    }
}
