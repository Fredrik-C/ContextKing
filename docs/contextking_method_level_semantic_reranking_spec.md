# Specification: Method-Level Semantic Reranking for `ck find-files`

**Status:** Proposed  
**Target:** Context King  
**Primary command:** `ck find-files`  
**Scope:** Local, code-specialized reranking without a hosted or generative decision model

## 1. Summary

Enhance `ck find-files` with a second semantic stage that reranks lexically retrieved files using code evidence extracted from their methods. Context King will continue to use its fast file-level lexical index for candidate generation. For the small overfetched candidate set, it will parse live source, construct bounded method cards, embed those cards with a code-specialized retrieval model, and aggregate method relevance back into file scores.

The intended pipeline is:

```text
file-level lexical retrieval
  -> controlled file overfetch
  -> live method extraction from candidate files
  -> bounded method-card construction
  -> code-specialized query/method embedding
  -> method-to-file score aggregation
  -> lexical/semantic score fusion
  -> top-K files with optional explanations
```

This enhancement deliberately does not introduce Jev, Laya, Von, an LLM, or another hosted decision service. It preserves CK's local-first, predictable, cross-platform navigation model while adding behavioral evidence that cannot be inferred from file paths and signatures alone.

## 2. Motivation

The current automatic reranker embeds compact file cards containing:

- relative path;
- folder path;
- file name;
- type names;
- method/member names.

That representation is effective for locating code by vocabulary and ownership. It cannot reliably distinguish behavior that appears only in implementation details.

For example:

```bash
ck find-files "adyen terminal refund retry" \
  --task "Find terminal refund handling that retries after transient provider errors."
```

Several files may contain `Adyen`, `refund`, and `retry` in paths or member names. The relevant behavior may only be visible through:

- caught provider exception types;
- transient-error predicates;
- retry, backoff, rescheduling, or recovery calls;
- conditional branches;
- invoked payment-channel types;
- constants and enum members;
- the method body itself.

The proposed reranker exposes this evidence to a code-specialized embedding model without reading whole repositories, persisting a semantic code index, or returning method bodies to the calling agent.

## 3. Goals

The implementation must:

1. Preserve lexical search as the first-stage retriever.
2. Improve precision for behavioral and implementation-oriented searches.
3. Parse only the small set of lexically overfetched candidate files.
4. Rank at method/member granularity and return results at file granularity.
5. Support C#, TypeScript/TSX, Kotlin, and Python using CK's existing Roslyn/tree-sitter infrastructure.
6. Run locally without API keys or source-code transmission.
7. Degrade safely to the existing metadata reranker or lexical ordering.
8. Keep default command output stable.
9. Preserve recall by treating semantic scores as ranking evidence, not hard filters.
10. Make score components inspectable through `--explain` and `--verbose`.
11. Teach agents to express `--task` as positive retrieval intent.
12. Avoid repository-wide method embeddings and persistent vector indexes.

## 4. Non-goals

This enhancement does not:

- use a generative LLM;
- add Jev, Laya, Von, or another general-purpose decision model;
- guarantee logical interpretation of arbitrary negation or exclusions;
- make `--task` a filtering language;
- replace the lexical index;
- embed every method during `ck index`;
- persist method embeddings;
- perform full call-graph or data-flow analysis;
- guarantee that a helper method can be connected to every indirect caller;
- return method-level search results as a new public command;
- change `ck get-method-source`, `ck signatures`, or `ck expand-folder` semantics;
- make natural-language text the preferred first-stage query.

## 5. User-facing behavior

### 5.1 Command shape

The public command remains:

```bash
ck find-files "<lexical-query>" --task "<positive retrieval intent>" [existing options]
```

No model-specific flags are introduced in the normal agent workflow.

Recommended example:

```bash
ck find-files "adyen terminal refund retry transient" \
  --task "Find terminal refund handling that retries after transient provider errors."
```

### 5.2 Positive-only task guidance

