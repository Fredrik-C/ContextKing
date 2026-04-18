---
name: ck-find-scope
description: Semantic folder search — always the first step. Returns the folders you'll work in for the rest of the task.
---

# ck find-scope — Reference

The entry point for all code navigation. Returns ranked folders that become your working scope.

## Syntax

```bash
.claude/skills/ck/ck find-scope --query "<multi-keyword description>" [--top <n>] [--min-score <f>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description — domain, concept, operation, structural layer |
| `--top <n>` | 10 | Max folders. Use 15–20 for broad tasks, 30 for impact analysis. |
| `--min-score <f>` | off | Score threshold — returns all above it. Check your range first (typically 0.69–0.82). |
| `--repo <path>` | auto | Repo root |

## Output

```
<score>\t<relative-folder-path>
```

## Query tips

- Use 3–5 keywords: `"adyen terminal card-present refund"` not `"adyen"`
- Include structural terms: `"Catalog API controllers endpoints"` not just `"Catalog"`
- Synonyms produce the same ranking — never rephrase, change vocabulary instead

## After this step

**Commit to these folders.** Pass them to `.claude/skills/ck/ck signatures <folder>/` to list all members, then use `.claude/skills/ck/ck get-method-source` or `Read` within them. Use grep/rg within these folders freely.

Do not re-run find-scope with rephrased queries. Do not search outside these folders.

## Behaviour

- Auto-builds index on first call (~30s for large repos).
- Reflects live working tree (untracked files included).
- Large `--top` values are fine — the output is one line per folder.
