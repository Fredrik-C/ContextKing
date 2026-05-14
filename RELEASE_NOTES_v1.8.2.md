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

- Fixed Windows global install download on the latest release channel.
- Added compatibility fallback for older Windows archive naming.

## Improvements

- `scripts/install-global.ps1`
  - Updated Windows archive lookup to use the published artifact name `context-king-windows.zip`.
  - Added fallback to legacy artifact name `context-king-win-x64.zip`.
  - Improved failure message when neither known archive name is available.