`--task` must describe what should be found. Skill and README instructions must discourage negations and exclusions because bi-encoder similarity does not reliably model negative intent. Terms inside phrases such as `ignore card refunds` may still increase similarity to card-refund candidates.

Required skill wording:

```markdown
### Write `--task` as positive retrieval intent

Describe only the code and behavior you want to find. Avoid negations,
exclusions, and instructions about what to ignore; embedding-based reranking
may treat excluded concepts as relevant signals.

Good:

ck find-files "adyen terminal refund retry transient" \
  --task "Find terminal refund handling that retries after transient provider errors."

Avoid:

ck find-files "adyen terminal refund retry" \
  --task "Find retry handling for terminal refunds. Ignore card refunds."

When unwanted concepts must be excluded, omit them from both the lexical query
and `--task`. Narrow positively using provider, channel, operation, error type,
or expected behavior.
```

Required limitation notice:

```markdown
`--task` improves positive semantic matching; it does not implement exclusion
logic. Terms appearing in negative phrases such as "not", "exclude", or
"ignore" may still increase semantic similarity.
```

### 5.3 Optional warning for negative phrasing

CK may detect obvious negative phrases in `--task`:

```text
not
except
exclude
excluding
ignore
ignoring
without
do not
don't
```

Detection must be advisory only. It must not reject the command or rewrite the task.

When `--verbose` is active, print:

```text
[ck find-files] WARN: --task appears to contain an exclusion. Semantic reranking is optimized for positive retrieval intent; excluded terms may still increase similarity.
```

This warning must be emitted at most once per invocation.

### 5.4 Default output

Default output remains unchanged:

```text
<score>\t<relative-file-path>
```

### 5.5 Explain output

Extend `--explain` with compact method evidence:

```text
0.8712\tsrc/Payments/Adyen/TerminalRefundService.cs\tlexical=0.7132 metadata=0.8011 method=0.9234 best_member=RetryTerminalRefundAsync evidence=catch:AdyenTimeoutException,call:IsTransient,call:RetryAsync
```

Rules:

- `lexical` is the original file lexical score.
- `metadata` is the existing metadata semantic score when calculated, otherwise `-`.
- `method` is the aggregated method-level semantic score, otherwise `-`.
- `best_member` is the highest-scoring member name, safely truncated.
- `evidence` contains at most three structural evidence items and never includes source text.
- When method reranking is unavailable, display `method=unavailable` only if a method-stage failure occurred; otherwise display `method=-` when disabled.

## 6. Architecture

### 6.1 Stages

```text
1. Parse arguments and load settings.
2. Ensure the file index is fresh.
3. Retrieve lexical file candidates using the current searcher.
4. Optionally run the current metadata-card semantic reranker.
5. Select a bounded set of files for live method extraction.
6. Parse those files using the existing language analyzers.
7. Build bounded method cards.
8. Embed the positive task once.
9. Embed method cards in batches.
10. Score methods by cosine similarity.
11. Aggregate method scores to their containing files.
12. Fuse lexical, metadata, method, and structural signals.
13. Return top K files.
```

### 6.2 Component boundaries

Recommended new components:

```text
ContextKing.Core/SourceMap/
  MethodCandidateExtractor.cs
  MethodCandidateCard.cs
  MethodSemanticReranker.cs
  FileScoreFusion.cs

ContextKing.Core/Embedding/
  IBatchTextEmbedder.cs
  CodeEmbeddingModel.cs

ContextKing.Cli/
  CodeModelLocator.cs
```

Names may be adjusted to existing conventions, but extraction, embedding, aggregation, and score fusion must remain independently testable.

## 7. Candidate selection

### 7.1 File overfetch

Continue using controlled lexical overfetch. Method extraction must not run across the entire repository.

Recommended defaults:

```text
top requested by user:       20
lexical overfetch minimum:   50
lexical overfetch maximum:   100
method extraction file cap:  50
```

