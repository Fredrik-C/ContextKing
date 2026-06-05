## Install
Step 1 - global install (once per machine):

Mac / Linux:
`curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash`

Windows (PowerShell 7+):
`irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex`

Step 2 - initialize each repo (once per repo):
```bash
cd /path/to/your-repo
ck init
```

## Highlights

- Added automatic metadata reranking for `ck find-files`, keeping lexical search as the first-stage retriever while improving precision for ambiguous result sets.
- Enabled semantic reranking by default and added default `findFiles` settings to new `.ck.json` files created by `ck init`.
- Reduced CK Brain merge conflicts by writing new knowledge snippets to session-specific JSONL files instead of one shared `snippets.jsonl`.
- Updated recall/indexing to aggregate all `.jsonl` files under `.ck-knowledge/` in memory, including legacy `snippets.jsonl` files.

## Improvements

- `src/ContextKing.Core/SourceMap/CandidateSemanticReranker.cs`
  Adds in-memory semantic reranking over compact candidate cards built from path, file, type, and member metadata. Full source files are not read and candidate embeddings are not persisted.

- `src/ContextKing.Cli/Commands/FindFilesCommand.cs`
  Adds `--task` as optional reranking context, internal lexical overfetch, lexical fallback when reranking is unavailable, and richer `--explain` score components.

- `src/ContextKing.Cli/CkSettings.cs`
  Adds repository-level `findFiles` settings with semantic rerank enabled by default and bounded/clamped reranking defaults.

- `src/ContextKing.Cli/Commands/InitCommand.cs`
  New repos now receive explicit `findFiles` defaults in `.ck.json` so release behavior is visible and configurable from the start.

- `src/ContextKing.Core/Knowledge/KnowledgeStore.cs`
  Writes new snippets to `.ck-knowledge/sessions/<session-id>.jsonl`, reads every knowledge JSONL file under `.ck-knowledge/`, keeps legacy `snippets.jsonl` compatibility, and preserves per-file placement when deleting or backfilling metadata.

- `src/ContextKing.Core/Knowledge/KnowledgeIndexBuilder.cs`
  Rebuilds the knowledge index from an aggregate hash of all knowledge JSONL files rather than a single file.

- `hooks/ck-bash-guard.*`, `hooks/ck-search-guard.*`
  Guardrails now block direct access to any `.ck-knowledge/**/*.jsonl` file, not just the legacy single-file store.

- `README.md`, `skills/ck-find-files/SKILL.md`, `skills/ck-learn/SKILL.md`, `skills/ck-recall/SKILL.md`, `skills/ck-forget/SKILL.md`
  Updated user and agent-facing guidance for reranking and multi-file CK Brain storage.

## Tests

- Added reranker unit coverage for semantic promotion, flat semantic fallback, lexical grounding, must boosts, generic penalties, and candidate-card truncation.
- Added command coverage for `ck find-files --task`, explain output, reranker fallback, and semantic-disabled lexical ordering.
- Added knowledge store/index coverage for session JSONL writes, legacy-plus-session aggregation, deletion from the containing file, and aggregate index staleness.
- Added command coverage proving `ck learn` writes to a session-specific JSONL file and `ck recall --folder` reads snippets across all knowledge JSONL files.
