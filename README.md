# Context King

Code navigation toolkit for **Claude Code**, **Codex CLI**, and **OpenCode** on large **C#** and **TypeScript** codebases.

Most approaches to reducing token usage focus on compacting what the agent reads: tighter prompts, summarised context, leaner encoding. Context King addresses a different problem: the token cost of getting there. On a large codebase, navigating to the right method without guidance means scanning many wrong files before finding the right one, and that over-reading during navigation dominates the total cost.

The goal is to reach the right method body in as few steps as possible, spending tokens only on what is relevant. Context King indexes source files with lightweight lexical metadata and replaces broad file searches with a four-step navigation system:

```
file-first lexical retrieval -> optional metadata reranking -> scoped exploration -> live AST signature extraction -> targeted method extraction
```

---

## The problem

A large C# solution or TypeScript monorepo has tens of thousands of files spread across thousands of folders. When Claude Code needs to find a specific piece of logic, the typical unguided path is:

1. Grep or Glob across the whole repo, returning dozens of hits across unrelated modules.
2. Scan candidate files for keywords. Many files touched, and grep misses cross-module naming/context clues.
3. Eventually find the right method, but only after pulling a lot of surrounding noise into the context window.

On a 20,000-file codebase this is expensive. Unscoped searches return false positives from test projects, generated code, node_modules, and unrelated modules. Every wrong file wastes tokens and pushes relevant context out of the window.

---

## The solution

Context King installs these commands into your AI CLI tool:

| Command | What it does |
|---|---|
| `ck find-files` | Lexical file search over path/file/type/method names |
| `ck get-keyword-map` | Returns a seed→related keyword map from indexed results to refine broad queries |
| `ck expand-folder` | Scoped folder browser: enumerates source files and extracts signatures, with an optional regex filter |
| `ck signatures` | Live AST extraction. Lists every method/property signature in a set of files |
| `ck get-method-source` | Reads one named member using AST and returns exact line and char spans |
| `ck get-type-source` | Reads one C#/TS type declaration by name with exact line and char spans |
| `ck get-enum-members` | Lists enum members for a named C#/TS enum without reading the full file |
| `ck read-full-file` | Reads one full C#/TS file with a large-file guardrail and explicit override |
| `ck build-check` | Runs `dotnet build -v q` and prints compact diagnostics |
| `ck index` | Builds or refreshes the source-map index (runs automatically on first use) |
| `ck init` | Initializes Context King in a repository (see Installation) |
| `ck find-symbol` | Finds type or member declarations in C# and TypeScript/TSX files across a scoped path |
| `ck refs` | Finds textual references (call-sites) for a symbol across a scoped path |
| `ck recall` | Retrieves knowledge snippets for a folder or a cross-folder ranked query (step 2.5) |
| `ck learn` | Records a knowledge snippet to a session-specific `.ck-knowledge/**/*.jsonl` file |
| `ck forget` | Removes a stale snippet by ID |

The file-first flow in practice:

```
1. ck find-files --query "order reservation inventory allocation" --top 20 --path src/
      -> 0.71  src/Modules/Inventory/Reservations/InventoryReservationService.cs
         0.69  src/Modules/Inventory/Allocations/AllocationService.cs

2. ck get-method-source  src/Modules/Inventory/Reservations/InventoryReservationService.cs  AllocateReservation
      -> just that method body, with exact start_line / start_char / end_char

3. Edit
```

Fallback when file-first results are weak/noisy: `ck get-keyword-map` -> `ck find-files` -> `ck expand-folder`.

The biggest savings come during investigation: answering "where does X live?", tracing cross-module behaviour, scoping a refactor. A typical end-to-end task that mixes investigation and implementation saves around 30% in tokens overall.

---

## Benchmarks

### "Describe retry handling" on MassTransit (~5,500 files)

| | With Context King | Without Context King |
|---|---|---|
| `.cs` files read in full | **1** | 43 |
| Repo-wide Glob/Grep/Bash searches | 0 | 7 |
| `ck find-files` calls | 1 | - |
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
| `ck find-files` calls | 2 | - |
| `ck signatures` calls | 2 | - |
| Total tool calls | 5 | 20 |
| New tokens processed | **22,280** | 234,842 |
| Ratio | 1x | **10.5x more** |

