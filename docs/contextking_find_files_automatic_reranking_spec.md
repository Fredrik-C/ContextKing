# Specification: Automatic Candidate Reranking for `ck find-files`

## 1. Reason

Context King currently uses fast lexical comparison for `find-files`, keyword maps, and related filtering tools. This keeps indexing cheap and avoids the earlier problem where indexing and embedding all keywords made index creation too slow.

The tradeoff is that lexical search sometimes needs to **overfetch**. This is acceptable because lexical search is fast, but it means the agent may still receive too many plausible candidates, especially when:

```text
- domain terms overlap across modules
- provider-specific code shares common abstractions
- method names are generic
- folders contain many related but wrong files
- the task intent is narrower than the lexical query can express
```

The goal is to keep the current lexical-first architecture, but improve the final subset of results before they enter the agent’s context.

This avoids going back to full semantic indexing while still getting some of the precision benefits of semantic comparison.

## 2. Goal

Add an automatic, internal reranking stage to `ck find-files`.

The intended flow becomes:

```text
lexical candidate generation
  -> internal overfetch
  -> in-memory semantic reranking of compact candidate metadata
  -> return top K
```

The reranker should:

```text
- preserve lexical search as the first-stage retriever
- avoid full-file reads
- avoid source parsing during search
- avoid persistent semantic indexes
- keep `ck find-files` simple for agents
- reduce noisy overfetch before results reach the context window
- degrade safely to lexical-only behavior if reranking is unavailable
```

## 3. Non-goals

This feature does **not**:

```text
- expose semantic tuning flags to agents
- embed the whole repository
- persist candidate embeddings in `.ck-index`
- use an LLM
- perform investigation or reasoning over source bodies
- replace `ck signatures`, `ck get-method-source`, or `ck expand-folder`
- make natural-language questions the preferred search input
```

The project should still teach agents to use compact lexical queries.

## 4. User-facing behavior

### 4.1 Normal usage includes task intent

Agents continue to call:

```bash
ck find-files "adyen terminal refund retry" \
  --task "Find retry handling for terminal refunds after transient provider errors."
```

No extra semantic flags are introduced.

The command remains easy to teach:

```text
Use code terms: path names, file names, type names, method names, domain words.
```

### 4.2 Required task description

Require one task-intent parameter:

```bash
ck find-files "adyen terminal refund retry" \
  --task "Find retry handling for terminal refunds after transient provider errors. Ignore normal card refunds."
```

`--task` is **not** used for first-stage search. It is required as richer semantic intent for reranking.

```text
lexical query -> candidate generation
task description -> semantic rerank query text
```

This keeps lexical retrieval grounded while giving the reranker better intent when the task is hard to express as keywords.

### 4.3 No new tuning flags

Do **not** add these user-facing options:

```text
--semantic
--overfetch
--semantic-weight
--lexical-weight
```

These should be internal or repo-level defaults only.

## 5. Configuration

Use defaults controlled internally and optionally via `.ck.json`.

Example:

```json
{
  "minVersion": "1.8.7",
  "brain": true,
  "findFiles": {
    "semanticRerank": true,
    "overfetchMultiplier": 5,
    "minOverfetch": 50,
    "maxOverfetch": 200,
    "lexicalWeight": 0.65,
    "semanticWeight": 0.30,
    "mustWeight": 0.10,
    "genericPenaltyMax": 0.10
  }
}
```

Recommended defaults:

```text
semanticRerank = true after stable, false while experimental
overfetchMultiplier = 5
minOverfetch = 50
maxOverfetch = 200
lexicalWeight = 0.65
semanticWeight = 0.30
mustWeight = 0.10
genericPenaltyMax = 0.10
```

The skill documentation should not mention these settings unless documenting advanced repository configuration.

## 6. Suggested implementation

### 6.1 High-level flow

Update `FindFilesCommand` to do this internally:

```text
1. Parse existing find-files arguments.
2. Parse required --task.
3. Load repository settings from .ck.json.
4. Ensure index is fresh, as today.
5. Calculate internal lexical overfetch count.
6. Run lexical file search for overfetch count.
7. If semantic reranking is enabled:
      rerank lexical candidates in memory
      return top K
   Else:
      return lexical top K
8. If semantic reranking fails:
      warn and return lexical top K
```

