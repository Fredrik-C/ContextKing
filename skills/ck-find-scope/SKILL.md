---
name: ck-find-scope
description: Semantic folder search for pure discovery when you don't have a keyword yet. If you have a keyword to search for, use ck-search instead — it combines scope + grep in one call.
---

# ck find-scope — Reference

## Syntax

```bash
ck find-scope --query "<multi-keyword description>" [--top <n>] [--min-score <f>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description — include domain, concept, operation, structural layer |
| `--top <n>` | 10 | Max folders returned. Removed when `--min-score` is set alone. |
| `--min-score <f>` | off | Score threshold — returns all folders above it. Check your score range first (typically 0.69–0.82). |
| `--repo <path>` | auto | Repo root |

## Output

```
<score>\t<relative-folder-path>
```

One line per folder. Scores are relative — use for ranking, not absolute confidence.

## Query tips

- Use 3–5 keywords: `"adyen terminal card-present refund"` not `"adyen"`
- Include structural terms: `"Catalog API controllers endpoints"` not just `"Catalog"`
- Synonyms produce the same ranking — don't rephrase, change vocabulary instead

## After this step

Pass the top folder to `ck signatures <folder>/` to list all members.

## Behaviour

- Auto-builds index on first call (~30s for large repos).
- Reflects live working tree (untracked files included).
