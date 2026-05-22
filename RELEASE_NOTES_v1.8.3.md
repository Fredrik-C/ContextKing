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

- Added pluggable language extractor architecture via `ILanguageExtractor` + `LanguageRegistry`.
- Added first-class Kotlin (`.kt`, `.kts`) and Python (`.py`) AST/CST support.
- Expanded tree-sitter based extraction to support TypeScript, Kotlin, and Python through a shared base extractor.

## Improvements

- `src/ContextKing.Core/ContextKing.Core.csproj`
  - Updated `TreeSitterLanguagePack` to `1.8.1` to align with the `Node` API used by tree-sitter extractors.

- `src/ContextKing.Core/Ast/LanguageRegistry.cs`
  - Added explicit tree-sitter extractor namespace import to ensure extractor registration compiles cleanly.

- `src/ContextKing.Cli/Program.cs`
  - Bumped CLI version to `1.8.3`.