The CK session read zero `.cs` files in full. Two `ck find-files` passes identified the relevant files; two `ck signatures` passes confirmed member lists without opening any file body.

---

## How file-first matching works

**File-level lexical index.** Context King indexes each source file (`.cs`, `.ts`, `.tsx`) as one row. A 20,000-file repo typically produces 15,000-20,000 indexed files. The index is incremental and updates only changed files.

Each indexed file stores normalized tokens from:

- Full relative path segments
- File name tokens
- Type names and method names/signatures extracted from source

PascalCase and camelCase identifiers are split at case boundaries. Symbol names are preserved in normalized token form so queries can match operation names, DTO/type names, and domain terms.

**Scoring.** `ck find-files` uses weighted lexical scoring across path/file/type/method fields, with term-coverage boosts and recency/scope heuristics. `--must` is a soft boost, not a hard filter, so the command can still return best-effort results when exact must terms are missing.

### Automatic candidate reranking

`ck find-files` uses lexical search as its first-stage retriever. Internally, CK may overfetch lexical candidates and rerank that small candidate set using compact metadata cards built from path, type, and member names.

This avoids maintaining a repository-wide semantic index while improving result precision for ambiguous searches. No full source files are read during reranking, and candidate embeddings are not persisted.

For tasks where code keywords are not enough to express intent, use `--task` to provide additional reranking context:

```bash
ck find-files "adyen terminal refund retry" --task "Find retry handling for terminal refunds after transient provider errors. Ignore card refunds."
```

**Staleness detection.** The index is keyed by file path + content fingerprint. A file row is refreshed when that file changes (add, remove, rename, content edit). Untracked new files and working-tree deletions are included, not just committed state.

---

## CK Brain — Institutional Memory

Navigation solves one problem: getting to the right code fast. But it doesn't help an agent understand what it finds. Every session starts from zero — no knowledge of why the code is structured the way it is, which modules are tricky, what was tried and rejected, what a domain concept actually means, or how two modules interact above the method level. An agent that can find the right file quickly is still slower than one that also knows what the team spent three weeks debugging in it.

CK Brain is the knowledge layer on top of the navigation layer. Where `ck find-files` answers "where is the code?", CK Brain answers "what do we know about that code?" — domain rules, architectural decisions, gotchas, cross-module relationships, anything a senior developer would tell a new joiner.

**How knowledge accumulates.** The brain starts empty and grows automatically. A hook runs after every agent turn and scans the new portion of the session transcript for code exploration signals: use of `ck find-files`, `ck signatures`, or `ck get-method-source` are strong signals; reading `.cs`/`.ts`/`.tsx` files, writing code, and running `ck recall` are moderate ones. Large investigation/edit windows can also trigger capture even without a strong signal. When enough signals are present, the hook injects a knowledge-capture prompt asking the agent to reflect on what it just discovered. The agent then calls `ck learn` to record the insight as a short snippet (2-4 sentences). CK writes new snippets to session-specific JSONL files under `.ck-knowledge/sessions/`, while recall reads every `.jsonl` file under `.ck-knowledge/` into one in-memory knowledge set. This keeps knowledge git-tracked and shared without forcing every session to append to the same file. Snippets are stored with schema-aware lifecycle metadata, and recall can lazily backfill older entries without breaking readability. Turns that involve no meaningful code exploration never trigger the prompt. No manual curation required.

After a dozen sessions on an active module, the brain holds the kind of contextual depth that normally takes months to accumulate, and it's available to every agent on every machine from the moment they pull the repo.

**How knowledge is retrieved.** Knowledge retrieval is step 2.5 in the navigation protocol: once an agent has confirmed the folder it will work in (via `ck expand-folder` or `ck signatures`), it runs `ck recall --folder <path>` before reading any method body. This returns all snippets associated with that folder, newest first. During recall, CK computes a folder-level semantic scope checksum/hash from current source content and compares it with each snippet's stored fingerprint to evaluate lifecycle status. Entries are flagged as `fresh`, `review_needed`, or `unknown`, with confidence metadata, so teams can rate reliability and focus review effort where code drift occurred. Only the snippets for the folder being worked in are surfaced, so sessions touching unrelated code pay no token cost for knowledge they won't use.

