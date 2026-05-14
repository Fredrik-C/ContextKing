## Install
Step 1 — global install (once per machine):

Mac / Linux:
`curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash`

Windows (PowerShell 7+):
`irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex`

Step 2 — initialize each repo (once per repo):
```
cd /path/to/your-repo
ck init
```

## Highlights

- Fixed Windows global installer execution when run directly via `irm ... | iex`.
- Removed a null script-path failure in local-repo detection for in-memory PowerShell execution.

## Improvements

- `scripts/install-global.ps1`
  - Hardened script path detection to support both file execution and piped invocation.
  - Added safe fallback from `$MyInvocation.MyCommand.Path` to `$PSCommandPath`.
  - Guarded parent path resolution so `Split-Path` is not called with null values.
