namespace ContextKing.Core.SourceMap;

/// <summary>
/// Computes high-value keyword suggestions from indexed folder tokens.
/// Responsibility: discriminative keyword ranking (IDF/lift/scope affinity/entropy)
/// without performing semantic search itself.
/// </summary>
public sealed class KeywordRankingEngine
{
    private readonly CorpusStatistics _corpus;

    public KeywordRankingEngine(string dbPath)
    {
        var folders = new SourceMapIndex(dbPath).LoadIndexedFolders();
        _corpus = CorpusStatistics.Build(folders);
    }

    public IReadOnlyList<RankedKeyword> RankRelatedTerms(
        IReadOnlyList<ScoredFolderDetails> scopedResults,
        string seed,
        IReadOnlyCollection<string> excludedTerms,
        int maxTerms)
    {
        if (scopedResults.Count == 0 || string.IsNullOrWhiteSpace(seed) || maxTerms <= 0)
            return [];

        var rankedScope = scopedResults
            .Take(Math.Min(12, scopedResults.Count))
            .ToArray();

        var seedScoped = rankedScope
            .Where(r => r.MatchedTerms.Contains(seed, StringComparer.Ordinal))
            .ToArray();
        var workingScope = seedScoped.Length > 0 ? seedScoped : rankedScope;

        var lowestScore = workingScope[^1].Score;
        var localDocCount = workingScope.Length;
        var statsByCanonical = new Dictionary<string, CandidateStats>(StringComparer.Ordinal);

        foreach (var (result, index) in workingScope.Select((r, i) => (r, i)))
        {
            var tokensInFolder = new HashSet<string>(StringComparer.Ordinal);
            var folderWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);
            var contribution = folderWeight * rankWeight;

            foreach (var token in EnumerateFolderTokens(result))
            {
                if (!IsUsefulToken(token, excludedTerms))
                    continue;

                var canonical = Canonicalize(token);
                if (canonical.Length == 0 || !tokensInFolder.Add(canonical))
                    continue;

                if (!statsByCanonical.TryGetValue(canonical, out var stats))
                    statsByCanonical[canonical] = stats = new CandidateStats(token);

                stats.LocalDocumentCount++;
                stats.ScopeWeight += contribution;
                if (result.Score > stats.BestScopedScore)
                    stats.BestScopedScore = result.Score;

                // Prefer longer and less generic representative form.
                if (ShouldReplaceRepresentative(stats.Representative, token))
                    stats.Representative = token;
            }
        }

        var scored = statsByCanonical
            .Select(kvp =>
            {
                var canonical = kvp.Key;
                var stats = kvp.Value;
                var score = ScoreCandidate(canonical, stats, localDocCount);
                return new RankedKeyword(stats.Representative, score);
            })
            .Where(k => k.Score > 0f)
            .OrderByDescending(k => k.Score)
            .ThenByDescending(k => k.Term.Length)
            .ThenBy(k => StableHash(k.Term))
            .ToArray();

        if (scored.Length == 0)
            return [];