For questions that span multiple folders, `ck recall --query "<text>"` performs ranked lexical retrieval across all snippets. This is supplementary and optional; the folder-scoped recall at step 2.5 covers most cases.

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
curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash
```

**Windows:**
```powershell
irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex
```

This installs the `ck` binary to `~/.ck/bin/` and registers skills, hooks, and rules in `~/.claude/`, `~/.codex/`, `~/.config/opencode/`, and `~/.agents/`. After install, start a new shell or add `~/.ck/bin` to your PATH manually.
Windows equivalents use `%USERPROFILE%` (for example `%USERPROFILE%\\.ck\\bin\\ck.exe`, `%USERPROFILE%\\.claude\\`, `%USERPROFILE%\\.codex\\`, `%USERPROFILE%\\.agents\\`) and `%APPDATA%\\opencode\\`.

### 2. Initialize each repository (once per repo)

From the root of any repo you want to use Context King in:

```bash
ck init
```

This creates `.ck.json` (with a minimum version requirement and default `findFiles` reranking settings), adds `.ck-index/` to `.gitignore`, and creates the `.ck-knowledge/` directory. Commit these files to share the setup with your team.

### Migrating a legacy repo

If the repo was previously set up with the old per-repo `deploy.sh`, run:

```bash
ck init --migrate
```

This detects and removes per-repo artifacts (binary, hook scripts, rule file) and cleans up the relative-path hook registrations and CK `allowedTools` entries from `.claude/settings.json`, leaving all non-CK content intact.

---

## What gets installed

**Global (from `install-global.sh`):**
```
~/.ck/
  bin/ck                                     <- ck binary
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
Windows equivalents:
```text
%USERPROFILE%\.ck\
%USERPROFILE%\.claude\
%USERPROFILE%\.codex\
%APPDATA%\opencode\
%USERPROFILE%\.agents\
```

**Per-repo (from `ck init`):**
```
<repo-root>/
  .ck.json                                   <- version requirement (commit this)
  .ck-knowledge/                             <- knowledge base directory (commit this)
  .ck-index/                                 <- lexical source index (gitignored, built on first use)
```

---

## Uninstall

To reverse the global install, run the uninstaller from a cloned repo:

**Mac / Linux:**
```bash
bash scripts/uninstall-global.sh
```

**Windows:**
```powershell
pwsh scripts/uninstall-global.ps1
```

This removes the `~/.ck` tree (binary + model), strips `~/.ck/bin` from your PATH, deletes the CK skills/hooks/rules from `~/.claude/`, `~/.codex/`, `~/.config/opencode/`, and `~/.agents/`, and removes only the CK entries from each client's config files (`settings.json`, `hooks.json`, `opencode.json(c)`, Codex `AGENTS.md` / `config.toml`) — all other content is left intact.

Preview without changing anything with `--dry-run` (PowerShell: `-DryRun`). The same skip flags as the installer are supported (`--no-path`, `--no-claude`, `--no-codex`, `--no-opencode`, `--no-agents`, and the `--*-home` overrides).

**Per-repo files** created by `ck init` are **not** removed (they may be git-tracked). To clean a repository manually:
```bash
rm -rf .ck-index .ck-knowledge .ck.json
```

---

## Enforcement

Context King enforces the navigation workflow through rules, hooks, and skill instructions. The mechanism varies by CLI tool.

### Claude Code

**Always-apply rule** (`~/.claude/rules/ck-code-search-protocol.md`, Windows: `%USERPROFILE%\\.claude\\rules\\ck-code-search-protocol.md`), loaded automatically in every session. Instructs the agent to run `ck find-files` first for source discovery, then use folder-scope workflow (`get-keyword-map`/`find-files`/`expand-folder`) only as fallback.