Pseudo-code:

```csharp
var top = reader.TryGetInt("--top", out var parsedTop) && parsedTop > 0
    ? parsedTop
    : 20;

var taskDescription = reader.GetString("--task");

var settings = CkSettings.Load(repoRoot);

var lexicalTopK = settings.FindFiles.SemanticRerank
    ? Math.Clamp(
        top * settings.FindFiles.OverfetchMultiplier,
        settings.FindFiles.MinOverfetch,
        settings.FindFiles.MaxOverfetch)
    : top;

var lexicalCandidates = searcher.Search(
    dbPath,
    query,
    topK: lexicalTopK,
    minScore: minScore,
    allowedFolders: normalizedRoots,
    mustTerms: mustTerms);

IReadOnlyList<FileSearchHit> finalHits;

if (settings.FindFiles.SemanticRerank)
{
    try
    {
        finalHits = semanticReranker.Rerank(
            lexicalQuery: query,
            taskDescription: taskDescription,
            lexicalCandidates: lexicalCandidates,
            topK: top,
            options: settings.FindFiles.ToSemanticOptions());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[ck find-files] WARN: semantic rerank unavailable: {ex.Message}. Falling back to lexical results.");

        finalHits = lexicalCandidates.Take(top).ToArray();
    }
}
else
{
    finalHits = lexicalCandidates.Take(top).ToArray();
}
```

## 7. Candidate representation

Do not embed full files. Build compact candidate cards from indexed metadata.

Recommended candidate card:

```csharp
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
    public string ToEmbeddingText(int maxChars = 4000)
    {
        var text = $"""
        Path: {Path}
        Folder: {FolderPath}
        File: {FileName}
        Types: {TypeNames}
        Members: {MethodNames}
        """;

        return text.Length <= maxChars
            ? text
            : text[..maxChars];
    }
}
```

Candidate text should include:

```text
- relative path
- folder path
- file name
- type names
- method/member names
```

Candidate text should not include:

```text
- full source content
- method bodies
- comments
- source snippets
```

This keeps reranking cheap and aligned with Context King’s navigation-first purpose.

## 8. Search result model

The current `ScoredFile` can be extended, or a new result model can be introduced.

Preferred new model:

```csharp
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
    string MethodNames,
    IReadOnlyList<string> MatchedTerms);
```

Benefits:

```text
- keeps original lexical score
- allows explain output to show semantic contribution
- avoids overloading Score with different meanings
- makes tests clearer
```

`FileMapSearcher` can return `FileSearchHit` directly, or an adapter can convert current `ScoredFile` into `FileSearchHit`.

## 9. Reranker component

Add a dedicated component:

```csharp
public sealed class CandidateSemanticReranker
{
    private readonly ITextEmbedder _embedder;

    public CandidateSemanticReranker(ITextEmbedder embedder)
    {
        _embedder = embedder;
    }

    public IReadOnlyList<FileSearchHit> Rerank(
        string lexicalQuery,
        string? taskDescription,
        IReadOnlyList<FileSearchHit> lexicalCandidates,
        int topK,
        SemanticRerankOptions options)
    {
        if (lexicalCandidates.Count == 0)
            return [];

        var semanticQuery = string.IsNullOrWhiteSpace(taskDescription)
            ? lexicalQuery
            : taskDescription;

        var queryVector = _embedder.Embed(semanticQuery);

        var cards = lexicalCandidates
            .Select(SearchCandidateCard.FromHit)
            .ToArray();

        var semanticScores = cards
            .Select(card => CosineSimilarity(queryVector, _embedder.Embed(card.ToEmbeddingText(options.MaxCandidateTextChars))))
            .ToArray();

        return CombineAndSort(
            lexicalCandidates,
            semanticScores,
            topK,
            options);
    }
}
```

Add options:

```csharp
public sealed record SemanticRerankOptions(
    float LexicalWeight = 0.65f,
    float SemanticWeight = 0.30f,
    float MustWeight = 0.10f,
    float GenericPenaltyMax = 0.10f,
    int MaxCandidateTextChars = 4000,
    float FlatSemanticThreshold = 0.03f);
```

## 10. Embedding abstraction

Use a small abstraction so the reranker can be tested deterministically.

