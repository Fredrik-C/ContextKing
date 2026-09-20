# v1.8.14

- Added default-on local method-level reranking for `ck find-files`, using bounded live C#, TypeScript/TSX, Kotlin, and Python AST cards. File-level lexical retrieval remains first; method embeddings are ephemeral.
- Bundled the pinned CodeRankEmbed INT8 model in every platform archive, with checksum verification, CPU batch inference, and query-prefix/pooling contracts. Standard installers place it in `~/.ck/models/code-reranker`; explicit `methodRerank: false` remains supported.
- Added method-score, best-member, and identifier-only evidence fields to `--explain`, and local stage diagnostics to `--verbose`. Default stdout remains two tab-separated columns.
- Documented positive-only task intent. Obvious negative phrasing receives a verbose advisory warning, without rewriting or rejecting the task.
- Added fallback, live-worktree, path-scoping, configuration, extraction, scoring, and model smoke tests. Actual-model tests are opt-in via `CK_CODE_MODEL_TEST_DIR`; cross-platform and held-out acceptance results are not yet complete.
