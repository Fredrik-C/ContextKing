## Ad-hoc semantic keyword hints

- `ck get-keyword-map` now generates semantic hints by embedding only the query and six compact metadata cards from lexical file candidates. It no longer depends on unavailable folder embeddings in the file-first index.
- Semantic hints are optional: they are shown only when the local BGE model is available, with no repository-wide embedding pass or persistent embedding storage.
- Related and semantic hints favor type-name vocabulary over method names.

## Tuned discovery workflow

- `ck find-files` is now consistently the required first discovery step across the rules, skills, agent guide, README, and guards.
- `ck get-keyword-map` is a fallback for broad, weak, or vocabulary-mismatched file results. Agents retain the original query and select only one or two hints for a single refined retry.

## Install

Step 1 — global install (once per machine):

**Mac / Linux:**

```bash
curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash
```

**Windows (PowerShell 7+):**

```powershell
irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex
```

Step 2 — initialize each repository:

```bash
cd /path/to/your-repo
ck init
```

To uninstall, use the matching `uninstall-global.sh` or `uninstall-global.ps1` asset from the latest release.