If the existing metadata reranker is enabled, method extraction may use the union of:

- top lexical candidates; and
- top metadata-reranked candidates.

Deduplicate by normalized relative path and cap the union deterministically.

### 7.2 Member selection

Extract supported executable members:

- methods and constructors;
- local functions when reliably supported;
- property accessors only when they contain non-trivial bodies;
- top-level functions;
- language-equivalent callable declarations.

Do not create independent cards for:

- abstract or interface declarations without bodies;
- trivial automatic properties;
- declarations generated or excluded by existing CK rules;
- members consisting only of a simple field/property return unless their names strongly match lexical terms;
- members whose source span cannot be resolved safely.

### 7.3 Per-file and global limits

Recommended defaults:

```text
maximum members per file:       24
maximum members per invocation: 500
maximum method-card characters: 6000
maximum body characters:        3500
```

When limits are exceeded, prioritize members using inexpensive signals:

1. lexical query term matches in member/type names;
2. task term matches in member/type names;
3. matches in call names, caught exceptions, literals, or referenced types;
4. existing signature relevance heuristics;
5. source order as a stable tie-breaker.

## 8. Method candidate representation

### 8.1 Data model

```csharp
public sealed record MethodCandidateCard(
    string FilePath,
    string Language,
    string ContainingType,
    string MemberName,
    string Signature,
    string StructuralSummary,
    string BodyExcerpt,
    int StartLine,
    int EndLine,
    IReadOnlyList<string> Evidence);
```

`BodyExcerpt` is an internal inference input. It must not be printed by `find-files`.

### 8.2 Embedding text

Recommended format:

```text
Language: CSharp
Path: src/Payments/Adyen/TerminalRefundService.cs
Type: TerminalRefundService
Member: RetryTerminalRefundAsync
Signature: Task RetryTerminalRefundAsync(TerminalRefund refund, CancellationToken cancellationToken)
Structure:
- catches AdyenTimeoutException, ProviderUnavailableException
- calls IsTransient, ExecuteWithRetryAsync, ScheduleRefundAsync
- references TerminalRefund, RefundChannel.Terminal
- branches on provider error classification
Code:
<bounded member source>
```

Field labels must remain stable because changing them changes the model input distribution and may affect ranking.

### 8.3 Structural evidence

Extract when supported by the language analyzer:

- invoked member names;
- constructed types;
- referenced type and enum names;
- caught exception types;
- attributes/decorators/annotations;
- string literals up to a conservative length;
- switch/match case labels;
- base/implemented types when relevant;
- control-flow keywords such as retry loops or exception handlers only as normalized descriptors;
- async/await status.

Do not attempt full semantic binding in languages where only syntax information is available. Structural extraction must be best-effort and deterministic.

### 8.4 Body bounding

Method bodies can exceed the model context. Apply these rules in order:

1. Include the complete member when it fits.
2. Preserve signature and structural summary.
3. Prefer statements containing query/task terms.
4. Prefer exception handlers and statements containing evidence symbols.
5. Include bounded leading and trailing context around selected statements.
6. Mark omitted regions with a stable token such as `<omitted>`.
7. Truncate on valid UTF-16 boundaries and never split surrogate pairs.

Initial implementation may use deterministic head-plus-tail truncation if relevance-aware excerpting would materially delay delivery. The selected strategy must be covered by tests.

## 9. Embedding model

### 9.1 Requirements

The chosen model must:

- be trained or demonstrably effective for natural-language-to-code retrieval;
- support commercial redistribution under a compatible license;
- be exportable to ONNX;
- run through ONNX Runtime on Windows, macOS, and Linux;
- support CPU inference;
- accept the intended method-card length;
- expose stable tokenizer assets;
- produce normalized or normalizable dense vectors;
- have a quantized distribution target suitable for optional download.

CodeRankEmbed is a candidate for evaluation, not a mandated dependency. Final selection must be based on CK-specific evaluation, license verification, ONNX compatibility, artifact size, and CPU performance.

