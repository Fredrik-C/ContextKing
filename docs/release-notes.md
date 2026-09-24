# v1.8.15

- Improved `ck find-files` ranking with case-insensitive document frequency, member-term co-occurrence, restated-term damping, and consistent signal spreading before fusion.
- Added method cards for bodiless C# declarations, including interface methods, so contract files can benefit from method reranking.
- Improved reranking coverage and performance with round-robin card allocation, a smaller dense-mode card budget, more CPU threads, and bounded SQLite card-vector caching.
- Added cache safety limits and uncached fallback for unavailable or corrupt cache stores.

# v1.8.14

- Added default-on local method-level reranking for `ck find-files`, using bounded live C#, TypeScript/TSX, Kotlin, and Python AST cards. File-level lexical retrieval remains first; method embeddings are ephemeral.
- Bundled the pinned CodeRankEmbed INT8 model in every platform archive, with checksum verification, CPU batch inference, and query-prefix/pooling contracts. Standard installers place it in `~/.ck/models/code-reranker`; explicit `methodRerank: false` remains supported.
- Added method-score, best-member, and identifier-only evidence fields to `--explain`, and local stage diagnostics to `--verbose`. Default stdout remains two tab-separated columns.
- Documented positive-only task intent. Obvious negative phrasing receives a verbose advisory warning, without rewriting or rejecting the task.
- Added fallback, live-worktree, path-scoping, configuration, extraction, scoring, and model smoke tests. Actual-model tests are opt-in via `CK_CODE_MODEL_TEST_DIR`; cross-platform and held-out acceptance results are not yet complete.