**PreToolUse hooks** fire before every tool call:

- `ck-bash-guard`: blocks piping `ck find-files` output through head/grep/tail (which destroys ranking signals), and blocks running `grep` on known source files when `ck get-method-source` should be used instead.
- `agent-usage-guard`: injects the full CK code search protocol into every sub-agent's context via `additionalContext`, so sub-agents use CK tools natively instead of broad searches.

### Codex CLI / Agents

The full code search protocol is deployed to `~/.codex/ck-code-search-protocol.md` (Windows: `%USERPROFILE%\\.codex\\ck-code-search-protocol.md`). The inline 4-step workflow is injected into `~/.codex/AGENTS.md` (Windows: `%USERPROFILE%\\.codex\\AGENTS.md`) so the agent sees it on every session without needing to follow a pointer. The `project_doc_fallback_filenames` entry in `~/.codex/config.toml` (Windows: `%USERPROFILE%\\.codex\\config.toml`) ensures per-repo `ck-code-search-protocol.md` files are auto-loaded alongside `AGENTS.md` traversal.

Global install registers Codex hooks in `~/.codex/hooks.json` (Windows: `%USERPROFILE%\\.codex\\hooks.json`):
- `SessionStart` -> `ck-update-check`
- `PreToolUse` (`Bash`) -> `ck-bash-guard`
- `PostToolUse` (`Bash`) -> `ck-scope-hint`
- `Stop` -> `ck-postsession` (CK Brain capture prompt)

Codex hook interception is still partial by tool surface and version. For that reason, CK still keeps protocol guidance in AGENTS/rules and uses `ck read-full-file` as the explicit full-file path when targeted reads are insufficient.

### OpenCode

A TypeScript plugin (`ck-guards.ts`) is installed to `~/.config/opencode/plugins/` (Windows: `%APPDATA%\\opencode\\plugins\\`) and auto-loaded on session start. It enforces the protocol through both reactive guards and proactive hooks:

**Reactive guards** (fire before tool execution):
- Broad `glob` or `grep` on source files (3 or fewer path segments): redirects to `ck find-files` first (with `find-files` fallback guidance when needed).
- `bash cat` on source files: redirects to `ck get-method-source` or `ck read-full-file`.
- `bash grep` on source files: redirects to the three-step protocol.
- `bash` pipe on `ck find-files` output: blocks to preserve ranking signals and structure.

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

Initializes Context King in the current git repository. Creates `.ck.json` with default `findFiles` reranking settings, adds `.ck-index/` to `.gitignore`, and creates `.ck-knowledge/`. Use `--migrate` to remove legacy per-repo deploy artifacts and clean up `settings.json`.

### `ck find-files`

```
ck find-files "<query>" [--task <text>] [--must <text>] [--top <n>] [--min-score <f>] [--path <folder-or-file>] [--explain]
```

Lexical file retrieval using weighted term matching across path, file, type, and method fields.
`--must` applies soft score boosts (it does not hard-filter to zero results).
`--task` provides optional extra intent for candidate reranking; the lexical query still controls first-stage retrieval.
Output rows are `<score>\t<relative-file-path>`, optionally with compact score components when `--explain` is set.
Write `--query` as lexical code terms (path/file/type/member words), not natural-language questions.
Example: use `terminal refund adyen async` instead of `where is the refund logic implemented`.
Use this as the default discovery step.

### `ck get-keyword-map`

```
ck get-keyword-map --query "<multi-keyword description>" [--must <text>] [--top <n>] [--per-keyword <n>] [--repo <path>] [--verbose]
```

Builds a keyword neighborhood map from the top file-first results for your query. Output includes matched query keywords, unmatched query keywords, global keyword hints, and a per-seed map (`seed: related1, related2, ...`). Default `--per-keyword` is `12` with adaptive quality cut-off (returns fewer when signal is weak). The command also persists a session keyword atlas in `.ck-index/session-keyword-atlas.json`, which is reused by later refinements until direction shifts.
Use the same lexical query style as `find-files`: path/file/type/member words, not natural-language questions.

### `ck expand-folder`

