---
name: ck-get-keyword-map
description: Build a seed-to-related keyword map from indexed results. Always the first step — establishes the keyword source of truth before ck find-scope.
---

# ck get-keyword-map — Query Precision Helper

**Always run this first** — before `ck find-scope`. It establishes the keyword source of truth
for the session. Use the precision terms it returns in the `find-scope` query that follows.

## Syntax

```bash
.claude/skills/ck/ck get-keyword-map --query "<multi-keyword description>" [--must "<provider>"] [--top <n>] [--per-keyword <n>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description of the area you need |
| `--must <text>` | off | Required provider/concept focus (repeatable) |
| `--top <n>` | 12 | Number of top semantic folders analyzed |
| `--per-keyword <n>` | 50 | Related terms returned per seed keyword (adaptive: may return fewer when quality drops) |
| `--repo <path>` | auto | Repo root |
| `--verbose` | off | Prints index build/refresh progress |

## Output shape

- `matched-query-keywords`: query terms that were found in top folders
- `unmatched-query-keywords`: query terms not found in top folders
- `global-keyword-hints`: top hints from the current result scope
- `keyword-map`: per-seed related terms (`seed: t1, t2, ...`)

## Usage pattern

1. Run `ck get-keyword-map --query "..."` — keywords from output are source of truth
2. Run `ck find-scope --query "..."` using the precision terms from step 1
3. Folders from `find-scope` are source of truth — all subsequent work stays within them
