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

- New language additions: Kotlin (`.kt`, `.kts`) and Python (`.py`) support.
- Improved reliability for TypeScript, Kotlin, and Python code navigation commands.
- Fixed command failures caused by missing native parser runtime loading in some environments.
- Improved extraction accuracy for constructors and enum members across supported languages.

## Improvements

- `ck signatures`
  - More stable behavior for TypeScript files after parser-path hardening.

- `ck get-constructors`
  - Better constructor extraction coverage:
    - TypeScript: `constructor(...)`
    - Kotlin: primary and secondary constructors
    - Python: `__init__(...)`

- `ck get-enum-members`
  - Better enum handling in Kotlin and Python.
  - Python no longer reports ordinary classes as enums by mistake.

- Error handling and environment compatibility
  - Reduced parser runtime-load failures during command execution.
  - Clearer supported-extension messaging on unsupported file types.
