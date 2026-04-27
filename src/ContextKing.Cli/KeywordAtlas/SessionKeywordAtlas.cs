namespace ContextKing.Cli.KeywordAtlas;

internal sealed record SessionKeywordAtlas(
    string Query,
    IReadOnlyList<string> QueryTerms,
    IReadOnlyList<string> MustTerms,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<string> UnmatchedTerms,
    IReadOnlyList<string> GlobalHints,
    IReadOnlyList<string> HighValueTerms,
    IReadOnlyList<SessionKeywordAtlasEntry> KeywordMap,
    DateTime CreatedAtUtc);

internal sealed record SessionKeywordAtlasEntry(
    string Seed,
    IReadOnlyList<string> Terms);