```csharp
public interface ITextEmbedder
{
    float[] Embed(string text);
}
```

If the existing embedder already exposes an equivalent method, wrap it rather than duplicating it.

Future improvement:

```csharp
public interface IBatchTextEmbedder : ITextEmbedder
{
    IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts);
}
```

Batching can be added later if per-candidate embedding is too slow.

## 11. Scoring

### 11.1 Normalize lexical scores

Lexical scores are not naturally comparable to cosine scores, so normalize within the candidate set:

```csharp
normalizedLexical =
    (lexicalScore - minLexicalScore) /
    (maxLexicalScore - minLexicalScore);
```

If all lexical scores are equal:

```csharp
normalizedLexical = 1.0f;
```

### 11.2 Normalize semantic scores

For cosine similarity:

```csharp
normalizedSemantic = Math.Clamp((cosine + 1.0f) / 2.0f, 0.0f, 1.0f);
```

If the embedding model already returns non-negative normalized cosine values, direct clamp is acceptable:

```csharp
normalizedSemantic = Math.Clamp(cosine, 0.0f, 1.0f);
```

### 11.3 Combine scores

Default formula:

```text
final_score =
    lexical_weight * normalized_lexical
  + semantic_weight * normalized_semantic
  + must_bonus
  - generic_penalty
```

The lexical score remains dominant.

### 11.4 Flat semantic score fallback

If semantic scores are too close together, semantic reranking has low signal.

Rule:

```csharp
if (maxSemantic - minSemantic < options.FlatSemanticThreshold)
{
    effectiveSemanticWeight = 0.10f;
    effectiveLexicalWeight = 0.85f;
}
```

This prevents random embedding noise from reshuffling good lexical results.

### 11.5 Lexical grounding guard

A candidate should not jump to the top purely because its metadata sounds semantically similar.

Rule:

```csharp
if (candidate.MatchedTerms.Count == 0)
{
    semanticContribution = Math.Min(semanticContribution, 0.10f);
}
```

This requires semantic promotion to be grounded in at least some lexical evidence.

### 11.6 Must boost

Keep `--must` soft.

If a candidate matches must terms in path, type, or member fields:

```csharp
mustBonus = options.MustWeight * matchedMustTerms / totalMustTerms;
```

Missing must terms should not remove the candidate.

## 12. Generic penalties

Apply conservative penalties to broad or noisy candidates.

Initial suggestions:

```text
- path contains migration/migrations/legacy/temp/tmp: -0.05
- signature count > 100: -0.05
- signature count > 250: -0.10
```

Do not overfit early. Tune this after collecting real examples.

## 13. Failure handling

Semantic reranking must be safe to enable.

### 13.1 Embedder unavailable

If the local embedding model is missing, cannot load, or is unsupported:

```text
[ck find-files] WARN: semantic rerank unavailable: <reason>. Falling back to lexical results.
```

Return lexical results with exit code `0` if lexical results exist.

### 13.2 Partial candidate failures

If some candidates fail to embed:

```text
- rerank candidates that embedded successfully
- append failed candidates by original lexical order
- keep total result count up to top K
```

Optional warning:

```text
[ck find-files] WARN: semantic rerank skipped 7 candidates. Appending them by lexical score.
```

### 13.3 No lexical candidates

Existing behavior remains:

```text
[ck find-files] No matches found.
```

Exit code remains `1`.

## 14. Explain output

Default output remains unchanged:

```text
<score>\t<path>
```

When semantic rerank is active, `<score>` is final score.

With `--explain`, include components:

```text
0.8421\tsrc/Payments/Adyen/TerminalRefundService.cs\ttypes=2 signatures=8 lexical=0.7132 semantic=0.9210 matched=adyen,terminal,refund
```

If semantic reranking was unavailable and lexical fallback was used:

```text
0.7132\tsrc/Payments/Adyen/TerminalRefundService.cs\ttypes=2 signatures=8 lexical=0.7132 semantic=unavailable matched=adyen,terminal,refund
```

## 15. Settings loader

Add a small settings model rather than parsing `.ck.json` ad hoc in commands.