```
ck expand-folder [--pattern <regex>] [--limit <n>] [--offset <n>] [--max-signatures <n>] [--all] <folder> [--repo <path>]
```

Enumerates every `.cs`, `.ts`, and `.tsx` file under `<folder>` recursively, extracts signatures, and filters to only files with a matching signature when `--pattern` is given. `\|` in the pattern is normalised to `|` automatically. Results are paged (`--limit`, `--offset`) and include pagination metadata on stderr. `--max-signatures` limits signatures printed per file (`0` = unlimited). Broad matches return a paged shortlist with narrowing hints; use `--all` only when broad output is intentional.

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

Finds type or member declarations in C# and TypeScript/TSX files. Uses `--path` roots when provided; otherwise falls back to the latest CK boundaries. Output: `<score>\t<file:line>\t<kind>\t<symbol>\t<container>\t<signature>`. Works on live disk content (uncommitted edits included).

### `ck refs`

```
ck refs <symbol> [--path <folder-or-file>] [--top <n>] [--ignore-case]
ck refs <symbol> <folder-or-file> [more paths...]
```

Finds textual references (call-sites) for a symbol in C# and TypeScript/TSX files. Uses identifier-boundary matching on the symbol's right-most segment. Uses `--path` roots when provided; otherwise falls back to the latest CK boundaries. Output: `<score>\t<file:line>\t<line snippet>`. Works on live disk content.

### `ck build-check`

```
ck build-check <project.csproj> [--max <n>] [--configuration <Debug|Release>] [--framework <tfm>] [--runtime <rid>] [--no-restore]
```

Runs `dotnet build -v q` and prints compact error/warning summaries.

### `ck index`

```
ck index [--status] [--force] [--repo <path>]
```

`--status` prints `fresh`, `stale`, or `missing`. Normally not needed since `ck find-files` triggers an incremental update automatically.

### `ck recall`

```
ck recall --folder <path> [--repo <path>]
ck recall --query <text> [--top <n>] [--repo <path>]
```

Retrieves knowledge snippets from all `.jsonl` files under `.ck-knowledge/`. `--folder` returns all snippets for a specific folder (no index), runs lifecycle validation, and prints status (`fresh`, `review_needed`, `unknown`) based on folder-scope hash comparison — this is step 2.5 in the navigation protocol. `--query` does a ranked cross-folder lookup and requires the knowledge index (auto-built). Silent when no snippets exist or when `"brain": false` is set in `.ck.json`.

### `ck learn`

```
ck learn --content "<text>" [--folders <f1,f2,...>] [--tags <t1,t2,...>] [--repo <path>]
```

Appends a snippet to a session-specific JSONL file under `.ck-knowledge/sessions/`. Keep content to 2-4 sentences of non-obvious insight: domain rules, architectural decisions and their reasons, gotchas, cross-module relationships. Omit anything derivable by reading the code. Silent when `"brain": false` is set in `.ck.json`.

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

To force-install a locally published dev binary (recommended for local testing):

```bash
dotnet publish src/ContextKing.Cli/ContextKing.Cli.csproj \
  -c Release -r osx-arm64 --self-contained false -v q \
  -o artifacts/publish/local

bash scripts/install-local-dev.sh
```

`install-local-dev.sh` first runs `install-global.sh` (baseline release assets), then force-overrides with your local binary. The script prints a version summary:
- baseline after `install-global.sh`
- local dev binary source
- final installed version

---

## Releasing

Use the release helper script to create/push a tag, wait for the GitHub release workflow, and then update release notes after release creation:

```bash
bash scripts/release.sh --tag v1.8.0 --notes-file RELEASE_NOTES_v1.8.0.md
```

Notes:
- Working tree must be clean before running.
- `--tag` can be omitted to auto-bump the latest patch version.
- Release notes are intentionally updated after the release object is created.

---

## License

Context King is licensed under **CC BY-NC-SA 4.0**. You are free to use it in any organisation, copy it, and build on it, as long as you attribute the original source and share modifications under the same terms. Selling or commercialising the tool itself is not permitted. See [LICENSE](LICENSE) for the full terms.
