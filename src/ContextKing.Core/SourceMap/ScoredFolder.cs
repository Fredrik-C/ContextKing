namespace ContextKing.Core.SourceMap;

public readonly record struct ScoredFolder(string Path, float Score);

public readonly record struct ScoredFolderDetails(
    string Path,
    float Score,
    float SemanticScore,
    float ExactBonus,
    float MustAdjustment,
    float NoisePenalty,
    int FileCount,
    int TokenCount,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<string> UnmatchedFolderTerms,
    string CombinedTokens = "");
