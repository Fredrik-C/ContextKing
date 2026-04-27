# Context King

Semantic code navigation toolkit for **Claude Code**, **Codex CLI**, and **OpenCode** on large **C#** and **TypeScript** codebases.

Most approaches to reducing token usage focus on compacting what the agent reads: tighter prompts, summarised context, leaner encoding. Context King addresses a different problem: the token cost of getting there. On a large codebase, navigating to the right method without guidance means scanning many wrong files before finding the right one, and that over-reading during navigation dominates the total cost.

The goal is to reach the right method body in as few steps as possible, spending tokens only on what is relevant. Context King indexes at the folder level, where the signal-to-cost ratio is highest, and replaces broad file searches with a four-step navigation system:

```
semantic folder search -> scoped exploration -> live AST signature extraction -> targeted method extraction
```

---

## The problem

A large C# solution or TypeScript monorepo has tens of thousands of files spread across thousands of folders. When Claude Code needs to find a specific piece of logic, the typical unguided path is:

1. Grep or Glob across the whole repo, returning dozens of hits across unrelated modules.
2. Scan candidate files for keywords. Many files touched, and grep misses semantic relationships entirely.
3. Eventually find the right method, but only after pulling a lot of surrounding noise into the context window.

On a 20,000-file codebase this is expensive. Unscoped searches return false positives from test projects, generated code, node_modules, and unrelated modules. Every wrong file wastes tokens and pushes relevant context out of the window.

---

## The solution

Context King installs these commands into your AI CLI tool:

| Command | What it does |
|---|---|
| `ck find-scope` | Semantic search over the folder tree, returning the most relevant folders |
| `ck get-keyword-map` | Returns a seed→related keyword map from indexed results to refine broad queries |
| `ck expand-folder` | Scoped folder browser: enumerates source files and extracts signatures, with an optional regex filter |
| `ck signatures` | Live AST extraction. Lists every method/property signature in a set of files |
| `ck get-method-source` | Reads one named member using AST and returns exact line and char spans |
| `ck get-type-source` | Reads one C#/TS type declaration by name with exact line and char spans |
| `ck get-enum-members` | Lists enum members for a named C#/TS enum without reading the full file |
| `ck read-full-file` | Reads one full C#/TS file with a large-file guardrail and explicit override |
| `ck build-check` | Runs `dotnet build -v q` and prints compact diagnostics |
| `ck index` | Builds or refreshes the semantic index (runs automatically on first use) |
| `ck init` | Initializes Context King in a repository (see Installation) |
| `ck find-symbol` | Finds type or member declarations in C# and TypeScript/TSX files across a scoped path |
| `ck refs` | Finds textual references (call-sites) for a symbol across a scoped path |
| `ck recall` | Retrieves knowledge snippets for a folder or a cross-folder semantic query (step 2.5) |
| `ck learn` | Records a knowledge snippet to `.ck-knowledge/snippets.jsonl` |
| `ck forget` | Removes a stale snippet by ID |

The four steps in practice:

```
1. ck find-scope  --query "order reservation inventory allocation"
      -> 0.91  src/Modules/Inventory/Reservations/
         0.84  src/Modules/Inventory/Allocations/
         0.79  src/Modules/Orders/Fulfilment/

2. ck expand-folder  --pattern "Reserv"  src/Modules/Inventory/Reservations/
      -> InventoryReservationService.cs
           AllocateReservation(ReservationRequest request): Task<ReservationResult>
           ReleaseReservation(Guid reservationId): Task

3. ck get-method-source  InventoryReservationService.cs  AllocateReservation
      -> just that method body, with exact start_line / start_char / end_char

4. Edit
```

`ck signatures <folder>` is an alternative to step 2 only for small folders or when broad output is intentional (`--all`). For large folders it uses adaptive relevance ranking by default; pass `--all` to force full output. Prefer `ck expand-folder --pattern` when you have any keyword.

