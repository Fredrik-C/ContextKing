# Context King — AGENTS.md

## Project Overview
Context King (ck) is a semantic code navigation CLI tool for large codebases.
It provides AST-based signature extraction, method/type source retrieval, 
symbol search, embeddings-based file search, and source map indexing.

## Architecture
- **ContextKing.Core** — Core library: AST extractors, embeddings, source map, git helpers
- **ContextKing.Cli** — CLI entry point with command implementations
- **ContextKing.Tests** — xUnit test suite
- **plugins/** — VS Code / AI agent guard plugins (TypeScript)
- **skills/** — OpenHands/Claude skill definitions

## Language Support (Pluggable)
Languages are supported via `ILanguageExtractor` interface registered in `LanguageRegistry`:
- **C# (.cs)** — Roslyn-based via `CSharpRoslynExtractor`
- **TypeScript (.ts/.tsx)** — Tree-sitter via `TypeScriptExtractor` (TreeSitterLanguagePack)
- **Kotlin (.kt/.kts)** — Tree-sitter via `KotlinExtractor`
- **Python (.py)** — Tree-sitter via `PythonExtractor`

Tree-sitter extractors extend `TreeSitterExtractor<T>` abstract base class in `Ast/TreeSitter/`.

## Key Patterns
- Commands dispatch to `LanguageRegistry.Get(filePath)` to obtain the correct extractor
- C# always uses Roslyn; all other languages use TreeSitterLanguagePack (v1.8.0)
- `SupportedLanguages.cs` delegates to `LanguageRegistry` for extension checking
- All extractors implement `ILanguageExtractor` with methods: `ExtractSignatures`, `ExtractMethodSource`, `ExtractAllConstructors`, `GetAllMemberNames`, `ExtractPublicNamesFromFile`, `ExtractPublicNamesFromSource`, `ExtractTypeAndMethodNames`
- `SourceMapBuilder` uses `LanguageRegistry.Get()` for both signature and public name extraction

## Source-Code Discovery
- For source-code discovery, start with `ck find-files`. Do not use `rg`, `grep`, `find`, globbing, or broad directory listings to discover source files.
- Inspect `find-files` candidates with `ck signatures`, then use targeted CK source extraction such as `ck get-method-source` or `ck get-type-source` to read the relevant code. Use `ck find-symbol` and `ck refs` for declaration and usage lookup.
- Use `rg` for non-source text, exact literal searches, or questions CK cannot answer. Keep source-text fallbacks as narrow as practical.
- If the exact source file path is already known, go directly to CK signatures/source extraction for that file instead of rediscovering it.

## TreeSitterLanguagePack API Notes
- `Parser.Default()` → `SetLanguage("python"|"kotlin"|"typescript"|"tsx")` → `Parse(source)` returns `Tree?`
- `Tree.RootNode()` returns `Node` (non-nullable, throws on failure)
- `Node.Kind()` returns node type string (e.g. "class_declaration")
- `Node.ChildByFieldName("name")` returns `Node?` (null if missing)
- `Node.Child(uint index)` returns `Node?` (null if out of range)
- `Node.StartByte()`, `Node.EndByte()` return `ulong` byte offsets
- `Node.StartPosition()`, `Node.EndPosition()` return `Point` with `Row`/`Column` (0-indexed)

## Building
```bash
dotnet build src/ContextKing.Core/ContextKing.Core.csproj
dotnet build src/ContextKing.Cli/ContextKing.Cli.csproj
```

## Testing
```bash
dotnet test src/ContextKing.Tests/ContextKing.Tests.csproj
```
Tests are xUnit-based. Test files for TypeScript extractors exist in `ContextKing.Tests/Ast/TypeScript/`.

## Git
- Do NOT push to remote or create PRs unless explicitly asked
- Add `Co-authored-by: openhands <openhands@all-hands.dev>` to commits
