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

- Completed skill coverage for the full CK CLI command surface.
- Added dedicated skill docs for:
  - `ck find-files`
  - `ck forget`
- Improved agent guidance consistency so every primary CK command now has a matching `skills/ck-*` entry.

## Improvements

- `skills/ck-find-files/SKILL.md`
  - Added command reference, options, usage examples, and protocol placement for default file discovery.
  - Clarified that `ck find-files` is the default first step before signature/member extraction.

- `skills/ck-forget/SKILL.md`
  - Added command reference and lifecycle workflow for removing stale knowledge snippets by ID.
  - Documented relation to `ck recall` and `ck learn`, plus behavior when CK Brain is disabled.
