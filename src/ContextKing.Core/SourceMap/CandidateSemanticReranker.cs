using ContextKing.Core.Embedding;

namespace ContextKing.Core.SourceMap;

public sealed record SemanticRerankOptions(
    float LexicalWeight = 0.65f,
    float SemanticWeight = 0.30f,
    float MustWeight = 0.10f,
    float GenericPenaltyMax = 0.10f,
    int MaxCandidateTextChars = 4000,
    float FlatSemanticThreshold = 0.03f);

public sealed record SearchCandidateCard(
    string Path,
    float LexicalScore,
    string FolderPath,
    string FileName,
    string TypeNames,
    string MethodNames,
    int TypeCount,
    int SignatureCount,
    IReadOnlyList<string> MatchedTerms)
{
    public static SearchCandidateCard FromHit(FileSearchHit hit) =>
        new(
            hit.Path,
            hit.LexicalScore,
            hit.FolderPath,
            hit.FileName,
            hit.TypeNames,
            hit.MethodNames,
            hit.TypeCount,
            hit.SignatureCount,
            hit.MatchedTerms);

    public string ToEmbeddingText(int maxChars = 4000)
    {
        var text = $"""
            Path: {Path}
            Folder: {FolderPath}
            File: {FileName}
            Types: {TypeNames}
            Members: {MethodNames}
            """;

        return text.Length <= maxChars ? text : text[..maxChars];
    }
}

public sealed class CandidateSemanticReranker(ITextEmbedder embedder)
{
    public IReadOnlyList<FileSearchHit> Rerank(
        string lexicalQuery,
        string? taskDescription,
        IReadOnlyList<FileSearchHit> lexicalCandidates,
        int topK,
        SemanticRerankOptions options,
        IReadOnlyList<string>? mustTerms = null)
    {
        if (lexicalCandidates.Count == 0 || topK <= 0)
            return [];

        var semanticQuery = string.IsNullOrWhiteSpace(taskDescription)
            ? lexicalQuery
            : taskDescription;

        var queryVector = embedder.Embed(semanticQuery);
        var scored = new List<(FileSearchHit Hit, float Semantic, int OriginalIndex)>(lexicalCandidates.Count);
        var failed = new List<(FileSearchHit Hit, int OriginalIndex)>();

        for (var i = 0; i < lexicalCandidates.Count; i++)
        {
            var hit = lexicalCandidates[i];
            try
            {
                var card = SearchCandidateCard.FromHit(hit);
                var semantic = CosineSimilarity(
                    queryVector,
                    embedder.Embed(card.ToEmbeddingText(options.MaxCandidateTextChars)));
                scored.Add((hit, semantic, i));
            }
            catch
            {
                failed.Add((hit, i));
            }
        }

        if (scored.Count == 0)
            return failed.Select(x => x.Hit).Take(topK).ToArray();

        var minLexical = scored.Min(x => x.Hit.LexicalScore);
        var maxLexical = scored.Max(x => x.Hit.LexicalScore);
        var minSemantic = scored.Min(x => x.Semantic);
        var maxSemantic = scored.Max(x => x.Semantic);
        var flatSemantic = maxSemantic - minSemantic < options.FlatSemanticThreshold;
        var lexicalWeight = flatSemantic ? 0.85f : options.LexicalWeight;
        var semanticWeight = flatSemantic ? 0.10f : options.SemanticWeight;
        var mustSet = NormalizeMustTerms(mustTerms);

        var reranked = scored
            .Select(item =>
            {
                var lexical = Normalize(item.Hit.LexicalScore, minLexical, maxLexical);
                var semantic = Math.Clamp((item.Semantic + 1.0f) / 2.0f, 0.0f, 1.0f);
                var semanticContribution = semanticWeight * semantic;
                if (item.Hit.MatchedTerms.Count == 0)
                    semanticContribution = Math.Min(semanticContribution, 0.10f);

                var mustBonus = MustBonus(item.Hit, mustSet, options.MustWeight);
                var penalty = GenericPenalty(item.Hit, options.GenericPenaltyMax);
                var finalScore = lexicalWeight * lexical + semanticContribution + mustBonus - penalty;

                var rerankedHit = item.Hit with
                {
                    Score = finalScore,
                    SemanticScore = semantic
                };
                return (Hit: rerankedHit, item.OriginalIndex);
            })
            .OrderByDescending(x => x.Hit.Score)
            .ThenBy(x => x.OriginalIndex)
            .Take(topK)
            .Select(x => x.Hit)
            .ToList();

        if (failed.Count > 0 && reranked.Count < topK)
            reranked.AddRange(failed.Select(x => x.Hit).Take(topK - reranked.Count));

        return reranked.ToArray();
    }

    private static float Normalize(float value, float min, float max) =>
        Math.Abs(max - min) < 1e-6f ? 1.0f : (value - min) / (max - min);

    private static HashSet<string> NormalizeMustTerms(IReadOnlyList<string>? mustTerms)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mustTerms is null)
            return result;

        foreach (var text in mustTerms)
        {
            foreach (var term in PathTokenizer.TokenizeQuery(text))
                if (term.Length >= 3)
                    result.Add(term);
        }

        return result;
    }

    private static float MustBonus(FileSearchHit hit, HashSet<string> mustTerms, float mustWeight)
    {
        if (mustTerms.Count == 0)
            return 0f;

        var haystack = string.Join(' ', hit.Path, hit.TypeNames, hit.MethodNames);
        var matched = mustTerms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matched == 0 ? 0f : mustWeight * ((float)matched / mustTerms.Count);
    }

    private static float GenericPenalty(FileSearchHit hit, float maxPenalty)
    {
        var penalty = 0f;
        foreach (var segment in hit.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = segment.ToLowerInvariant();
            if (s is "migration" or "migrations" or "legacy" or "temp" or "tmp")
            {
                penalty += 0.05f;
                break;
            }
        }

        if (hit.SignatureCount > 250)
            penalty += 0.10f;
        else if (hit.SignatureCount > 100)
            penalty += 0.05f;

        return Math.Min(maxPenalty, penalty);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0f;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator <= 1e-9f ? 0f : dot / denominator;
    }
}
