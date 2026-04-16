# Context King

Semantic code navigation toolkit for **Claude Code**, **Codex CLI**, and **OpenCode** on large **C#** codebases.

Most approaches to reducing token usage focus on *compacting* what the agent reads: tighter
prompts, summarised context, leaner encoding. Context King addresses a different problem: the
token cost of *getting there*. On a large codebase, navigating to the right method without
guidance means scanning many wrong files before finding the right one. That over-reading
during navigation dominates the total cost.

The goal is to reach the right method body in as few steps as possible, spending tokens only
on what is relevant while preventing over-reading at each step along the way. The same
principle applies to indexing: rather than embedding every file or symbol, Context King indexes
only at the folder level, where the signal-to-cost ratio is highest.

Replaces broad file searches with a three-tier navigation system:
semantic folder search →
live AST signature listing → 
targeted method extraction.

---

## The problem

A large C# solution has tens of thousands of files spread across thousands of folders.
When Claude Code needs to find and read a specific piece of logic, the typical path is:

1. Grep or Glob across the whole repo, returning dozens of hits across unrelated modules.
2. Scan candidate files, searching for keywords within them, reading the relevant sections.
   Still many files touched, and grep misses semantic relationships entirely.
3. Eventually find the right method, but only after pulling a lot of surrounding noise into
   the context window.

On a 20 000-file codebase this is expensive. Unscoped searches return false positives from
test projects, generated code, and unrelated modules. Every file that turns out to be wrong
wastes tokens and pushes relevant context out of the window.

---

## The solution

Context King installs four commands into your AI CLI tool:

| Command | What it does |
|---|---|
| `ck find-scope` | Semantic search over the folder tree. Returns the X most relevant folders |
| `ck signatures` | Live AST extraction. Lists every method/property signature in a set of files |
| `ck get-method-source` | Reads one named member using AST (method/constructor/property) and returns exact line+char spans |
| `ck index` | Builds or refreshes the semantic index (runs automatically on first use) |

### Why folder-level embeddings, not file-level or symbol-level

The obvious approach to semantic code search is to embed every file or every symbol. That does
not scale. A 20 000-file codebase would require 20 000 embedding passes at index time and
20 000 vectors to score at query time. That means minutes to build, slow queries, and heavy storage.

