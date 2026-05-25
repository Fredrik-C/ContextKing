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

- Improved knowledge lifecycle management with schema-aware v2 metadata and lazy rollover during `ck recall`.
- Added freshness evaluation for snippets using content-based scope fingerprints, with explicit validity states: `fresh`, `review_needed`, and `unknown`.
- Added guardrails to prevent direct raw access to `.ck-knowledge/snippets.jsonl`; knowledge operations now flow through CK commands.

## Improvements

- `src/ContextKing.Core/Knowledge/KnowledgeFreshnessEvaluator.cs`
  New evaluator that backfills legacy snippets to schema v2 on recall, computes semantic scope hashes from tracked source files to detect scoped drift, and uses branch-agnostic freshness logic so branch-only changes do not force review.

- `src/ContextKing.Core/Knowledge/KnowledgeSnippet.cs`
  Extended snippet model with v2 lifecycle fields: `schema_version`, `validity` (`status`, `validated_at`, `confidence`), `anchors` (`files`, `symbols`), `fingerprints` (`semantic_scope_hash`, `anchor_signature_hash`, `context_hash`), and `origin` (`branch`, `head`).

- `src/ContextKing.Cli/Commands/RecallCommand.cs`
  Recall now refreshes and persists knowledge lifecycle metadata before returning results, prints snippet status in folder mode, and supports normalized folder overlap matching.

- `src/ContextKing.Cli/Commands/LearnCommand.cs`
  New snippets are created with `schema_version = 2`.

- `hooks/ck-bash-guard.sh`, `hooks/ck-bash-guard.ps1`
  Blocks direct shell-level reads/writes to `.ck-knowledge/snippets.jsonl` unless via CK commands.

- `hooks/ck-search-guard.sh`, `hooks/ck-search-guard.ps1`
  Blocks direct `Grep`/`Glob` access patterns targeting `.ck-knowledge/snippets.jsonl`.

- `skills/ck-recall/SKILL.md`, `skills/ck-learn/SKILL.md`, `rules/ck-code-search-protocol.md`
  Updated guidance to enforce command-mediated knowledge lifecycle operations.

## Tests

- Added `src/ContextKing.Tests/Knowledge/KnowledgeFreshnessEvaluatorTests.cs`.
- Covers legacy snippet backfill to schema v2.
- Covers freshness stability across branch-only changes.
- Covers `review_needed` transitions when scoped content changes.
