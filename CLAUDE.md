# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

**Build:**
```bash
dotnet build src/ContextKing.Cli/ContextKing.Cli.csproj -v q
```

**Test (target by class name):**
```bash
dotnet test src/ContextKing.Tests/ContextKing.Tests.csproj --filter "SourceMapBuilderTests" -v q
```

**Publish single-file binary for current platform (example: osx-arm64):**
```bash
dotnet publish src/ContextKing.Cli/ContextKing.Cli.csproj \
  -c Release -r osx-arm64 -p:PublishSingleFile=true \
  -o skills/ck -v q
mv skills/ck/ContextKing.Cli skills/ck/ck-osx-arm64
chmod +x skills/ck/ck-osx-arm64 skills/ck/ck
```
Valid RIDs: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`

**Run CLI locally (from repo root):**
```bash
dotnet run --project src/ContextKing.Cli -- find-files --query "reservation allocation"
```

## Architecture

The solution has three projects:

- **`ContextKing.Cli`** — Thin command dispatcher (`Program.cs` routes to `Commands/`). Each command is a static `RunAsync` method. `ModelLocator.cs` resolves the BGE model path relative to the binary.
- **`ContextKing.Core`** — All business logic, split into four modules (see below).
- **`ContextKing.Tests`** — xUnit tests, mirroring Core's module structure. `Helpers/TempRepo.cs` creates temporary git repos for integration tests; `Helpers/TestEmbedder.cs` provides a deterministic fake embedder.

### Core modules

**`SourceMap/`** — Semantic indexing pipeline

`SourceMapBuilder` orchestrates: git enumeration → tokenization → embedding → SQLite storage. Key behaviour:
- Index lives at `.ck-index/<sha256-of-worktree-path>.db` (one DB per worktree).
- Staleness is detected by a fingerprint of `{branch-name}:{sorted-cs-filename-set}`. Content-only edits never trigger a rebuild.
- On `BuildOrUpdateAsync`, only folders whose filename set changed since last build are re-embedded — unchanged folders are skipped.
- `Test`, `Tests`, and `Specs` path segments are excluded from indexing by default (configurable via `excludeSegments`).

`SourceMapIndex` wraps SQLite (one table for folders with path, combined_tokens, embedding blob, filename hashes; one meta table for the state key).

`SourceMapSearcher` loads all embeddings into memory, scores with hybrid formula: `cosine_similarity + 0.30 × (matched_query_terms / total_query_terms)`.

**`Embedding/`** — Local ML inference

`BgeEmbedder` wraps BGE-small-en-v1.5 via ONNX Runtime: lazy session init, thread-safe, runs mean pooling + L2 normalisation. `WordPieceTokenizer` does BERT-style tokenisation from `vocab.txt`. No network calls, no API keys.

**`Ast/`** — Live Roslyn analysis (no caching, always reads from disk)

`SignatureExtractor` returns compact one-line signatures for every member in a file. `MethodSourceExtractor` returns the full source of a single named member, including exact `start_line`, `start_char`, `end_line`, `end_char` within the original file. `SymbolSearchCommon` powers `find-symbol` (declaration lookup) and `refs` (call-site lookup) — both walk Roslyn syntax trees across a scoped file set. All use `Microsoft.CodeAnalysis.CSharp` (Roslyn 4.10.0).

**`Git/`** — Working-tree enumeration

`GitTracker` calls the `git` CLI to list all tracked `.cs` files plus untracked ones, discovers the worktree root, and computes the state-key fingerprint used for staleness detection.

### Deployment artefacts

The `skills/`, `hooks/`, `plugins/`, `rules/`, `agents/`, and `models/` directories at the repo root are what gets installed globally — they are not part of the .NET build. Pre-built binaries (`skills/ck/ck-osx-arm64`, etc.) are committed here and rebuilt by CI on every push to `main` that touches `src/`.

`scripts/install-global.sh` (and `.ps1`) installs these artefacts globally into `~/.ck/`, `~/.claude/`, `~/.codex/`, `~/.config/opencode/`, and `~/.agents/` (Windows: `%USERPROFILE%\\.ck\\`, `%USERPROFILE%\\.claude\\`, `%USERPROFILE%\\.codex\\`, `%APPDATA%\\opencode\\`, `%USERPROFILE%\\.agents\\`). Per-repo activation is done with `ck init`.

- `hooks/` — shell guard scripts installed to `~/.claude/hooks/` and `~/.codex/hooks/` (Windows: `%USERPROFILE%\\.claude\\hooks\\` and `%USERPROFILE%\\.codex\\hooks\\`)
- `plugins/` — TypeScript plugin installed to `~/.config/opencode/plugins/` (Windows: `%APPDATA%\\opencode\\plugins\\`)
- `agents/` — OpenCode agent definitions installed to `~/.config/opencode/agents/` (Windows: `%APPDATA%\\opencode\\agents\\`)
- `rules/` — protocol reference installed to `~/.claude/rules/`, `~/.codex/ck-code-search-protocol.md`, and `~/.config/opencode/ck-code-search-protocol.md` (Windows: `%USERPROFILE%\\.claude\\rules\\`, `%USERPROFILE%\\.codex\\ck-code-search-protocol.md`, `%APPDATA%\\opencode\\ck-code-search-protocol.md`)

## CI/CD

**`build.yml`** — Triggers on pushes to `main` when `src/` changes. Builds all five platform binaries in parallel (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`) and uploads them as GitHub Actions artifacts.

**`release.yml`** — Triggers on `v*` tags. Builds binaries, packages per-platform archives, and creates a GitHub Release. Bump version in `Program.cs` (`PrintHelp` / `PrintVersion`) before tagging.

## Key constraints

- Target `.NET 10`, `C# 13 (latest)`, nullable enabled, implicit usings — all set in the `.csproj` files.
- The embedding model path is resolved at runtime by `ModelLocator` searching upward from the binary location. If you move the binary, the `models/bge-small-en-v1.5/` directory must be findable in an ancestor directory.
- `ck signatures` and `ck get-method-source` are always live — they never consult the index and are safe to call before indexing.
