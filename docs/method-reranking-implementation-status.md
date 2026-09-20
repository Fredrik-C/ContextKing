# Method reranking implementation status

The governing requirements remain [the full specification](contextking_method_level_semantic_reranking_spec.md). This is a progress record, not a replacement scope or completion claim.

## v1.8.14 rollout override

At the user's explicit request, method reranking is now enabled by default and the pinned CodeRankEmbed INT8 pack is bundled in all platform archives. Explicit `methodRerank: false` is respected. The standard local model directory is `~/.ck/models/code-reranker`. All 337 Windows tests passed with the actual model enabled. Release CI also gates native Windows, Linux x64, and macOS ARM64 smoke tests. The broader acceptance evaluation remains unfinished; default enablement is not evidence that those gates passed. Historical progress details below predate this rollout decision.

## Implemented foundation

- Optional batch embedder interface without changing existing embedders.
- Ephemeral method cards, stable input labels, deterministic head/tail bounding, UTF-16 boundary preservation.
- Live Roslyn and tree-sitter extraction for C#, TypeScript/TSX, Kotlin, and Python; bounded candidate files and methods; identifier-only explanation evidence.
- Method scoring with one task embedding, batches, individual retry after batch failure, invalid-vector rejection, cancellation, flat-score detection, and top-three aggregation.
- Independently testable file fusion, missing-component normalization, capped structural boosts, and lexical-order tie-breaking.
- 41 focused extraction/command tests pass on Windows, covering all four languages, bodyless/stub declarations, constructors/accessors/local functions, structural evidence, nested attribution, live edits, scoping, scoring, fusion, malformed source, and failure injection. Full suite: 336 passed, 1 optional artifact test skipped.
- CLI and settings integration is implemented: opt-in configuration, bounded overfetch, metadata/method fusion, lexical fallback after total scoring failure, missing-model fallback, explain fields, verbose diagnostics, and a once-per-invocation negative-intent warning. Focused command/settings suite: 15 passing tests.
- Added a verified local ONNX runtime with BERT tokenization, query/document prefix separation, pooling/output manifest contract, dynamic batch padding, CPU inference, and process session reuse. BGE remains unchanged.
- Manifest loading now rejects checksum mismatches, missing licenses/assets, path traversal, symlink/reparse-point assets, unsupported tokenizer/pooling/dimension contracts, and non-finite vectors. Security/manifest focused tests pass (6).
- Explicit PowerShell 7 model-pack installer pins every asset checksum and successfully downloaded the experimental CodeRankEmbed INT8 pack to `C:/Users/rayta/.ck/models/contextking-coderank-prototype`. Re-running verified offline reuse. Normal search does not download the pack.
- README, repository skill, shared agent protocol, Codex installer templates, CLI help, advanced configuration, release notes, and preceding automatic-reranking spec now teach positive task intent.

## Actual-model evidence and unresolved issue

The optional smoke test can be reproduced with:

```powershell
$env:CK_CODE_MODEL_TEST_DIR = 'C:/Users/rayta/.ck/models/contextking-coderank-prototype'
dotnet test src/ContextKing.Tests/ContextKing.Tests.csproj --filter FullyQualifiedName~CodeEmbeddingModelTests -v normal
```

Pinned model: `mrsladoje/CodeRankEmbed-onnx-int8` revision `e74f446dc6e67e29fcee77213472c142f73a6bbb`, SHA-256 `4eae31d09b1843103a1ebd5e2b2e24b5a5cad441a33906b35b12b1e2ed91d1db`.

The artifact loads on Windows, returns finite normalized 768-dimensional vectors, is deterministic for repeated identical inputs, and passes the factorial/code and transient-terminal-refund hard-negative probes. The upstream and CK runtimes show quantized batch/single cosine values of 0.977500, 0.955667, 0.976937, and 0.973422; the pack therefore declares a model-specific minimum of 0.95 rather than pretending quantization is exact. The smoke test records these measurements and passes against that explicit contract. This is not final model selection or accepted cross-platform evidence; CPU, OS, and reference-export comparisons still remain.

## Required work still outstanding

- Audit and complete extraction details: decorators/annotations, enum/case/reference evidence, constructors/accessors/local functions across languages, exclusions, nested-member attribution, Python bodyless conventions, deterministic global-limit and live-worktree tests.
- Complete audits of settings, fallback paths, cancellation, explanation privacy, structural boosts, and score discontinuities at extraction caps. Path-scoping and uncommitted-edit command tests now pass; parser and partial embedding failure command tests remain to be expanded.
- Select a code model using CK-specific evaluation, license verification, ONNX compatibility, download size, and CPU performance. The installed CodeRankEmbed INT8 pack is a prototype, with the unresolved consistency issue above.
- Validate tokenizer parity against the upstream tokenizer; expand manifest validation/security tests and installation/release packaging. The pinned Windows artifact now passes its declared 0.95 batch-agreement smoke gate; cross-platform artifact loading and cold/warm performance evidence remain.
- Add complete multi-language behavioral integration fixtures and tests; existing command integration currently exercises C# with a fake batch embedder.
- Audit installed-agent update paths, release packaging of the optional model installer/manifest, and documentation consistency. No global agent installations were changed.
- Build the reproducible evaluation harness and graded dataset: at least 100 tasks, five repositories, all supported languages, 50 lexical candidates/task, development and held-out splits, all seven ablations, quality and operational metrics.
- The harness contract and scorer are now present in `docs/evaluation/` and `scripts/evaluate-method-reranking.ps1`; the required real 100-task/five-repository labeled dataset and measured runs remain to be supplied.
- Run acceptance checks including cross-platform artifact loading, actual-model behavioral hard negatives, cold/warm CPU latency and memory, regression analysis, fallback injection, and license/redistribution review. The original disabled-until-acceptance rollout was superseded by the explicit v1.8.14 user request above.

The implementation is exposed by `ck find-files` unless `findFiles.methodRerank` is explicitly disabled. The full evaluation objective remains active.