        var take = AdaptiveTake(scored, maxTerms);
        return scored.Take(take).ToArray();
    }

    public IReadOnlyList<RankedKeyword> RankScopedHints(
        IReadOnlyList<ScoredFolderDetails> scopedResults,
        IReadOnlyCollection<string> excludedTerms,
        int maxHints)
    {
        if (scopedResults.Count == 0 || maxHints <= 0)
            return [];

        var rankedScope = scopedResults
            .Take(Math.Min(12, scopedResults.Count))
            .ToArray();

        var lowestScore = rankedScope[^1].Score;
        var localDocCount = rankedScope.Length;
        var statsByCanonical = new Dictionary<string, CandidateStats>(StringComparer.Ordinal);

        foreach (var (result, index) in rankedScope.Select((r, i) => (r, i)))
        {
            var tokensInFolder = new HashSet<string>(StringComparer.Ordinal);
            var folderWeight = Math.Max(0.10f, result.Score - lowestScore + 0.20f);
            var rankWeight = 1f / (index + 1);
            var contribution = folderWeight * rankWeight;

            foreach (var token in EnumerateFolderTokens(result))
            {
                if (!IsUsefulToken(token, excludedTerms))
                    continue;

                var canonical = Canonicalize(token);
                if (canonical.Length == 0 || !tokensInFolder.Add(canonical))
                    continue;

                if (!statsByCanonical.TryGetValue(canonical, out var stats))
                    statsByCanonical[canonical] = stats = new CandidateStats(token);

                stats.LocalDocumentCount++;
                stats.ScopeWeight += contribution;
                if (result.Score > stats.BestScopedScore)
                    stats.BestScopedScore = result.Score;
                if (ShouldReplaceRepresentative(stats.Representative, token))
                    stats.Representative = token;
            }
        }

        return statsByCanonical
            .Select(kvp =>
            {
                var score = ScoreCandidate(kvp.Key, kvp.Value, localDocCount);
                return new RankedKeyword(kvp.Value.Representative, score);
            })
            .Where(k => k.Score > 0f)
            .OrderByDescending(k => k.Score)
            .ThenByDescending(k => k.Term.Length)
            .ThenBy(k => StableHash(k.Term))
            .Take(maxHints)
            .ToArray();
    }

    private float ScoreCandidate(string canonical, CandidateStats stats, int localDocCount)
    {
        if (localDocCount <= 0 || !_corpus.TryGet(canonical, out var global))
            return 0f;

        var localCoverage = (float)stats.LocalDocumentCount / localDocCount;
        var globalRatio = global.GlobalRatio;

        // Prefer terms that are dense in current scope but sparse globally.
        var lift = Math.Max(0f, localCoverage - globalRatio);

        var idf = MathF.Log(1f + (float)_corpus.TotalDocuments / (1f + global.DocumentFrequency));
        var maxIdf = MathF.Log(1f + _corpus.TotalDocuments);
        var idfNorm = maxIdf <= 0f ? 0f : idf / maxIdf;

        var scopeAffinity = MathF.Min(1f, stats.ScopeWeight / (localDocCount * 0.75f));
        var concentration = global.AreaConcentration;
        var shape = ShapeQuality(canonical);

        var freqPenalty = globalRatio > 0.18f
            ? (globalRatio - 0.18f) * 2.8f
            : 0f;
        var suffixPenalty = GenericSuffixPenalty(canonical);

        return
            2.20f * idfNorm +
            1.60f * lift +
            1.15f * scopeAffinity +
            0.70f * localCoverage +
            0.55f * concentration +
            shape -
            freqPenalty -
            suffixPenalty;
    }

    private static int AdaptiveTake(IReadOnlyList<RankedKeyword> ranked, int requested)
    {
        var maxTake = Math.Min(requested, ranked.Count);
        if (maxTake <= 8)
            return maxTake;

        var cutoff = Math.Max(0.92f, ranked[0].Score * 0.42f);
        var highQuality = ranked.TakeWhile(r => r.Score >= cutoff).Count();
        if (highQuality == 0)
            return Math.Min(8, maxTake);

        return Math.Min(maxTake, Math.Max(8, highQuality));
    }

    private static IEnumerable<string> EnumerateFolderTokens(ScoredFolderDetails result)
    {
        if (!string.IsNullOrWhiteSpace(result.CombinedTokens))
        {
            foreach (var token in result.CombinedTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return token;
            yield break;
        }

        foreach (var token in result.UnmatchedFolderTerms)
            yield return token;
    }

    private static bool IsUsefulToken(string token, IReadOnlyCollection<string> excludedTerms)
    {
        if (token.Length < 3 || token.Length > 48)
            return false;
        if (!token.Any(char.IsLetter))
            return false;
        if (excludedTerms.Contains(token) || LowRankDictionary.Contains(token))
            return false;

        return true;
    }

    private static bool ShouldReplaceRepresentative(string current, string candidate)
    {
        if (candidate.Length > current.Length + 2)
            return true;

        // Prefer alphanumeric terms (e.g. 3ds2) and underscore compounds.
        var currentMixed = current.Any(char.IsDigit) && current.Any(char.IsLetter);
        var candidateMixed = candidate.Any(char.IsDigit) && candidate.Any(char.IsLetter);
        if (!currentMixed && candidateMixed)
            return true;

        if (!current.Contains('_') && candidate.Contains('_'))
            return true;

        return false;
    }

    private static string Canonicalize(string term)
    {
        var t = term.ToLowerInvariant();
        if (t.Length <= 3)
            return t;

        if (t.EndsWith("ies", StringComparison.Ordinal) && t.Length > 4)
            return t[..^3] + "y";
        if (t.EndsWith("ing", StringComparison.Ordinal) && t.Length > 6)
            return t[..^3];
        if (t.EndsWith("ed", StringComparison.Ordinal) && t.Length > 5)
            return t[..^2];
        if (t.EndsWith("es", StringComparison.Ordinal) && t.Length > 5)
            return t[..^2];
        if (t.EndsWith('s') && t.Length > 4)
            return t[..^1];

        return t;
    }

    private static float ShapeQuality(string term)
    {
        var score = 0f;
        if (term.Length is >= 8 and <= 28)
            score += 0.26f;
        if (term.Any(char.IsDigit) && term.Any(char.IsLetter))
            score += 0.22f;
        if (term.Contains('_'))
            score += 0.14f;
        if (term.Length <= 4)
            score -= 0.25f;

        return score;
    }

    private static float GenericSuffixPenalty(string term)
    {
        foreach (var suffix in GenericSuffixes)
        {
            if (term.EndsWith(suffix, StringComparison.Ordinal))
                return 0.18f;
        }

        return 0f;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
                hash = hash * 31 + c;
            return hash;
        }
    }

    private sealed class CandidateStats(string representative)
    {
        public string Representative { get; set; } = representative;
        public int LocalDocumentCount { get; set; }
        public float ScopeWeight { get; set; }
        public float BestScopedScore { get; set; }
    }

    private sealed class CorpusStatistics
    {
        private readonly Dictionary<string, int> _documentFrequency;
        private readonly Dictionary<string, float> _areaConcentration;

        public CorpusStatistics(
            int totalDocuments,
            Dictionary<string, int> documentFrequency,
            Dictionary<string, float> areaConcentration)
        {
            TotalDocuments = totalDocuments;
            _documentFrequency = documentFrequency;
            _areaConcentration = areaConcentration;
        }

        public int TotalDocuments { get; }

        public bool TryGet(string term, out CorpusTermStats stats)
        {
            if (!_documentFrequency.TryGetValue(term, out var df))
            {
                stats = default;
                return false;
            }

            _areaConcentration.TryGetValue(term, out var concentration);
            stats = new CorpusTermStats(df, concentration, (float)df / Math.Max(1, TotalDocuments));
            return true;
        }

        public static CorpusStatistics Build(IReadOnlyList<IndexedFolder> folders)
        {
            var totalDocs = Math.Max(1, folders.Count);
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            var termAreaCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (var folder in folders)
            {
                var area = AreaKey(folder.Path);
                var unique = folder.CombinedTokens
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var token in unique)
                {
                    if (token.Length < 3 || !token.Any(char.IsLetter) || LowRankDictionary.Contains(token))
                        continue;

                    df[token] = df.GetValueOrDefault(token) + 1;

                    if (!termAreaCounts.TryGetValue(token, out var areaCounts))
                        termAreaCounts[token] = areaCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                    areaCounts[area] = areaCounts.GetValueOrDefault(area) + 1;
                }
            }

            var concentration = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (term, areaCounts) in termAreaCounts)
                concentration[term] = AreaConcentration(areaCounts);

            return new CorpusStatistics(totalDocs, df, concentration);
        }

        private static float AreaConcentration(Dictionary<string, int> areaCounts)
        {
            if (areaCounts.Count <= 1)
                return 1f;

            var total = areaCounts.Values.Sum();
            if (total <= 0)
                return 0f;

            var entropy = 0f;
            foreach (var count in areaCounts.Values)
            {
                var p = (float)count / total;
                entropy -= p * MathF.Log(p);
            }

            var maxEntropy = MathF.Log(areaCounts.Count);
            if (maxEntropy <= 0f)
                return 0f;

            var normalizedEntropy = entropy / maxEntropy;
            return Math.Clamp(1f - normalizedEntropy, 0f, 1f);
        }

        private static string AreaKey(string path)
        {
            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 3 ? path : string.Join('/', parts.Take(3));
        }
    }

    public readonly record struct RankedKeyword(string Term, float Score);

    private readonly record struct CorpusTermStats(int DocumentFrequency, float AreaConcentration, float GlobalRatio);

    private static readonly string[] GenericSuffixes =
    [
        "request",
        "response",
        "handler",
        "command",
        "service",
        "component",
        "manager",
        "processor",
        "provider",
        "gateway",
        "entity",
        "record",
        "value",
        "model",
        "status",
        "result"
    ];
}