The biggest savings come during investigation: answering "where does X live?", tracing cross-module behaviour, scoping a refactor. A typical end-to-end task that mixes investigation and implementation saves around 30% in tokens overall.

---

## Benchmarks

### "Describe retry handling" on MassTransit (~5,500 files)

| | With Context King | Without Context King |
|---|---|---|
| `.cs` files read in full | **1** | 43 |
| Repo-wide Glob/Grep/Bash searches | 0 | 7 |
| `ck find-scope` calls | 1 | - |
| `ck signatures` calls | 2 | - |
| `ck get-method-source` calls | 3 | - |
| Total tool calls | 11 | 54 |
| New tokens processed | **21,283** | 97,937 |
| Ratio | 1x | **4.6x more** |

The no-CK session delegated navigation to an Explore sub-agent, which internally read 43 full `.cs` files and accumulated 84,000 cache-creation tokens in the sub-agent alone. Delegating navigation to a sub-agent amplifies the token cost rather than reducing it.

### Cross-module analysis on a proprietary codebase (~20,000 files)

| | With Context King | Without Context King |
|---|---|---|
| `.cs` files read in full | **0** | 9 |
| Repo-wide Glob/Grep searches | 0 | 2 |
| `ck find-scope` calls | 2 | - |
| `ck signatures` calls | 2 | - |
| Total tool calls | 5 | 20 |
| New tokens processed | **22,280** | 234,842 |
| Ratio | 1x | **10.5x more** |

The CK session read zero `.cs` files in full. Two `ck find-scope` passes identified the relevant folders; two `ck signatures` passes confirmed member lists without opening any file body.

---

## How semantic matching works

**Folder-level index.** Rather than indexing individual files, Context King indexes leaf folders: folders that directly contain source files. A 20,000-file repo typically has 2,000-3,000 leaf folders. The index builds in under 15 seconds on Mac/Linux, fits entirely in memory for scoring, and still captures the conceptual structure of the codebase.

Each folder's embedding is built from three token sources:

- Its full path from the repo root (e.g. `src modules inventory reservations allocator`)
- All source filenames it contains (e.g. `inventory reservation service`)
- Public surface symbols from those files: type names, constructors, methods, properties/fields, enum members, and exported TypeScript declarations (e.g. `AllocateReservation PaymentType Refund`)

PascalCase and camelCase identifiers are split at case boundaries. Interface prefixes are stripped. Symbol names are included in both readable embedding text and exact-match tokens, so queries can use operation names, DTO/type names, or property names.

**Embedding model.** Embeddings use **BGE-small-en-v1.5** running locally via ONNX Runtime with no network calls and no API keys. The model produces 384-dimensional float vectors, L2-normalised so cosine similarity reduces to a dot product. Scoring 2,000-3,000 embeddings completes in milliseconds.

**Hybrid scoring.** The final score combines semantic and exact-match signals:

```
score = cosine_similarity(query_embedding, folder_embedding)
      + 0.30 x (query_terms_found_in_folder_tokens / total_query_terms)
      + must_provider_adjustment
      - folder_noise_penalty
```

Semantic similarity is the primary driver. The exact-match bonus (capped at 0.30) is a tiebreaker when folders are semantically close: a folder whose indexed symbols or path literally contain words from the query ranks slightly higher than one that is only conceptually similar. Very large or miscellaneous folders receive a small noise penalty so keyword-stuffed grab-bag folders do not outrank focused implementation folders by default. Use `--explain` to see the scoring breakdown.

**Staleness detection.** The index is keyed by a fingerprint of file paths and content hashes in each folder. A folder is re-embedded when any source file changes (add, remove, rename, or content edit). Untracked new files and working-tree deletions are included, not just committed state.

---

## CK Brain — Institutional Memory

Navigation solves one problem: getting to the right code fast. But it doesn't help an agent understand what it finds. Every session starts from zero — no knowledge of why the code is structured the way it is, which modules are tricky, what was tried and rejected, what a domain concept actually means, or how two modules interact above the method level. An agent that can find the right file quickly is still slower than one that also knows what the team spent three weeks debugging in it.

