## Faster and more accurate `ck find-files`

This release improves the quality and speed of method-aware file retrieval, based on the changes merged in [pull request #11](https://github.com/Fredrik-C/ContextKing/pull/11).

- Document-frequency scoring is now case-insensitive for type and member names.
- Member-name matching rewards query terms that co-occur in the same member and dampens terms that merely repeat the file or type name.
- C# declarations without bodies, including interface methods, now provide method-ranking cards.
- Lexical, metadata, and method signals are spread consistently before fusion, so the configured ranking weights have their intended influence.
- Method cards are allocated round-robin across candidate files, improving reranking coverage while reducing the dense-mode card budget from 300 to 180.
- Method reranking uses more available CPU threads and caches card vectors in the repository's `.ck-index` SQLite store.
- The embedding cache is bounded by age and size, uses incremental vacuuming, and safely falls back to uncached operation if it is unavailable or corrupted.

The cache is transparent to existing repositories. Repeated searches can reuse card vectors; deleting `.ck-index/embeddings.db` is safe if the cache needs to be reset.

## Validation

- 360 tests passed, 1 skipped in the PR validation run.
- The merged changes added coverage for document frequency, member co-occurrence, interface carding, signal fusion, card allocation, and embedding-cache behavior.

## Install or upgrade

macOS / Linux:

```bash
curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash
```

Windows (PowerShell 7+):

```powershell
irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex
```
