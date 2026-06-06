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

- CK Brain no longer depends on one shared knowledge file. New snippets are stored in scoped JSONL files under `.ck-knowledge/sessions/`, reducing merge conflicts when several agents or developers learn from the same repository.
- `ck learn` behavior has been tuned to be optional and high-signal. It should only record durable, non-obvious conclusions that future engineers cannot recover by reading the code.
- Knowledge capture prompts now explicitly discourage changelog-style snippets, implementation details, file paths, and symbol names.

## Improvements

- `.ck-knowledge/` storage
  Uses multiple JSONL files instead of a single shared `snippets.jsonl`, so parallel work is less likely to collide in git. Existing legacy knowledge remains readable.

- `ContextKing.Core/Knowledge/KnowledgeStore.cs`
  Resolves a stable session file from `CK_SESSION_ID` when available, otherwise falls back to the current git branch, detached HEAD, or a unique id. Separate `ck learn` invocations on the same branch append to the same branch-scoped file instead of creating one file per command.

- `ck recall` / knowledge indexing
  Reads knowledge across all JSONL files under `.ck-knowledge/`, so recall continues to behave like one knowledge base even though storage is split across files.

- `ck learn`
  Reframed knowledge capture as optional. The expected snippet is now 1-3 sentences describing the WHY, a gotcha, a cross-module relationship, or another durable conclusion. Most sessions should skip `ck learn`.

- `hooks/ck-postsession.*`, `plugins/ck-guards.ts`, `rules/ck-code-search-protocol.md`, `agents/explore.md`
  Updated prompts and protocol text to say that `ck learn` is optional, not mandatory, and that a prompt appearing is only a signal to consider whether anything is worth recording.

- `hooks/ck-bash-guard.*`
  Knowledge JSONL guardrails still block direct shell manipulation of `.ck-knowledge/**/*.jsonl`, but allow normal `git` operations such as diff, status, log, show, and add.

## Tests

- Added coverage that multiple `KnowledgeStore` instances without `CK_SESSION_ID` append to one branch-scoped knowledge file.
- Added coverage that the aggregate knowledge store still reads snippets from the split JSONL layout.
