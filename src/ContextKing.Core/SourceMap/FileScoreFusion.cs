namespace ContextKing.Core.SourceMap;

public sealed record MethodFusionOptions(
    float LexicalWeight = 0.45f,
    float MetadataSemanticWeight = 0.15f,
    float MethodSemanticWeight = 0.35f,
    float StructuralBoostMax = 0.08f,
    float GenericPenaltyMax = 0.10f);

public sealed record FusedFileScore(FileSearchHit Hit, FileMethodScore? Method, float StructuralBonus);

/// <summary>Combines evidence without filtering candidates or penalizing missing extraction.</summary>
public static class FileScoreFusion
{
    public static IReadOnlyList<FusedFileScore> Fuse(
        IReadOnlyList<FileSearchHit> lexicalCandidates,
        IReadOnlyDictionary<string, float> metadataScores,
        MethodRerankResult methods,
        string lexicalQuery,
        int topK,
        MethodFusionOptions? options = null)
    {
        if (lexicalCandidates.Count == 0 || topK <= 0) return [];
        options ??= new();
        var min = lexicalCandidates.Min(h => h.LexicalScore);
        var max = lexicalCandidates.Max(h => h.LexicalScore);
        var terms = PathTokenizer.TokenizeQuery(lexicalQuery).Where(t => t.Length >= 3).Distinct().ToArray();
        var scored = new List<(FusedFileScore Score, int Order)>();
        for (var i = 0; i < lexicalCandidates.Count; i++)
        {
            var hit = lexicalCandidates[i];
            methods.Files.TryGetValue(hit.Path, out var method);
            var hasMetadata = metadataScores.TryGetValue(hit.Path, out var metadata) && float.IsFinite(metadata);
            var lexical = max - min < 1e-6f ? 1 : (hit.LexicalScore - min) / (max - min);
            // With metadata absent, redistribute its budget in the specified 0.55:0.40 ratio.
            var lexicalWeight = Weight(options.LexicalWeight);
            var methodWeight = Weight(options.MethodSemanticWeight);
            var metadataWeight = Weight(options.MetadataSemanticWeight);
            if (!hasMetadata)
            {
                var budget = lexicalWeight + methodWeight + metadataWeight;
                lexicalWeight = budget * 0.55f / 0.95f;
                methodWeight = budget * 0.40f / 0.95f;
                metadataWeight = 0;
            }
            if (methods.Flat) methodWeight *= 0.2f;
            if (method is null) methodWeight = 0;
            var denominator = lexicalWeight + metadataWeight + methodWeight;
            var bonus = method is null ? 0 : StructuralBonus(method, terms, options.StructuralBoostMax);
            var penalty = hit.Path.Split(['/', '\\']).Any(p => p.ToLowerInvariant() is "migration" or "migrations" or "legacy" or "temp" or "tmp")
                ? Math.Min(Weight(options.GenericPenaltyMax), 0.05f) : 0;
            var score = denominator <= 0 ? lexical :
                (lexicalWeight * lexical + metadataWeight * Math.Clamp(metadata, 0, 1) + methodWeight * (method?.Score ?? 0)) / denominator;
            scored.Add((new(hit with { Score = score + bonus - penalty, SemanticScore = hasMetadata ? metadata : null }, method, bonus), i));
        }
        return scored.OrderByDescending(x => x.Score.Hit.Score).ThenBy(x => x.Order).Take(topK).Select(x => x.Score).ToArray();
    }

    public static float StructuralBonus(FileMethodScore method, IReadOnlyList<string> terms, float cap)
    {
        if (terms.Count == 0 || method.Score < 0.25f) return 0;
        var card = method.BestMember;
        float Match(string text) => (float)terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) / terms.Count;
        return Math.Min(Math.Min(Weight(cap), 0.08f),
            0.04f * Match(card.MemberName + " " + card.ContainingType) +
            0.04f * Match(string.Join(' ', card.Evidence)) + 0.02f * Match(card.FilePath));
    }

    private static float Weight(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