### 9.2 Model packaging

Do not embed a large model inside the CK executable. Distribute it as an optional model pack:

```text
~/.ck/models/code-reranker/
  model.onnx
  tokenizer.json or vocab assets
  config.json
  LICENSE
  manifest.json
```

The manifest should include:

```json
{
  "modelId": "<stable-id>",
  "modelVersion": "<version>",
  "sha256": "<hash>",
  "dimensions": 768,
  "maxTokens": 8192,
  "pooling": "mean",
  "normalized": true,
  "quantization": "int8",
  "license": "<spdx-id>"
}
```

Downloads must use CK's existing installation/update conventions, verify checksums, and support offline reuse after installation.

### 9.3 Embedding abstraction

Extend without breaking existing implementations:

```csharp
public interface IBatchTextEmbedder : ITextEmbedder
{
    IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts);
}
```

The method reranker should prefer batching. A compatibility adapter may call `Embed` sequentially for tests or initial integration.

The existing BGE embedder remains available for CK Brain and current metadata operations unless separately migrated.

## 10. Query construction

### 10.1 Semantic query

Use `--task` as the method semantic query. Do not concatenate the full lexical query by default, because the task already represents intent and repeated keywords can over-amplify common terms.

If the task is missing, preserve the existing CLI validation requiring `--task`.

### 10.2 Positive intent validation

Do not transform natural language negation automatically. Rewriting `ignore card refunds` into a correct positive expression requires domain knowledge and may silently change intent.

The implementation may:

- warn in verbose mode;
- expose the limitation in skill documentation;
- include a diagnostic flag in explain telemetry.

It must not:

- remove words following `ignore`;
- infer antonyms;
- turn arbitrary clauses into penalties;
- fail the command because negative phrasing was used.

### 10.3 Future exclusion support

A future version may add a structured repeatable option:

```bash
--exclude "card refund"
```

That is explicitly outside this enhancement. The present design should avoid naming or serialization choices that prevent later independent exclusion scoring.

## 11. Method scoring

For query vector `q` and method-card vector `m`:

```text
raw_method_similarity = cosine(q, m)
method_similarity = clamp((raw_method_similarity + 1) / 2, 0, 1)
```

If the selected embedding model produces non-negative retrieval scores with a documented normalization, use that model's documented transformation and lock it in tests.

### 11.1 Structural boost

Apply only conservative deterministic boosts:

```text
member/type lexical match:          up to 0.04
call/exception/reference match:     up to 0.04
path/provider/channel match:        up to 0.02
maximum structural boost:           0.08
```

Structural boosts must not independently promote a candidate with an extremely low semantic score to the top.

### 11.2 Flat-score detection

If the method semantic score range is below a configured threshold, treat the method stage as low signal and reduce its effective weight.

Recommended initial threshold:

```text
max(methodScore) - min(methodScore) < 0.03
```

## 12. Aggregating methods to files

Do not use a plain average across every method in a file. Large service files would be penalized by unrelated methods.

Recommended aggregation:

```text
file_method_score =
    0.75 * best_method_score
  + 0.20 * second_best_method_score
  + 0.05 * top_three_mean
```

Rules:

- Missing second or third scores reuse neither zero nor the best score; renormalize weights across available methods.
- Store the best member and its structural evidence for explanation.
- A file with no extractable executable member receives no method score and remains eligible through lexical/metadata scoring.
- Cap any member-count bonus to avoid favoring large files.

## 13. File score fusion

### 13.1 Normalization

Normalize lexical scores within the candidate set as today. Normalize metadata and method signals according to their documented ranges.

### 13.2 Default weights

Recommended initial full-signal formula:

```text
final_score =
    0.45 * normalized_lexical
  + 0.15 * metadata_semantic
  + 0.35 * file_method_score
  + structural_bonus
  - generic_penalty
```