```csharp
public sealed record CkSettings(
    string? MinVersion,
    bool Brain,
    FindFilesSettings FindFiles);

public sealed record FindFilesSettings(
    bool SemanticRerank = true,
    int OverfetchMultiplier = 5,
    int MinOverfetch = 50,
    int MaxOverfetch = 200,
    float LexicalWeight = 0.65f,
    float SemanticWeight = 0.30f,
    float MustWeight = 0.10f,
    float GenericPenaltyMax = 0.10f);
```

Loader behavior:

```text
- missing findFiles section -> use defaults
- malformed findFiles section -> warn only in verbose mode, use defaults
- invalid numeric values -> clamp or use defaults
```

This keeps `.ck.json` backward-compatible.

## 16. Tests

### 16.1 Unit tests for reranker

Use fake embedder.

Required tests:

```text
1. Empty candidates returns empty.
2. Semantic score can promote a lexically lower candidate.
3. Flat semantic scores preserve lexical ordering.
4. Candidate with zero matched lexical terms cannot jump to top.
5. Must-matching candidate receives small boost.
6. Generic penalty reduces noisy candidate score.
7. Embedder failure falls back to lexical results at command level.
8. Candidate card text truncates to max length.
```

### 16.2 Command tests

Required tests:

```text
1. `ck find-files` usage with required `--task` works.
2. `--task` is accepted and passed to reranker.
3. Missing `--task` fails with a clear error.
4. `--explain` shows semantic fields when reranking is active.
5. Lexical fallback still returns results when reranker fails.
```

### 16.3 Regression tests

Add tests proving that without configured semantic rerank, current lexical ordering remains unchanged.

## 17. Skill documentation update

The skill should remain simple.

Recommended wording:

```markdown
Use `ck find-files` first for source discovery. Write the query as compact lexical code terms: path names, file names, type names, method names, and domain words.

Good:
`ck find-files "adyen terminal refund retry" --task "Find retry handling for terminal refunds."`

Avoid:
`ck find-files "where is the refund retry logic implemented" --task "Find retry handling for terminal refunds."`

Always keep the main query lexical and pass the task intent separately with `--task`:

`ck find-files "adyen terminal refund retry" --task "Find retry handling for terminal refunds after transient provider errors. Ignore normal card refund flows."`
```

Do not mention:

```text
semantic rerank
overfetch
weights
embedding model
```

unless writing developer documentation.

## 18. README documentation update

Add a short developer-facing section:

```markdown
### Automatic candidate reranking

`ck find-files` uses lexical search as its first-stage retriever. Internally, CK may overfetch lexical candidates and rerank that small candidate set using compact metadata cards built from path, type, and member names.

This avoids maintaining a repository-wide semantic index while improving result precision for ambiguous searches. No full source files are read during reranking, and candidate embeddings are not persisted.

Always pass `--task` to provide reranking context while keeping the main query lexical:

```bash
ck find-files "adyen terminal refund retry" --task "Find retry handling for terminal refunds after transient provider errors. Ignore card refunds."
```
```

## 19. Rollout plan

### Phase 1: Internal implementation

```text
- Add settings model
- Add candidate card model
- Add reranker with fake-embedder tests
- Keep semanticRerank default false
```

### Phase 2: Command integration

```text
- Wire reranker into FindFilesCommand
- Add --task
- Add explain fields
- Add fallback behavior
```

### Phase 3: Real-world tuning

```text
- Test on ContextKing itself
- Test on a large C# repo
- Test on a TS/Kotlin/Python mixed repo
- Tune weights and penalties
```

### Phase 4: Enable by default

```text
- Flip semanticRerank default to true
- Keep .ck.json override available
- Update README and skill docs
```

## 20. Acceptance criteria

The implementation is accepted when:

```text
- `ck find-files` remains simple and does not expose semantic tuning flags.
- Usage with required `--task` succeeds.
- Missing `--task` fails clearly.
- Lexical search remains the first-stage retriever.
- Internal overfetch is controlled by settings, not CLI flags.
- Semantic reranking embeds only compact candidate metadata.
- No full source files are read during reranking.
- No semantic candidate embeddings are persisted.
- Reranking failure falls back to lexical results.
- `--explain` shows lexical and semantic score components.
- Unit tests cover score combination, grounding guard, flat scores, and fallback.
- README and skill docs explain the behavior without adding agent-facing complexity.
```