Context King makes a calculated trade-off: it embeds *leaf folders* (folders that directly
contain `.cs` files) rather than individual files. A 20 000-file repo has roughly 2 000-3 000
leaf folders. The index builds in under 15 seconds (Mac/Linux), fits entirely in memory for
scoring, and still captures the conceptual structure of the codebase. See
[How semantic matching works](#how-semantic-matching-works) for details on what goes into each
folder embedding.

The remaining precision comes from `ck signatures` (live AST, no index) and
`ck get-method-source` (targeted single-member extraction using AST), which operate at the file and
member level after the folder-level search has already narrowed the scope.

Together they form a five-step workflow that reaches the target method with far fewer tokens
than an unguided search:

```
1. ck find-scope  --query "order reservation inventory allocation"
      → 0.91  src/Modules/Inventory/Reservations/
         0.84  src/Modules/Inventory/Allocations/
         0.79  src/Modules/Orders/Fulfilment/
         0.71  src/Modules/Inventory/Reservations/Tests/
         ...   (up to --top N results, ranked by score)

2. Glob / Grep scoped to the top folder(s)
      → InventoryReservationService.cs, ReservationAllocator.cs, ...

3. ck signatures  InventoryReservationService.cs ReservationAllocator.cs
      → compact list of all signatures, ~1-2 lines each

4. ck get-method-source  InventoryReservationService.cs  AllocateReservation
      → just that method's body, with exact start_line / start_char / end_char

5. Read  (fallback only, when you need several members from the same file)
```

## Benchmark: "Describe retry handling and incremental support" on MassTransit (~5 500 files)

Two Claude Code sessions were run against the MassTransit open-source codebase (~5 500 C# files)
with the prompt "describe how retries of failed messages is handled and if there is incremental
support", differing only in whether Context King was active.

**Navigation strategy**

| | With Context King | Without Context King |
|---|---|---|
| `.cs` files read in full | **1** | 43 |
| Repo-wide Glob/Grep/Bash searches | 0 | 7 |
| `ck find-scope` calls | 1 | - |
| `ck signatures` calls | 2 | - |
| `ck get-method-source` calls | 3 | - |
| Total tool calls | 11 | 54 (1 outer + 53 via sub-agent) |

**Token cost** (new tokens = uncached input + cache creation)

| | With Context King | Without Context King |
|---|---|---|
| New tokens processed | **21,283** | 97,937 |
| Ratio | 1× | **4.6× more** |

The no-CK session delegated navigation to an Explore sub-agent, which internally read 43 full
`.cs` files, accumulating 84,000 cache-creation tokens in the sub-agent alone. Without CK,
delegating navigation to a sub-agent amplifies the token cost rather than reducing it.

The CK session used 2 `ck signatures` passes (compact member lists) and 3 batched
`ck get-method-source` calls, reading one `.cs` file in full as a fallback, spending tokens
only on the specific methods relevant to retry handling.

---

## Benchmark: cross-module analysis on a proprietary codebase (~20 000 files)

Two Claude Code sessions were run against a proprietary C# codebase (~20 000 files) with a
cross-module analysis prompt, differing only in whether Context King was active.

**Navigation strategy**

| | With Context King | Without Context King |
|---|---|---|
| `.cs` files read in full | **0** | 9 |
| Repo-wide Glob/Grep searches | 0 | 2 |
| `ck find-scope` calls | 2 | - |
| `ck signatures` calls | 2 | - |
| Total tool calls | 5 | 20 |

**Token cost** (new tokens = uncached input + cache creation)

| | With Context King | Without Context King |
|---|---|---|
| New tokens processed | **22,280** | 234,842 |
| Ratio | 1× | **10.5× more** |

The CK session read zero `.cs` files in full. Two `ck find-scope` passes identified the
relevant folders directly; two `ck signatures` passes confirmed member lists without opening
any file body, completing the task in just 5 tool calls.

---

## How semantic matching works

### Folder-level index

Rather than indexing individual files, Context King indexes *leaf folders*: any folder that
directly contains `.cs` files. A 20 000-file repo typically has 2 000–3 000 leaf folders.

Each folder's embedding is built from the combined tokens of:
- Its full path from the repo root (e.g. `src modules inventory reservations allocator`)
- All `.cs` filenames it contains (e.g. `inventory reservation service reservation allocator`)
- All public method names from the `.cs` files (e.g. `AllocateReservation ReleaseReservation`)

PascalCase identifiers in paths and filenames are split at case boundaries
(`InventoryReservationService` → `inventory reservation service`). Interface prefixes are
stripped (`IReservationAllocator` → `reservation allocator`). Public method names are added
as-is, without splitting. `AllocateReservation` stays as a single token so it can be matched
exactly by queries that use the method name. The result is a bag-of-words token string fed
into the embedding model.

### Embedding model

Embeddings use **BGE-small-en-v1.5** running locally via ONNX Runtime with no network calls,
no API keys. The model produces 384-dimensional float vectors. Each vector is L2-normalised so
cosine similarity reduces to a dot product.

The index stores 2 000–3 000 embeddings, all loaded into memory for scoring. An in-memory
cosine similarity pass across all embeddings completes in milliseconds.

### Hybrid scoring

The final score combines semantic and exact-match signals:

```
score = cosine_similarity(query_embedding, folder_embedding)
      + 0.30 × (query_terms_found_in_folder_tokens / total_query_terms)
```

Semantic similarity is the primary driver. The exact-match bonus (capped at 0.30) acts as
a tiebreaker when multiple folders are semantically close. A folder whose path literally
contains words from the query ranks slightly higher than one that is only conceptually similar.

The bonus is uniform across path segments and filenames: a query term found only in a filename
contributes the same as one found in the folder path. This means a query like
`"order reservation inventory"` will surface a folder named `Allocation/` that contains
`InventoryReservationProcessor.cs` just as well as a folder named `Inventory/Reservations/`.

### Staleness detection

The index is keyed by the SHA-256 fingerprint of the file paths and their content hashes
in each folder, not by git HEAD. A folder is re-embedded when any `.cs` file in it changes
(add, remove, rename, or content modification). This ensures that changes to public method
signatures, which are part of the folder's bag of words, are always reflected in the index.

The live working tree is reflected: untracked new files and working-tree deletions are included,
not just committed state.

---

## Installation

### Requirements

- .NET 10 runtime (required for AST analysis)
- At least one of: Claude Code, Codex CLI, or OpenCode, already initialized in the target repo
- Bash (Mac/Linux) or PowerShell 7+ (Windows)
- Git (used for repo root detection and worktree isolation)

### Quick install (recommended)

See the [latest release](https://github.com/Fredrik-C/ContextKing/releases/latest) for
platform-specific installation instructions and downloads.

The installer auto-detects which AI CLI tools are configured in the target repo
(`.claude/` → Claude Code, `.codex/` → Codex CLI, `.opencode/` → OpenCode) and deploys
support only for the ones present. Initialize at least one CLI tool in your repo first,
then run the install command from the root of the repository where you want Context King
installed.

---

## What gets deployed

The deploy script detects which AI CLI tools are configured in the target repo and installs
support for each one found. Deployment is per-CLI-tool:

**Claude Code** (detected by `.claude/` directory):
```
<target-repo>/.claude/
├── models/bge-small-en-v1.5/       ← embedding model (ONNX, ~34 MB)
├── skills/
│   ├── ck/                          ← ck binary + platform wrapper
│   ├── ck-find-scope/SKILL.md
│   ├── ck-signatures/SKILL.md
│   ├── ck-get-method-source/SKILL.md
│   └── ck-index/SKILL.md
├── hooks/
│   ├── ck-read-guard.sh/.ps1        ← PreToolUse: prompts for ck signatures before .cs reads
│   ├── ck-search-guard.sh/.ps1      ← PreToolUse: prompts for ck find-scope before broad searches
│   └── agent-usage-guard.sh/.ps1    ← PreToolUse: injects CK protocol into sub-agent prompts
├── rules/ck-code-search-protocol.md ← always-apply rule
└── settings.json                    ← hook registration + tool allowlist
```

**Codex CLI** (detected by `.codex/` directory):
- Installs binaries, models, and skills to `~/.codex/` (global)
- Writes full protocol to `.codex/ck-code-search-protocol.md`
- Adds a short pointer entry to `AGENTS.md` at the repo root (Codex reads `AGENTS.md` for project-level instructions)

**OpenCode** (detected by `.opencode/` directory):
```
<target-repo>/.opencode/
├── models/bge-small-en-v1.5/       ← embedding model (ONNX, ~34 MB)
├── skills/ck/                       ← ck binary + platform wrapper
├── skills/ck-*/SKILL.md             ← skill docs (opencode paths)
├── plugin/ck-guards.ts              ← hook plugin (auto-loaded by OpenCode)
├── ck-code-search-protocol.md      ← full code search protocol
├── AGENTS.md                        ← short pointer to the protocol file
└── config.json                      ← tool allowlist
```

The index lives at `<repo-root>/.ck-index/` (gitignored, created on first use). Each git
worktree has its own database, keyed by a SHA-256 of the worktree root path.

---

### Deploy from a local clone

If you have cloned the Context King repository locally (e.g. for development or
customisation), you can deploy directly without downloading anything:

**Mac / Linux:**
```bash
bash scripts/deploy.sh /path/to/your-repo
```

**Windows:**
```powershell
pwsh scripts/deploy.ps1 -TargetRepo C:\path\to\your-repo
```

Both scripts detect `.claude/`, `.codex/`, and `.opencode/` in the target and deploy only
for the tools that are present. They are safe to re-run since all steps are idempotent.

To deploy for all supported tools regardless of detection:
```bash
bash scripts/deploy.sh /path/to/your-repo --all
```

### Per-tool deployers (advanced)

If you need to deploy for a single tool in isolation:

```bash
# Codex CLI only (installs to ~/.codex/)
bash scripts/deploy-codex.sh --target-repo /path/to/your-repo

# OpenCode only
bash scripts/deploy-opencode.sh /path/to/your-repo
```

### First use

No manual index build needed. On the first `ck find-scope` call the index is built
automatically. Progress is printed to stderr; stdout is silent until the build completes.

**Claude Code / OpenCode:**
```bash
.claude/skills/ck/ck find-scope --query "order reservation inventory allocation"
# or
.opencode/skills/ck/ck find-scope --query "order reservation inventory allocation"
```

**Codex CLI:**
```bash
${CODEX_HOME:-$HOME/.codex}/skills/ck/ck find-scope --query "order reservation inventory allocation"
```

On a 20 000-file repo the first build typically takes under 30 seconds.

---

## Enforcement

Context King enforces the navigation workflow through a combination of rules, hooks, and
instructions. The mechanism varies by CLI tool.

### Claude Code

Two complementary mechanisms keep the agent on the efficient path:

**Always-apply rule** (`rules/ck-code-search-protocol.md`), loaded automatically in every
session. Instructs the agent to run `ck find-scope` before any Glob, Grep, or Read when the
target folder is unknown; scope all searches to the returned folder(s); run `ck signatures`
before reading any `.cs` file; and never speculatively open a file.

**PreToolUse hooks** fire before tool calls, without blocking:
- **`ck-read-guard`** fires on every `.cs` file read. Reminds the agent to confirm it has
  run `ck signatures` and pre-fills the exact command for the file being opened.
- **`ck-search-guard`** fires on broad Glob or Grep calls. Reminds the agent to run
  `ck find-scope` first.
- **`agent-usage-guard`** fires when a sub-agent is launched for navigation. Injects the
  full CK code search protocol into the sub-agent's prompt so it uses CK tools natively.

### Codex CLI

The full code search protocol is written to `.codex/ck-code-search-protocol.md`. A short
pointer entry is appended to `AGENTS.md` at the repo root directing Codex to read it,
keeping the repo's own `AGENTS.md` uncluttered. Skills and the binary are installed to
`~/.codex/` globally.

### OpenCode

The full code search protocol is written to `.opencode/ck-code-search-protocol.md`. A short
pointer entry is added to `.opencode/AGENTS.md` directing OpenCode to read it, keeping
the file uncluttered for user-owned content. The `ck` binary is allowed via the
`tools.bash.allow` list in `.opencode/config.json`.

A TypeScript plugin (`plugin/ck-guards.ts`) is deployed to `.opencode/plugin/` and
auto-loaded by OpenCode on session start. It intercepts three patterns before they waste
tokens scanning the wrong files:

- **broad `glob` on `.cs` files**: fires when a glob pattern targets `.cs` across a
  path with 3 or fewer segments; throws with a redirect to `ck find-scope`.
- **broad `grep` on `.cs` files**: same depth check; throws with a redirect to `ck find-scope`.
- **`bash` grep on `.cs` files**: fires when a bash command contains `grep` targeting
  `.cs`; throws with the full three-step protocol.

A Read guard is not implemented for OpenCode: the plugin API only supports throw-to-block
with no warn-and-allow equivalent, which would cause the agent to loop on legitimate reads.
Protocol instructions in `AGENTS.md` handle read discipline instead.

---

## Building from source

Requires .NET 10 SDK.

```bash
# Build
dotnet build src/ContextKing.Cli/ContextKing.Cli.csproj -v q

# Publish for current platform
dotnet publish src/ContextKing.Cli/ContextKing.Cli.csproj \
  -c Release -r osx-arm64 -p:PublishSingleFile=true \
  -o skills/ck -v q

# Rename output to match platform convention
mv skills/ck/ContextKing.Cli skills/ck/ck-osx-arm64
chmod +x skills/ck/ck-osx-arm64 skills/ck/ck
```

Pre-built binaries for macOS (arm64, x64), Linux (x64), and Windows (x64) are published as
GitHub Release assets and rebuilt automatically by GitHub Actions on each tagged release.

---

## Commands reference

### `ck find-scope`

```
ck find-scope --query "<multi-keyword description>" [--top <n>] [--repo <path>]
```

Output: `<score>\t<relative-folder-path>`, one line per result, sorted by score descending.
Default `--top 10`. Auto-builds index on first call.

### `ck signatures`

```
ck signatures <file.cs> [file2.cs ...]
```

Output: `<filepath>:<line>\t<containingType>\t<memberName>\t<signature>`, one line per member.
Always live, no index required.

### `ck get-method-source`

```
ck get-method-source <file.cs> <member-name> [--type <TypeName>] [--mode <mode>]
```

Modes: `signature_plus_body` (default), `signature_only`, `body_only`, `body_without_comments`.

Output: JSON array. Each element includes `file`, `member_name`, `containing_type`,
`signature`, `mode`, `start_line`, `end_line`, `start_char`, `end_char`, `content`.

### `ck index`

```
ck index [--status] [--force] [--repo <path>]
```

`--status` prints `fresh`, `stale`, or `missing`. Normally not needed since `ck find-scope`
triggers an incremental update automatically when the index is stale.

---

## License

Context King is licensed under **CC BY-NC-SA 4.0**. You are free to use it in any
organisation, commercial or non-commercial, copy it, and build on it, as long as you
attribute the original source and share any modifications under the same terms. Selling
or otherwise commercialising the tool itself is not permitted. See [LICENSE](LICENSE) for
the full terms.
