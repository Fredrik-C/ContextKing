# CK Brain — Semantic Memory for AI Agents

## The Problem

AI agents are day-one developers every session. They start with no institutional knowledge: no
understanding of why the code is structured the way it is, which modules are tricky, what was
tried and rejected, what domain concepts mean, or how features interact above the method level.

Context King already solves the navigation problem: given a task, find the right folder in seconds
without reading wrong files. But navigation alone doesn't make an agent effective — a new developer
who can find the right file is still slower than a senior developer who also knows *why* the code
is there, *what it shouldn't do*, and *what the team spent three weeks debugging in it*.

**CK Brain is the institutional knowledge layer on top of CK's navigation layer.**

Where `ck find-scope` answers *"where is the code?"*, CK Brain answers *"what do we know about
that code?"* — domain explanations, architectural decisions, gotchas, cross-module relationships,
and anything else a senior developer would tell a new joiner on their first day.

---

## How Knowledge Is Built Up

The brain starts empty and grows with every completed session. No manual curation required.

When a session ends, a hook prompts the agent to reflect on what it discovered: domain rules that
weren't obvious from the code, architectural decisions and their reasons, gotchas and constraints,
relationships between modules. Each insight is written as a snippet to `.ck-knowledge/snippets.jsonl`
— a plain text file in the repository.

The developer reviews the diff before committing, acting as a lightweight quality gate. Snippets
that are wrong, too specific, or not worth preserving are dropped. The rest travel with the next
commit and become part of the shared codebase.

Over time the effect compounds:

```
Session 1  → agent discovers Interac requires card-present, writes snippet
Session 2  → agent discovers AdyenTerminalService is the single entry point, writes snippet
Session 3  → agent encounters the settlement reconciliation gotcha, writes snippet
Session N  → every agent working in this area starts informed by all prior sessions
```

After a dozen sessions on an active module, the brain holds the kind of contextual depth that
normally takes a developer months to accumulate — and it's available to every agent, on every
machine, from the moment they pull the branch.

The knowledge is never injected blindly. It is retrieved by similarity against the current task
at the moment the agent navigates to the relevant module — so a session working on unrelated code
pays no token cost for knowledge it won't use.

---

## Why Not Always-Inject at Session Start?

Loading all accumulated knowledge into every session context would add tokens to every task,
including simple ones that don't need it. Instead, knowledge is surfaced on-demand: when the
agent navigates to a folder via `ck find-scope`, relevant knowledge snippets for those folders
are automatically appended to the navigation result. The agent gets institutional knowledge at
the moment it starts working in a module — not front-loaded for tasks that never touch it.

---

## Collective Knowledge at Team Scale

On a large team working on a monolith, institutional knowledge is currently siloed per developer.
CK Brain makes it shared and versioned: knowledge snippets are stored as a plain JSONL file
committed to the repository alongside the code. Every developer pulls the team's accumulated
knowledge on `git pull`. PRs include knowledge updates alongside code changes. Knowledge is
reviewed, attributed, and travels with branches — the same as any other source file.

The embeddings (used for similarity search and ranking) are derived locally from the JSONL file
and cached in the existing `.ck-index/` database. No central server, no sync mechanism, no
vendor lock-in. Git is the sync mechanism.

---

## Technical Specification

### Storage

**Source of truth: `.ck-knowledge/snippets.jsonl`**

One JSON object per line. Git-tracked. Append-mostly, so merges are usually clean (line-per-snippet
means concurrent additions on different branches don't conflict).

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "content": "Interac refunds require card-present because Interac's network rules mandate cardholder authentication at the terminal. Unlike Visa/MC which support online refunds, Interac has no offline refund path. This is why the Adyen terminal flow differs from the API-only path.",
  "tags": ["adyen", "interac", "refund", "terminal", "card-present"],
  "folders": ["src/Modules/Payments/Adyen/Terminal/"],
  "source": "agent",
  "session_id": "optional-session-ref",
  "created_at": "2026-04-22T10:30:00Z"
}
```

The `folders` field links knowledge to the code it describes, enabling scoped recall when
navigating specific modules.

**Derived cache: `.ck-index/<sha>.db` (existing database, new table)**

```sql
CREATE TABLE knowledge (
  id          TEXT PRIMARY KEY,
  content     TEXT NOT NULL,
  embedding   BLOB NOT NULL,   -- float32[], same dimensionality as folder embeddings
  tags        TEXT,            -- JSON array, for exact-match bonus
  folders     TEXT,            -- JSON array, for folder-scoped queries
  source      TEXT,
  created_at  TEXT
);

CREATE TABLE knowledge_meta (
  key   TEXT PRIMARY KEY,
  value TEXT
);
-- key = 'snippets_hash', value = sha256 of snippets.jsonl at last build
```

Staleness detection: hash the full content of `snippets.jsonl`. If the hash differs from
`knowledge_meta.snippets_hash`, rebuild the knowledge table. Full rebuild only (snippets are
small — hundreds to low thousands of entries — so incremental tracking is not worth the
complexity).

---

### New Core Module: `ContextKing.Core/Knowledge/`

**`KnowledgeIndexBuilder`**

Orchestrates: read `snippets.jsonl` → embed each snippet's content → upsert into SQLite knowledge
table → update `snippets_hash`. Reuses `BgeEmbedder` directly — no new embedding infrastructure.

**`KnowledgeStore`**

Reads and writes `snippets.jsonl`. Append-only writes (new snippets always go to the end).
Validates schema on read, skips malformed lines with a warning.

**`KnowledgeSearcher`**

Loads all knowledge embeddings into memory (same pattern as `SourceMapSearcher`). Scores using
the same hybrid formula:

```
score = cosine_similarity(query_embedding, snippet_embedding)
      + 0.30 × (query_terms_found_in_snippet_tags_and_folders / total_query_terms)