CK Brain is the knowledge layer on top of the navigation layer. Where `ck find-scope` answers "where is the code?", CK Brain answers "what do we know about that code?" — domain rules, architectural decisions, gotchas, cross-module relationships, anything a senior developer would tell a new joiner.

**How knowledge accumulates.** The brain starts empty and grows automatically. A hook runs after every agent turn and scans the new portion of the session transcript for code exploration signals: use of `ck find-scope`, `ck signatures`, or `ck get-method-source` are strong signals; reading `.cs`/`.ts`/`.tsx` files, writing code, and running `ck recall` are moderate ones. Large investigation/edit windows can also trigger capture even without a strong signal. When enough signals are present, the hook injects a knowledge-capture prompt asking the agent to reflect on what it just discovered. The agent then calls `ck learn` to record the insight as a short snippet (2-4 sentences) in `.ck-knowledge/snippets.jsonl` — a plain-text file in the repository, git tracked and shared across the team like any other source file. Turns that involve no meaningful code exploration never trigger the prompt. No manual curation required.

After a dozen sessions on an active module, the brain holds the kind of contextual depth that normally takes months to accumulate, and it's available to every agent on every machine from the moment they pull the repo.

**How knowledge is retrieved.** Knowledge retrieval is step 2.5 in the navigation protocol: once an agent has confirmed the folder it will work in (via `ck expand-folder` or `ck signatures`), it runs `ck recall --folder <path>` before reading any method body. This returns all snippets associated with that folder, newest first. Only the snippets for the folder being worked in are surfaced, so sessions touching unrelated code pay no token cost for knowledge they won't use.

For questions that span multiple folders, `ck recall --query "<text>"` does a semantic search across all snippets. This is supplementary and optional; the folder-scoped recall at step 2.5 covers most cases.

**Token cost.** Unlike the navigation commands, brain recall does add tokens to the context window — the snippets are injected as text. This is intentional: the value is precisely that the agent reads and reasons over that knowledge. Snippets are kept short (2-4 sentences) to keep the cost low. Sessions working on code with no associated knowledge pay nothing.

**Opt-out.** Set `"brain": false` in `.ck.json` to disable all brain commands (`recall`, `learn`, `forget`) for a repository. The post-session hook also respects this flag. All three commands exit silently, so the navigation workflow continues without changes.

---

## Installation

### Requirements

