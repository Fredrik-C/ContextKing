---
name: ck-learn
description: Record a new knowledge snippet — domain rules, architectural decisions, gotchas, or cross-module relationships discovered during a session.
---

# ck learn — Reference

Appends a new snippet to `.ck-knowledge/snippets.jsonl`. Called by the session-end hook
(automated) and directly when you encounter something worth preserving mid-session.

## Syntax

```bash
.claude/skills/ck/ck learn \
  --content "<text>" \
  --folders "<folder1>,<folder2>" \
  --tags "<tag1>,<tag2>"
```

## Options

| Option | Description |
|---|---|
| `--content <text>`      | The knowledge to record (required) |
| `--folders <f1,f2,...>` | Comma-separated folder paths this snippet applies to |
| `--tags <t1,t2,...>`    | Comma-separated keywords for exact-match search |
| `--source <name>`       | Source label — defaults to `"agent"` |
| `--repo <path>`         | Repo root (default: auto-detect) |

## Output

The new snippet's UUID (stdout). Creates `.ck-knowledge/` if absent. Silent (exit 0) when the repo has `"brain": false` in `.ck.json`.

## What to record

**Record:** architectural patterns not obvious from folder structure, the WHY behind decisions,
gotchas and constraints, cross-module relationships that span folders.

**Do NOT record:**
- File paths — those go in `--folders`, not in `--content`
- Function/method names found via `ck signatures` — an agent can find those in 1 call
- Internal helper names, specific flag names, parameter types — implementation detail
- Anything a future agent would see immediately by reading the relevant file

## Length check

If `--content` is more than 4 sentences, you are including implementation detail. Cut until
only the non-obvious insight remains.

## Example

**Good:**
```bash
.claude/skills/ck/ck learn \
  --content "Interac refunds require card-present: Interac's network rules mandate terminal
authentication. Unlike Visa/MC, there is no offline refund path — this is why the terminal
flow is structurally different from the API-only path." \
  --folders "src/Modules/Payments/Adyen/Terminal/" \
  --tags "interac,adyen,refund,terminal,card-present"
```

**Bad** (same snippet, wrong):
```
--content "... renderComponent (path/to/renderComponent.tsx) calls getInitChainWithCheck(),
finds the DOM node via getMountElement, creates a React element, and pushes both into
backendComponentsStore (Zustand vanilla store in path/to/backendComponentsStore.ts) ..."
```
File paths in prose, internal helper names, Zustand store location — all findable via
`ck signatures`. Strip them; keep only the non-obvious architectural insight.
