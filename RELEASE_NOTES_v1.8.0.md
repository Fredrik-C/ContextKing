## Highlights

- Replaced source-map file retrieval/indexing dependencies on embeddings with a lexical-first engine tuned for precision and speed.
- Made indexing dramatically faster by removing per-file embedding generation from the default CK navigation path.
- Reduced navigation noise and token waste in `ck get-keyword-map` with stronger filtering and concise output limits.

## Improvements

- `ck index`
  - Removed per-file embedding generation from source-map indexing.
  - Added timing breakdowns that separate scan, parse+embed pipeline wall time, parse accumulation, embed accumulation, persist, and total.
  - Kept file-first indexing focused on lexical fields (`path`, `file`, `type_names`, `method_names`).

- `ck find-files`
  - Replaced semantic vector ranking with weighted lexical ranking across `method`, `type`, `file`, and `path` fields.
  - Added IDF-style term weighting and query coverage bonuses.
  - Changed `--must` behavior from hard filtering to soft score boosts with fallback, preventing brittle zero-result loops.

- `ck get-keyword-map`
  - Switched retrieval backing to the file-first lexical search path.
  - Added aggressive noisy-token suppression for operational/config-heavy terms (for example feature-flag and long `enable*` style terms).
  - Limited output volume with concise top-N buckets:
    - lower default `--per-keyword` (12)
    - capped global hints and per-seed lists
    - capped role/evidence output lines

- File-first workflow and CLI surface alignment
  - Removed `find-scope` command path and aligned hooks/docs/protocol guidance to `find-files` first.
  - Updated command help and workflow messaging to reflect lexical file-first navigation.

- Internals and schema updates
  - Added/used dedicated lexical fields in file index rows (`type_names`, `method_names`) to support fast SQL-backed ranking.
  - Retained compatibility where needed while clearing folder-index maintenance for the file-first path.