- .NET 10 runtime (required for C# AST analysis; TypeScript analysis uses bundled tree-sitter)
- At least one of: Claude Code, Codex CLI, or OpenCode
- Bash (Mac/Linux) or PowerShell 7+ (Windows)
- Git

### 1. Install globally (once per machine)

**Mac / Linux:**
```bash
curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.sh | bash
```

**Windows:**
```powershell
irm https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.ps1 | iex
```

This installs the `ck` binary to `~/.ck/bin/`, the embedding model to `~/.ck/models/`, and registers skills, hooks, and rules in `~/.claude/`, `~/.codex/`, `~/.config/opencode/`, and `~/.agents/`. After install, start a new shell or add `~/.ck/bin` to your PATH manually.

### 2. Initialize each repository (once per repo)

From the root of any repo you want to use Context King in:

```bash
ck init
```

This creates `.ck.json` (with a minimum version requirement), adds `.ck-index/` to `.gitignore`, and creates the `.ck-knowledge/` directory. Commit these files to share the setup with your team.

### Migrating a legacy repo

If the repo was previously set up with the old per-repo `deploy.sh`, run:

```bash
ck init --migrate
```

This detects and removes per-repo artifacts (binary, model, hook scripts, rule file) and cleans up the relative-path hook registrations and CK `allowedTools` entries from `.claude/settings.json`, leaving all non-CK content intact.

---

## What gets installed

**Global (from `install-global.sh`):**
```
~/.ck/
  bin/ck                                     <- ck binary
  models/bge-small-en-v1.5/                  <- embedding model (ONNX, ~34 MB)
~/.claude/
  skills/ck*/                                <- skill docs + binary wrapper
  hooks/ck-*.sh                              <- PreToolUse guards
  rules/ck-code-search-protocol.md           <- always-apply rule
  settings.json                              <- hook + permission registrations (merged)
~/.codex/
  skills/ck*/                                <- Codex skills
  hooks/ck-*.sh                              <- Codex guard scripts
  hooks.json                                 <- Codex hook registrations (merged)
  ck-code-search-protocol.md                 <- Codex protocol reference
~/.config/opencode/
  skills/ck*/                                <- OpenCode skills
  plugins/ck-guards.ts                       <- hook plugin
~/.agents/skills/ck*/                        <- generic agent skills
```

**Per-repo (from `ck init`):**
```
<repo-root>/
  .ck.json                                   <- version requirement (commit this)
  .ck-knowledge/                             <- knowledge base directory (commit this)
  .ck-index/                                 <- semantic index (gitignored, built on first use)
```

---

## Enforcement

Context King enforces the navigation workflow through rules, hooks, and skill instructions. The mechanism varies by CLI tool.

### Claude Code

**Always-apply rule** (`~/.claude/rules/ck-code-search-protocol.md`), loaded automatically in every session. Instructs the agent to run `ck find-scope` before any search when the target folder is unknown, scope all work to the returned folders, use filtered `ck expand-folder` before reading source files, and never speculatively open broad files.

**PreToolUse hooks** fire before every tool call:

- `ck-bash-guard`: blocks piping `ck find-scope` output through head/grep/tail (which destroys folder scores), and blocks running `grep` on known source files when `ck get-method-source` should be used instead.
- `agent-usage-guard`: injects the full CK code search protocol into every sub-agent's context via `additionalContext`, so sub-agents use CK tools natively instead of broad searches.

### Codex CLI / Agents

The full code search protocol is deployed to `~/.codex/ck-code-search-protocol.md`. The inline 4-step workflow is injected into `~/.codex/AGENTS.md` (global, idempotent) so the agent sees it on every session without needing to follow a pointer. The `project_doc_fallback_filenames` entry in `~/.codex/config.toml` ensures per-repo `ck-code-search-protocol.md` files are auto-loaded alongside `AGENTS.md` traversal.

Global install registers Codex hooks in `~/.codex/hooks.json`:
- `SessionStart` -> `ck-update-check`
- `PreToolUse` (`Bash`) -> `ck-bash-guard`
- `PostToolUse` (`Bash`) -> `ck-scope-hint`
- `Stop` -> `ck-postsession` (CK Brain capture prompt)

Codex hook interception is still partial by tool surface and version. For that reason, CK still keeps protocol guidance in AGENTS/rules and uses `ck read-full-file` as the explicit full-file path when targeted reads are insufficient.

### OpenCode

A TypeScript plugin (`ck-guards.ts`) is installed to `~/.config/opencode/plugins/` and auto-loaded on session start. It enforces the protocol through both reactive guards and proactive hooks:

**Reactive guards** (fire before tool execution):
- Broad `glob` or `grep` on source files (3 or fewer path segments): redirects to `ck find-scope`.
- `bash cat` on source files: redirects to `ck get-method-source` or `ck read-full-file`.
- `bash grep` on source files: redirects to the three-step protocol.
- `bash` pipe on `ck find-scope` output: blocks to preserve folder scores and structure.

**Proactive hooks** (shape the agent's behaviour before guards are needed):
- `tool.definition`: rewrites the descriptions of `grep` and `glob` that the model sees, prepending the CK protocol mandate so the model prefers CK tools without being blocked first.
- `experimental.chat.system.transform`: injects the full CK protocol at the top of the system prompt on every turn, ahead of all other instructions.
- `session.idle` event: if CK tools were used during a turn but `ck learn` was not called, writes a marker file. The next turn's system-prompt injection reads the marker and adds a reminder to run `ck learn` before finishing.

---

## Commands reference

### `ck init`

```
ck init [--force] [--quiet] [--migrate]
```

Initializes Context King in the current git repository. Creates `.ck.json`, adds `.ck-index/` to `.gitignore`, and creates `.ck-knowledge/`. Use `--migrate` to remove legacy per-repo deploy artifacts and clean up `settings.json`.

### `ck find-scope`

```
ck find-scope --query "<multi-keyword description>" [--must <text>] [--top <n>] [--min-score <f>] [--explain] [--verbose] [--repo <path>]
```

Output: `<score>\t<relative-folder-path>`, one line per result, sorted by score descending. Default `--top 10`. Auto-builds the index on first call.

When the top results are too broad or ambiguous, `find-scope` prints a narrowing instruction before the folder list. The diagnostic includes query keywords that matched, query keywords that did not match, and grouped hints from the too-wide scope: exact symbol candidates first, then provider/workflow buckets, then generic leftovers. Treat that as a request to rerun with more precise provider, workflow, DTO/type, or method words instead of expanding every returned folder.

### `ck get-keyword-map`

```
ck get-keyword-map --query "<multi-keyword description>" [--must <text>] [--top <n>] [--per-keyword <n>] [--repo <path>] [--verbose]
```

Builds a keyword neighborhood map from the top semantic folders for your query. Output includes matched query keywords, unmatched query keywords, global keyword hints, and a per-seed map (`seed: related1, related2, ...`). Default `--per-keyword` is `50` with adaptive quality cut-off (returns fewer when signal is weak). The command also persists a session keyword atlas in `.ck-index/session-keyword-atlas.json`, which is reused by later scope/expand refinements until the query direction shifts.

### `ck expand-folder`

```
ck expand-folder [--pattern <regex>] [--all] <folder> [--repo <path>]
```

Enumerates every `.cs`, `.ts`, and `.tsx` file under `<folder>` recursively, extracts signatures, and filters to only files with a matching signature when `--pattern` is given. `\|` in the pattern is normalised to `|` automatically. Broad unfiltered folders and broad pattern matches are refused unless `--all` is passed intentionally; refusal output includes keyword hints for making the pattern more precise.

### `ck signatures`

```
ck signatures <file> [file2 ...]
ck signatures [--all] <folder>
```

Supports `.cs`, `.ts`, and `.tsx` files. Output: `<filepath>:<line>\t<containingType>\t<memberName>\t<signature>`, one line per member. Always live, no index required. For large folders, adaptive relevance ranking is applied unless `--all` is passed.

### `ck get-method-source`

```
ck get-method-source <file> <member-name> [--type <TypeName>] [--mode <mode>]
```

Supports `.cs`, `.ts`, and `.tsx` files. Modes: `signature_plus_body` (default), `signature_only`, `body_only`, `body_without_comments`. Output: JSON array with `file`, `member_name`, `containing_type`, `signature`, `mode`, `start_line`, `end_line`, `start_char`, `end_char`, and `content`.

### `ck get-type-source`

```
ck get-type-source <file> <TypeName> [--kind <class|interface|struct|record|enum|type_alias>]
```

Supports `.cs`, `.ts`, and `.tsx` files. Output: JSON array with `file`, `type_name`, `kind`, `start_line`, `end_line`, `start_char`, `end_char`, and `content`.

### `ck get-enum-members`

```
ck get-enum-members <file> <EnumName>
```

Supports `.cs`, `.ts`, and `.tsx` files. Output: JSON object with `file`, `enum_name`, `start_line`, `end_line`, and `members`.

### `ck read-full-file`

```
ck read-full-file <file> [--max-lines <n>] [--allow-large]
```

Reads a full `.cs` / `.ts` / `.tsx` file. By default it refuses files above 300 lines and points to targeted commands (`get-method-source`, `get-type-source`, `get-usings`, etc). If full context is truly required, rerun with `--allow-large`.

### `ck find-symbol`

```
ck find-symbol <symbol> [--path <folder-or-file>] [--kind type|member] [--top <n>]
ck find-symbol <symbol> <folder-or-file> [more paths...]
```

Finds type or member declarations in C# and TypeScript/TSX files. Uses `--path` roots when provided; otherwise falls back to scoped folders from the latest `ck find-scope`. Output: `<score>\t<file:line>\t<kind>\t<symbol>\t<container>\t<signature>`. Works on live disk content (uncommitted edits included).

### `ck refs`

```
ck refs <symbol> [--path <folder-or-file>] [--top <n>] [--ignore-case]
ck refs <symbol> <folder-or-file> [more paths...]
```

Finds textual references (call-sites) for a symbol in C# and TypeScript/TSX files. Uses identifier-boundary matching on the symbol's right-most segment. Uses `--path` roots when provided; otherwise falls back to scoped folders from the latest `ck find-scope`. Output: `<score>\t<file:line>\t<line snippet>`. Works on live disk content.

### `ck build-check`

```
ck build-check <project.csproj> [--max <n>] [--configuration <Debug|Release>] [--framework <tfm>] [--runtime <rid>] [--no-restore]
```

Runs `dotnet build -v q` and prints compact error/warning summaries.

### `ck index`

```
ck index [--status] [--force] [--repo <path>]
```

`--status` prints `fresh`, `stale`, or `missing`. Normally not needed since `ck find-scope` triggers an incremental update automatically.

### `ck recall`

```
ck recall --folder <path> [--repo <path>]
ck recall --query <text> [--top <n>] [--repo <path>]
```

Retrieves knowledge snippets from `.ck-knowledge/snippets.jsonl`. `--folder` returns all snippets for a specific folder (no index, always fresh) — this is step 2.5 in the navigation protocol. `--query` does a semantic cross-folder search and requires the knowledge index (auto-built). Silent when no snippets exist or when `"brain": false` is set in `.ck.json`.

### `ck learn`

```
ck learn --content "<text>" [--folders <f1,f2,...>] [--tags <t1,t2,...>] [--repo <path>]
```

Appends a snippet to `.ck-knowledge/snippets.jsonl`. Keep content to 2-4 sentences of non-obvious insight: domain rules, architectural decisions and their reasons, gotchas, cross-module relationships. Omit anything derivable by reading the code. Silent when `"brain": false` is set in `.ck.json`.

### `ck forget`

```
ck forget --id <uuid> [--repo <path>]
```

Removes a stale snippet by ID. Use when code described by a snippet has been refactored or the information is no longer accurate. Get the ID from `ck recall` output. Silent when `"brain": false` is set.

---

## Building from source

Requires .NET 10 SDK.

```bash
dotnet build src/ContextKing.Cli/ContextKing.Cli.csproj -v q

dotnet publish src/ContextKing.Cli/ContextKing.Cli.csproj \
  -c Release -r osx-arm64 -p:PublishSingleFile=true \
  -o skills/ck -v q

mv skills/ck/ContextKing.Cli skills/ck/ck-osx-arm64
chmod +x skills/ck/ck-osx-arm64 skills/ck/ck
```

Valid RIDs: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`.

Pre-built binaries for all platforms are published as GitHub Release assets and rebuilt automatically by GitHub Actions on each tagged release.

To install a local build globally (useful during development):

```bash
bash scripts/install-global.sh      # Mac / Linux
pwsh scripts/install-global.ps1     # Windows
```

---

## License

Context King is licensed under **CC BY-NC-SA 4.0**. You are free to use it in any organisation, copy it, and build on it, as long as you attribute the original source and share modifications under the same terms. Selling or commercialising the tool itself is not permitted. See [LICENSE](LICENSE) for the full terms.
