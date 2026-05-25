---
name: ck-recall
description: Retrieve institutional knowledge for a confirmed folder or a cross-folder semantic query. Step 2.5 in the navigation protocol — run after confirming a target folder, before reading method bodies.
---

# ck recall — Reference

Retrieves knowledge snippets: domain rules, architectural decisions, gotchas, and cross-module
relationships accumulated by previous sessions.

## When to use

- **After confirming a target folder** (post `ck expand-folder` or `ck signatures`) — always run
  `ck recall --folder <path>` before reading method bodies. This is step 2.5 in the protocol.
- **When you encounter unfamiliar domain behaviour** that isn't explained by the code structure.
- **Before starting work in a module you haven't touched this session.**

## Syntax

```bash
# Folder mode — primary usage
.claude/skills/ck/ck recall --folder <path>

# Cross-folder semantic search
.claude/skills/ck/ck recall --query "<text>" [--top <n>]
```

## Options

| Option | Description |
|---|---|
| `--folder <path>` | Return all snippets for this folder, newest first. No index required. |
| `--query <text>`  | Semantic search across all snippets. Auto-builds knowledge index. |
| `--top <n>`       | Max results for `--query` mode (default: 10) |
| `--repo <path>`   | Repo root (default: auto-detect) |

## Output

**Folder mode:**
```
[2026-04-22] id:550e8400  tags:interac,refund,terminal  status:fresh
Interac refunds require card-present because Interac's network rules mandate cardholder
authentication at the terminal. Unlike Visa/MC which support online refunds, Interac has no
offline refund path.
```

**Query mode:**
```
0.8234	id:550e8400
Interac refunds require card-present...
```

Silent (no output, exit 0) when no snippets exist — not an error. Also silent when the repo has `"brain": false` in `.ck.json`.

## Behaviour

- `--folder` reads directly from `.ck-knowledge/snippets.jsonl` and prints snippet freshness status (`fresh`, `review_needed`, `unknown`) from lazy validation metadata.
- `--query` uses the knowledge index (auto-built from snippets.jsonl on first use).
- If `.ck-knowledge/snippets.jsonl` does not exist yet, both modes exit silently.
