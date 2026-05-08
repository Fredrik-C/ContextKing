namespace ContextKing.Core.SourceMap;

public enum KeywordRole
{
    Unknown = 0,
    Anchor = 1,
    Discriminator = 2,
    Workflow = 3,
    Noise = 4
}

public sealed record KeywordRoleAssignment(
    string Term,
    KeywordRole Role,
    int GlobalDocumentFrequency,
    float DocumentFrequencyPercentile,
    float RarityScore,
    float LocalLiftScore,
    float ScopeConcentrationScore,
    float BroadRiskScore,
    bool IsMatchedQueryTerm,
    bool IsMustTerm);

public sealed record QueryCompositionAdvice(
    IReadOnlyList<KeywordRoleAssignment> Terms,
    IReadOnlyList<string> SuggestedQueries,
    string? SuggestedMust,
    string SuggestedNextCommand);

public static class KeywordIntentAdvisor
{
    public static QueryCompositionAdvice BuildAdvice(
        string dbPath,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> matchedTerms,
        IReadOnlyList<string> mustTerms,
        IReadOnlyList<string> globalHints,
        int maxQueries = 3)
    {
        var folders = new SourceMapIndex(dbPath).LoadIndexedFolders();
        var corpus = CorpusTokenStatistics.Build(folders);
        var matchedSet = matchedTerms.ToHashSet(StringComparer.Ordinal);
        var mustSet = mustTerms.ToHashSet(StringComparer.Ordinal);

        var candidates = queryTerms
            .Concat(globalHints.Take(24))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var evidence = BuildEvidence(candidates, globalHints, corpus, queryTerms, matchedSet, mustSet);
        var anchorTerms = SelectAnchors(evidence, mustSet);
        var assignments = evidence
            .Select(e => ToAssignment(e, anchorTerms.Contains(e.Term, StringComparer.Ordinal)))
            .OrderByDescending(a => a.IsMustTerm)
            .ThenBy(a => a.Role)
            .ThenByDescending(a => a.RarityScore)
            .ThenByDescending(a => a.IsMatchedQueryTerm)
            .ThenBy(a => a.Term)
            .ToArray();

        var anchor = assignments.Where(a => a.Role == KeywordRole.Anchor).Select(a => a.Term).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        var discriminator = assignments.Where(a => a.Role == KeywordRole.Discriminator).Select(a => a.Term).Distinct(StringComparer.Ordinal).Take(4).ToArray();
        var workflow = assignments.Where(a => a.Role == KeywordRole.Workflow).Select(a => a.Term).Distinct(StringComparer.Ordinal).Take(4).ToArray();

        // Backfill role buckets to keep composition robust when one bucket is sparse.
        if (anchor.Length == 0)
            anchor = assignments.Where(a => a.IsMatchedQueryTerm && a.Role != KeywordRole.Noise).Select(a => a.Term).Take(1).ToArray();
        if (discriminator.Length == 0)
            discriminator = assignments.Where(a => a.IsMatchedQueryTerm && a.Role != KeywordRole.Noise).OrderByDescending(a => a.RarityScore).Select(a => a.Term).Take(2).ToArray();
        if (workflow.Length == 0)
            workflow = assignments.Where(a => a.IsMatchedQueryTerm && a.Role != KeywordRole.Noise).Select(a => a.Term).Take(1).ToArray();

        var suggestions = new List<string>(maxQueries);
        var primaryTokens = ComposeQuery(anchor, discriminator, workflow, 1, 1);
        if (primaryTokens.Length > 0)
            suggestions.Add(string.Join(' ', primaryTokens));

        var secondaryTokens = ComposeQuery(anchor, discriminator, workflow, 2, 2);
        if (secondaryTokens.Length > 0)
            suggestions.Add(string.Join(' ', secondaryTokens));

        var tertiaryTokens = ComposeQuery(anchor, discriminator.Skip(1).ToArray(), workflow, 2, 2);
        if (tertiaryTokens.Length > 0)
            suggestions.Add(string.Join(' ', tertiaryTokens));

        var finalQueries = suggestions
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(1, maxQueries))
            .ToArray();

        var suggestedMust = mustTerms.FirstOrDefault() ?? SelectSuggestedMust(evidence, anchorTerms);

        var next = BuildNextCommand(finalQueries.FirstOrDefault(), suggestedMust);