```

Supports optional `--folder` filter: restricts candidates to snippets whose `folders` field
overlaps with the given path prefix.

---

### New CLI Commands

**`ck recall`**

```
ck recall --query "<text>" [--top <n>] [--folder <path>] [--repo <path>]
```

Embeds the query, scores against the knowledge index, returns ranked snippets. Auto-builds the
knowledge index on first call if `snippets.jsonl` exists. Silent if no knowledge file exists yet.

Output format:
```
<score>\t<snippet-id>\t<content-preview-120-chars>
```

Full content written to stdout when `--top 1` or when a single result is returned, so the agent
can read the full text without a separate command.

**`ck learn`**

```
ck learn --content "<text>" [--tags <t1,t2,...>] [--folders <f1,f2,...>] [--repo <path>]
```

Appends a new snippet to `.ck-knowledge/snippets.jsonl`. Generates a UUID for the `id` field.
Sets `source` to `"human"` when called directly, `"agent"` when called from a hook.
Prints the new snippet ID on stdout.

---

### Hook Integration

**PostToolUse: knowledge surfacing piggybacked on `ck find-scope`**

The existing `ck-scope-hint` hook already fires after `ck find-scope`. Extend it to also run
`ck recall` scoped to the returned folders. If relevant snippets exist, append them to the hint
output block with a `## Knowledge` header. The agent gets navigation results and institutional
knowledge in a single hook response, with no extra commands required.

```
## Scope hint
Top folders: src/Modules/Payments/Adyen/Terminal/ (0.89), ...

## Knowledge
[0.84] Interac refunds require card-present because Interac's network rules mandate...
[0.76] AdyenTerminalService is the single entry point for all terminal interactions.
       Do not call the Adyen API directly from outside this module.
```

**PostSession: AI-extracted knowledge capture**

A new `ck-postsession` hook fires when a Claude Code session ends. It prompts the agent with a
structured system message to enumerate discoveries from the session:

- Domain concepts that weren't obvious from code alone
- Architectural decisions and their reasons
- Gotchas, invariants, or constraints discovered
- Relationships between modules that aren't visible from folder structure

The agent calls `ck learn` for each significant insight. The developer then reviews the diff to
`snippets.jsonl` before committing, acting as a quality gate on what gets canonised as team
knowledge. Knowledge that's wrong or too session-specific doesn't get committed.

Hook registration in `settings.json`:
```json
{
  "hooks": {
    "Stop": [{
      "matcher": "*",
      "hooks": [
        {"type": "command", "command": ".claude/hooks/ck-postsession.sh"},
        {"type": "command", "command": "bash -c 'command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-postsession.ps1 || exit 0'"}
      ]
    }]
  }
}
```

---

### New Skill Docs

**`skills/ck-recall/SKILL.md`** — instructs agents when and how to use `ck recall` explicitly
(for unfamiliar domain concepts, before starting work in a new module).

**`skills/ck-learn/SKILL.md`** — instructs agents how to call `ck learn` during or after a
session when they discover something worth preserving.

---

### Code Search Protocol Updates

`rules/ck-code-search-protocol.md` gains a **Knowledge** section:

- Knowledge snippets are automatically surfaced after `ck find-scope` via the scope-hint hook.
  Read them before proceeding to signatures.
- Use `ck recall --query "..."` explicitly when encountering unfamiliar domain concepts or
  behaviour that isn't explained by the code structure alone.
- Never call `ck learn` speculatively. Only record discoveries that a future agent working in
  this module would benefit from knowing.

---

### Setup

`install-global.sh` installs all hooks and skills globally. `ck init` (run once per repo) creates `.ck-knowledge/` (commit this to share knowledge with your team) and `.ck-index/` (gitignored, machine-local).

`.ck-knowledge/` is committed to the repo. `.ck-index/` remains gitignored.

---

### Collective Knowledge Workflow

```
Developer A works on Adyen terminal refunds
  → PostSession hook → agent appends 3 snippets to .ck-knowledge/snippets.jsonl
  → Developer A reviews diff, commits snippets alongside the code PR
  → Team pulls → all developers now have the Interac knowledge

Developer B starts a new session on the same module
  → runs ck find-scope --query "adyen terminal refund"
  → PostToolUse hook surfaces Developer A's snippets automatically
  → Developer B starts informed, not as a day-one developer
```

Knowledge is attributed via `git blame`. Outdated knowledge is removed via normal PRs. Branches
carry their own knowledge discoveries and merge them back with the code. The knowledge base grows
with the codebase, reviewed by the same people, under the same process.

---

### Implementation Phases

**Phase 1 — Core storage and retrieval**
- `KnowledgeStore`, `KnowledgeIndexBuilder`, `KnowledgeSearcher` in Core
- `ck recall` and `ck learn` CLI commands
- Knowledge index built into existing SQLite DB
- SKILL.md files for both commands

**Phase 2 — Hook integration**
- Extend `ck-scope-hint` to append knowledge after `ck find-scope`
- `ck-postsession` hook for AI-extracted capture
- Deploy script updates
- Protocol rule updates

**Phase 3 — Team workflow polish**
- `ck learn --from-session <file>` for batch import from session log
- `ck knowledge --status` (snippet count, last updated, index freshness)
- Deduplication pass: warn when new snippet is semantically close to an existing one
