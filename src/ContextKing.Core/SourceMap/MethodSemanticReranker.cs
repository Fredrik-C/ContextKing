using ContextKing.Core.Embedding;

namespace ContextKing.Core.SourceMap;

public sealed record ScoredMethod(MethodCandidateCard Card, float Similarity);
public sealed record FileMethodScore(float Score, MethodCandidateCard BestMember);
public sealed record MethodRerankResult(
    IReadOnlyDictionary<string, FileMethodScore> Files,
    int EmbeddedCount, int FailureCount, bool Flat, bool Cancelled);

/// <summary>Embeds bounded, in-memory cards and aggregates successful scores only.</summary>
public sealed class MethodSemanticReranker(ITextEmbedder embedder)
{
    public MethodRerankResult Score(string task, IReadOnlyList<MethodCandidateCard> cards,
        int maxCardChars = 6000, int maxBodyChars = 3500, float flatThreshold = 0.03f,
        CancellationToken cancellationToken = default)
    {
        var scored = new List<ScoredMethod>();
        var failed = 0;
        if (cards.Count == 0 || cancellationToken.IsCancellationRequested)
            return Result();

        float[] query;
        try
        {
            query = embedder is IQueryTextEmbedder queryEmbedder ? queryEmbedder.EmbedQuery(task) : embedder.Embed(task);
            Validate(query);
        }
        catch (Exception) { failed = cards.Count; return Result(); }

        for (var start = 0; start < cards.Count && !cancellationToken.IsCancellationRequested; start += 16)
        {
            var batch = cards.Skip(start).Take(16).ToArray();
            var texts = batch.Select(c => c.ToEmbeddingText(maxCardChars, maxBodyChars)).ToArray();
            IReadOnlyList<float[]>? vectors = null;
            if (embedder is IBatchTextEmbedder batchEmbedder)
            {
                try
                {
                    vectors = batchEmbedder.EmbedBatch(texts);
                    if (vectors.Count != batch.Length) vectors = null;
                }
                catch (Exception) { /* Retry individually to isolate bad inputs. */ }
            }
            for (var i = 0; i < batch.Length && !cancellationToken.IsCancellationRequested; i++)
            {
                try
                {
                    var vector = vectors is null ? embedder.Embed(texts[i]) : vectors[i];
                    scored.Add(new(batch[i], Similarity(query, vector)));
                }
                catch (Exception) { failed++; }
            }
        }
        return Result();

        MethodRerankResult Result()
        {
            var files = scored.GroupBy(x => x.Card.FilePath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Aggregate(g.ToArray()), StringComparer.Ordinal);
            var flat = scored.Count > 0 && scored.Max(x => x.Similarity) - scored.Min(x => x.Similarity) < flatThreshold;
            return new(files, scored.Count, failed, flat, cancellationToken.IsCancellationRequested);
        }
    }

    public static FileMethodScore Aggregate(IReadOnlyList<ScoredMethod> methods)
    {
        if (methods.Count == 0) throw new ArgumentException("At least one successful method is required.", nameof(methods));
        var best = methods.OrderByDescending(m => m.Similarity).ThenBy(m => m.Card.StartLine).Take(3).ToArray();
        var total = 0.75f * best[0].Similarity;
        var weight = 0.75f;
        if (best.Length >= 2) { total += 0.20f * best[1].Similarity; weight += 0.20f; }
        total += 0.05f * best.Average(m => m.Similarity);
        weight += 0.05f;
        return new(total / weight, best[0].Card);
    }

    private static void Validate(float[] vector)
    {
        if (vector.Length == 0 || vector.Any(x => !float.IsFinite(x)) || vector.All(x => x == 0))
            throw new InvalidOperationException("Invalid embedding vector.");
    }

    private static float Similarity(float[] a, float[] b)
    {
        Validate(b);
        if (a.Length != b.Length) throw new InvalidOperationException("Embedding dimensions differ.");
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i];
        }
        return (float)Math.Clamp((dot / Math.Sqrt(aa * bb) + 1) / 2, 0, 1);
    }
}