        return new QueryCompositionAdvice(assignments, finalQueries, suggestedMust, next);
    }

    private static IReadOnlyList<TermEvidence> BuildEvidence(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> globalHints,
        CorpusTokenStatistics corpus,
        IReadOnlyList<string> queryTerms,
        IReadOnlySet<string> matchedSet,
        IReadOnlySet<string> mustSet)
    {
        var hintRank = globalHints
            .Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i + 1, StringComparer.Ordinal);
        var querySet = queryTerms.ToHashSet(StringComparer.Ordinal);

        var terms = candidates
            .Select(term =>
            {
                var df = corpus.GetDocumentFrequency(term);
                var isMatched = matchedSet.Contains(term);
                var isMust = mustSet.Contains(term);
                var isQuery = querySet.Contains(term);
                var rank = hintRank.TryGetValue(term, out var r) ? r : int.MaxValue;
                return new TermEvidence(term, df, isQuery, isMatched, isMust, rank);
            })
            .ToArray();

        var dfs = terms
            .Where(t => !LowRankDictionary.Contains(t.Term) && t.Term.Length >= 3)
            .Select(t => t.DocumentFrequency)
            .OrderBy(x => x)
            .ToArray();

        var p30 = Percentile(dfs, 0.30f);
        var p70 = Percentile(dfs, 0.70f);
        var maxDf = Math.Max(1, dfs.LastOrDefault());

        return terms.Select(t =>
        {
            var rarity = 1f - ((float)t.DocumentFrequency / maxDf);
            var hintSignal = t.HintRank <= 24 ? 1f - ((t.HintRank - 1f) / 24f) : 0f;
            var broad = t.DocumentFrequency >= p70 && t.DocumentFrequency > 2;
            var rare = t.DocumentFrequency <= p30;
            var dfPercentile = maxDf <= 0 ? 1f : MathF.Min(1f, t.DocumentFrequency / (float)maxDf);
            var localLift = MathF.Max(0f, hintSignal - dfPercentile);
            var concentration = hintSignal;
            var broadRisk = broad ? MathF.Min(1f, dfPercentile + 0.25f) : MathF.Max(0f, dfPercentile - 0.15f);

            var discriminatorScore =
                (rare ? 1f : 0f) * 1.25f +
                hintSignal * 0.65f +
                localLift * 0.75f +
                (t.IsMatchedQueryTerm ? 0.35f : 0f) -
                (broad ? 0.90f : 0f);

            var workflowScore =
                (t.IsInQuery ? 1f : 0f) * 0.85f +
                (t.IsMatchedQueryTerm ? 1f : 0f) * 0.65f +
                hintSignal * 0.35f -
                (rare ? 0.25f : 0f);

            return t with
            {
                Rarity = rarity,
                DocumentFrequencyPercentile = dfPercentile,
                LocalLift = localLift,
                ScopeConcentration = concentration,
                BroadRisk = broadRisk,
                IsBroad = broad,
                IsRare = rare,
                DiscriminatorScore = discriminatorScore,
                WorkflowScore = workflowScore
            };
        }).ToArray();
    }

    private static IReadOnlyList<string> SelectAnchors(IReadOnlyList<TermEvidence> evidence, IReadOnlySet<string> mustSet)
    {
        var must = evidence.Where(e => mustSet.Contains(e.Term)).Select(e => e.Term).Distinct(StringComparer.Ordinal).ToArray();
        if (must.Length > 0)
            return must;

        var matchedCandidates = evidence
            .Where(e => !LowRankDictionary.Contains(e.Term) && e.Term.Length >= 3)
            .Where(e => e.IsMatchedQueryTerm)
            .ToArray();

        var source = matchedCandidates.Length > 0
            ? matchedCandidates
            : evidence.Where(e => !LowRankDictionary.Contains(e.Term) && e.Term.Length >= 3 && (e.IsMatchedQueryTerm || e.IsInQuery)).ToArray();

        return source
            .OrderByDescending(e => AnchorScore(e, evidence))
            .ThenBy(e => e.DocumentFrequency)
            .ThenBy(e => e.HintRank)
            .Select(e => e.Term)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }

    private static string? SelectSuggestedMust(IReadOnlyList<TermEvidence> evidence, IReadOnlyList<string> anchorTerms)
    {
        if (anchorTerms.Count == 0)
            return null;

        var anchorEvidence = anchorTerms
            .Select(t => evidence.First(e => e.Term == t))
            .Select(e => new { Evidence = e, Score = AnchorScore(e, evidence) })
            .OrderByDescending(x => x.Score)
            .ToArray();

        var top = anchorEvidence[0];
        var secondScore = anchorEvidence.Length > 1 ? anchorEvidence[1].Score : 0f;
        var scoreGap = top.Score - secondScore;

        var strongConfidence =
            top.Evidence.IsMatchedQueryTerm &&
            !top.Evidence.IsBroad &&
            top.Evidence.DocumentFrequency > 1 &&
            top.Evidence.BroadRisk < 0.75f &&
            top.Score >= 1.80f &&
            scoreGap >= 0.30f;

        return strongConfidence ? top.Evidence.Term : null;
    }

    private static float AnchorScore(TermEvidence e, IReadOnlyList<TermEvidence> evidence)
    {
        var maxDf = Math.Max(1, evidence.Max(x => x.DocumentFrequency));
        var normDf = MathF.Min(1f, e.DocumentFrequency / (float)maxDf);
        return
            (e.IsMatchedQueryTerm ? 1f : 0f) * 1.40f +
            (e.IsInQuery ? 1f : 0f) * 1.10f +
            (1f - MathF.Abs(0.22f - normDf) * 1.8f) -
            (e.IsBroad ? 0.45f : 0f);
    }

    private static KeywordRoleAssignment ToAssignment(TermEvidence evidence, bool isAnchorOverride)
    {
        if (LowRankDictionary.Contains(evidence.Term) || evidence.Term.Length < 3)
            return new KeywordRoleAssignment(evidence.Term, KeywordRole.Noise, evidence.DocumentFrequency, evidence.DocumentFrequencyPercentile, evidence.Rarity, evidence.LocalLift, evidence.ScopeConcentration, evidence.BroadRisk, evidence.IsMatchedQueryTerm, evidence.IsMustTerm);

        var role = evidence.DiscriminatorScore >= 1.05f
            ? KeywordRole.Discriminator
            : KeywordRole.Workflow;

        if (isAnchorOverride || evidence.IsMustTerm)
            role = KeywordRole.Anchor;

        return new KeywordRoleAssignment(
            evidence.Term,
            role,
            evidence.DocumentFrequency,
            evidence.DocumentFrequencyPercentile,
            evidence.Rarity,
            evidence.LocalLift,
            evidence.ScopeConcentration,
            evidence.BroadRisk,
            evidence.IsMatchedQueryTerm,
            evidence.IsMustTerm);
    }

    private static string[] ComposeQuery(
        IReadOnlyList<string> anchors,
        IReadOnlyList<string> discriminators,
        IReadOnlyList<string> workflows,
        int discriminatorCount,
        int workflowCount)
    {
        var anchor = anchors.Take(1);
        var discriminator = discriminators.Take(discriminatorCount);
        var workflow = workflows.Take(workflowCount);
        return anchor
            .Concat(discriminator)
            .Concat(workflow)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }

    private static int Percentile(IReadOnlyList<int> values, float percentile)
    {
        if (values.Count == 0)
            return 0;

        var p = Math.Clamp(percentile, 0f, 1f);
        var idx = (int)MathF.Round((values.Count - 1) * p, MidpointRounding.AwayFromZero);
        return values[Math.Clamp(idx, 0, values.Count - 1)];
    }

    private static string BuildNextCommand(string? query, string? must)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "ck find-files \"<refined query>\" --path src/";

        if (string.IsNullOrWhiteSpace(must))
            return $"ck find-files \"{query}\" --path src/";

        return $"ck find-files \"{query} {must}\" --path src/";
    }

    private sealed record TermEvidence(
        string Term,
        int DocumentFrequency,
        bool IsInQuery,
        bool IsMatchedQueryTerm,
        bool IsMustTerm,
        int HintRank)
    {
        public float DocumentFrequencyPercentile { get; init; }
        public float LocalLift { get; init; }
        public float ScopeConcentration { get; init; }
        public float BroadRisk { get; init; }
        public bool IsBroad { get; init; }
        public bool IsRare { get; init; }
        public float Rarity { get; init; }
        public float DiscriminatorScore { get; init; }
        public float WorkflowScore { get; init; }
    }
}