Weights must be treated as starting values to be tuned on CK's evaluation set.

If metadata semantic reranking is disabled or unavailable:

```text
final_score =
    0.55 * normalized_lexical
  + 0.40 * file_method_score
  + structural_bonus
  - generic_penalty
```

Renormalize absent components rather than treating them as zero.

### 13.3 Recall safeguards

The implementation must:

- never discard candidates solely because method similarity is low;
- keep lexical candidates with no parseable methods;
- reduce the method weight when semantic scores are flat;
- use original lexical order as the final deterministic tie-breaker;
- return lexical top K on total method-stage failure;
- avoid large score discontinuities at extraction limits.

## 14. Configuration

Extend `.ck.json` while preserving backward compatibility:

```json
{
  "findFiles": {
    "semanticRerank": true,
    "methodRerank": false,
    "methodRerankModel": "code-reranker",
    "methodCandidateFiles": 50,
    "maxMethodsPerFile": 24,
    "maxMethodsTotal": 500,
    "maxMethodCardChars": 6000,
    "maxBodyChars": 3500,
    "lexicalWeight": 0.45,
    "metadataSemanticWeight": 0.15,
    "methodSemanticWeight": 0.35,
    "structuralBoostMax": 0.08,
    "flatMethodThreshold": 0.03
  }
}
```

Rollout default:

- `methodRerank = false` during experimental releases;
- enable by default only after acceptance benchmarks pass and model distribution is stable.

Loader behavior:

- missing values use defaults;
- invalid numbers are clamped or replaced with defaults;
- malformed sections warn only under `--verbose`;
- existing `findFiles` configurations continue to work;
- candidate and character caps have hard safety ceilings.

## 15. Failure handling

### 15.1 Model unavailable

If the code model is absent:

```text
[ck find-files] WARN: method rerank unavailable: <reason>. Using metadata/lexical ranking.
```

Normal command success must not depend on the optional model.

During the experimental phase, CK must not download the model implicitly during a normal search unless existing CK installation policy explicitly supports and communicates such downloads. Prefer installation-time or explicit model acquisition.

### 15.2 Parse failures

For individual file/member parse failures:

- skip the failed extraction;
- continue processing other candidates;
- retain the file via lexical/metadata scoring;
- summarize skipped counts only under `--verbose`.

### 15.3 Embedding failures

For partial method embedding failures:

- score successful method cards;
- retain affected files through other signals;
- do not append failed methods as synthetic zero scores;
- report aggregate failure counts under `--verbose`.

For total method-stage failure, fall back to the existing metadata reranker or lexical order.

### 15.4 Cancellation and time budget

The method stage should accept cancellation. Consider a configurable soft time budget. If exceeded:

- use completed method scores;
- retain all remaining files through lexical/metadata scores;
- report the partial result under `--verbose`;
- keep exit code `0` when valid lexical results exist.

## 16. Performance requirements

Initial release targets on a representative developer laptop, warm model:

```text
lexical retrieval:                    unchanged within noise
parse/extract 50 candidate files:     target <= 300 ms median
embed up to 500 bounded methods:      target <= 1200 ms median CPU
complete find-files invocation:       target <= 1800 ms median warm CPU
peak additional memory:               target <= 750 MB
```

These are product targets, not guaranteed model characteristics. Final thresholds should be revised after selecting and quantizing the model.

Additional requirements:

- cache the loaded ONNX session within the process;
- batch embeddings;
- avoid repeated parsing of the same file within one invocation;
- avoid writing candidate embeddings to disk;
- do not make index refresh materially slower;
- measure cold-start separately from warm inference.

An optional bounded in-process cache keyed by `(modelVersion, fileFingerprint, memberSpan)` may be considered later. Persistent embeddings are out of scope.

## 17. Privacy and security

The implementation must:

- perform inference locally;
- make no network calls during reranking;
- never transmit source code;
- verify downloaded model artifacts by checksum;
- include upstream model license and attribution;
- avoid logging method bodies or secrets;
- ensure `--explain` prints identifiers and structural evidence only;
- preserve CK's generated/vendor exclusion behavior;
- treat source text as untrusted data, not instructions.

## 18. Tests

### 18.1 Method-card unit tests

Cover every supported language:

1. Extracts signature and body span correctly.
2. Extracts calls and caught exception types when supported.
3. Includes containing type and relative path.
4. Skips bodyless declarations.
5. Skips trivial automatic properties.
6. Truncates deterministically.
7. Preserves valid Unicode boundaries.
8. Redacts source text from explain evidence.
9. Applies per-file and global limits deterministically.
10. Handles malformed/incomplete working-tree source without crashing.

### 18.2 Method reranker unit tests

Use a fake batch embedder:

1. Empty candidates return empty scores.
2. A behaviorally relevant method promotes its file.
3. A second relevant method contributes less than the best method.
4. Large unrelated files are not favored by member count.
5. Flat semantic scores reduce method weight.
6. Missing methods preserve lexical eligibility.
7. Partial embedding failure retains affected files.
8. Structural boosts remain capped.
9. Original lexical order breaks ties deterministically.
10. Aggregation renormalizes correctly for one or two methods.

### 18.3 Command tests

1. Existing command syntax remains valid.
2. Default stdout stays `<score>\t<path>`.
3. Explain output contains method fields when enabled.
4. Missing model falls back successfully.
5. Parser failure falls back successfully.
6. Method reranking disabled preserves current behavior.
7. Negative task phrasing emits one warning only under `--verbose`.
8. Negative task phrasing is not rewritten or rejected.
9. Path scoping applies before method extraction.
10. Uncommitted working-tree changes are reflected.

### 18.4 Integration tests

Create small multi-language fixtures containing:

- a terminal refund retry after a transient exception;
- an ordinary card refund without retry;
- a generic retry helper;
- a terminal refund handler without error recovery;
- a similarly named test or migration file.

Assert that the complete positive task ranks the terminal transient-retry implementation above the distractors.

Do not use an exclusion phrase in the primary acceptance query. Add a separate regression test demonstrating that negative phrasing is advisory and not guaranteed.

### 18.5 Model smoke tests

For the actual ONNX artifact:

- output dimension matches the manifest;
- vectors contain finite values;
- normalization is within tolerance;
- batch and single-item results agree within tolerance;
- identical inputs produce stable results;
- task/code pairs rank above selected hard negatives;
- Windows, macOS, and Linux artifacts load successfully.

## 19. Evaluation plan

### 19.1 Dataset

Build an evaluation set containing at least:

```text
100 search tasks
5 or more repositories
all supported languages where possible
50 lexical candidates per task
one or more relevant files per task
graded labels: primary, supporting, irrelevant, misleading
```

Include task categories:

- behavior implemented inside generic method names;
- provider-specific handling;
- exception and retry logic;
- validation and authorization;
- serialization/mapping behavior;
- cross-module vocabulary mismatch;
- tests versus production implementations;
- large service files;
- indirect helper methods;
- lexical hard negatives.

### 19.2 Systems compared

Evaluate:

1. lexical baseline;
2. current metadata-card BGE reranker;
3. metadata cards with the code model;
4. signature-only method cards;
5. structured method cards without bodies;
6. structured method cards with bounded bodies;
7. full proposed score fusion.

This ablation is required to distinguish gains from the model from gains caused by richer candidate evidence.

### 19.3 Metrics

Primary metrics:

- Recall@5;
- Recall@10;
- Mean Reciprocal Rank;
- nDCG@10 for graded labels;
- percentage of tasks with a relevant result ranked first.

Operational metrics:

- warm and cold latency;
- peak memory;
- model download size;
- parse failures;
- embedding failures;
- fallback frequency.

### 19.4 Acceptance criteria

Before enabling by default, require all of:

1. Recall@5 improves by at least 5 absolute percentage points over the current reranker, or MRR improves by at least 10% relative, on the held-out CK evaluation set.
2. Recall@10 does not regress by more than 1 absolute percentage point.
3. At least 70% of behavioral-query categories improve or remain neutral.
4. Warm median latency remains within the agreed product budget.
5. P95 latency and memory are acceptable on CPU-only machines.
6. No supported language shows a material unexplained regression.
7. Failure injection confirms lexical/metadata fallback.
8. Model licensing and redistribution review is complete.

Do not tune weights on the final held-out set.

## 20. Telemetry and diagnostics

CK should not add remote telemetry. Local verbose diagnostics may include:

```text
lexical candidates: 100
method candidate files: 50
methods extracted: 312
method cards embedded: 308
parse failures: 1
embedding failures: 4
method stage duration: 684 ms
method semantic range: 0.41..0.87
method stage status: active|flat|partial|unavailable|disabled
```

Never include source bodies in diagnostics.

## 21. Documentation changes

Update:

- `README.md` automatic candidate reranking section;
- `skills/ck-find-files/SKILL.md`;
- installed Claude, Codex, OpenCode, and generic-agent protocol text;
- `ck find-files --help`;
- advanced `.ck.json` configuration documentation;
- release notes;
- existing automatic-reranking specification, either by superseding it or linking to this document.

Remove or rewrite all examples that teach exclusions in `--task`, including variants of:

```text
Ignore card refunds.
Ignore normal card refund flows.
```

Use positive narrowing instead:

```text
Find terminal refund handling that retries after transient provider errors.
```

## 22. Rollout plan

### Phase 0: Evaluation harness

- Define the labeled query/file dataset format.
- Capture the current lexical and metadata baselines.
- Add reproducible metric and latency reporting.

### Phase 1: Extraction and cards

- Implement method extraction adapters.
- Implement structural summaries.
- Add deterministic bounding and tests.
- Evaluate cards with the existing embedder to isolate representation gains.

### Phase 2: Code model prototype

- Evaluate candidate code-retrieval models.
- Verify licenses and ONNX export.
- Add batch inference and model manifest support.
- Keep the feature disabled by default.

### Phase 3: Score fusion

- Implement method aggregation and file fusion.
- Add explain output and fallback paths.
- Tune weights on the training/development split.

### Phase 4: Experimental release

- Make the model pack explicitly installable.
- Enable through `.ck.json` only.
- Collect local benchmark reports and failure cases.
- Keep current metadata reranking as the default.

### Phase 5: Default enablement

- Re-run the frozen held-out evaluation.
- Confirm cross-platform packaging and performance.
- Enable by default for installations with the code model available.
- Preserve a repository-level opt-out.

## 23. Future extensions

Potential later work, explicitly outside this specification:

- structured `--exclude` with independently embedded negative facets;
- local cross-encoder classification over the final 10–20 methods;
- query decomposition into independently scored positive facets;
- lightweight call-graph expansion for generic helpers;
- CK-specific fine-tuning from labeled navigation sessions;
- quantized smaller student models;
- persistent method embeddings if benchmarks justify indexing cost;
- a method-level search command;
- hosted decision-model backends as optional plugins.

## 24. Definition of done

The enhancement is complete when:

- lexical search remains the first stage;
- only bounded lexical candidates are parsed;
- supported methods are converted into deterministic bounded cards;
- a local code-specialized model reranks methods;
- method scores aggregate to file scores without penalizing large files;
- lexical recall safeguards and fallbacks are implemented;
- stdout compatibility is preserved;
- explain output identifies the best member without leaking source;
- documentation teaches positive-only task phrasing;
- negative phrasing is documented as unsupported rather than silently interpreted;
- tests cover all supported languages and failure paths;
- the evaluation meets the acceptance criteria;
- model artifacts are licensed, checksummed, cross-platform, and optional;
- no repository-wide semantic code index or external service is introduced.

