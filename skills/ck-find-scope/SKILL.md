---
name: ck-find-scope
description: Semantic folder search — always the first step. Returns the folders you'll work in for the rest of the task.
---

# ck find-scope — Reference

The entry point for all code navigation. Returns ranked folders that become your working scope.

## Syntax

```bash
.claude/skills/ck/ck find-scope --query "<multi-keyword description>" [--must "<provider>"] [--top <n>] [--min-score <f>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description — domain, concept, operation, structural layer |
| `--must <text>` | off | Provider/concept to focus on. Boosts folders containing this term; auto-penalises competing providers detected via embedding similarity — without needing to name them. Repeatable. |
| `--top <n>` | 10 | Max folders. Use 15–20 for broad tasks, 30 for impact analysis. |
| `--min-score <f>` | off | Score threshold — returns all above it. Check your range first (typically 0.69–0.82). |
| `--repo <path>` | auto | Repo root |

## Output

```
<score>\t<relative-folder-path>
```

The score is a **relevance score** — higher means more relevant. It combines semantic similarity
with an exact-keyword match bonus. Scores are not percentages or probabilities; they are relative
values used for ranking. On large codebases they typically cluster in a narrow band (e.g. 0.69–0.82)
— a spread of 0.03–0.07 across 10 results is normal. Use them to rank folders against each other,
not as absolute confidence measures.

## Query tips

- Use 3–5 keywords: `"adyen terminal card-present refund"` not `"adyen"`
- Include structural terms: `"Catalog API controllers endpoints"` not just `"Catalog"`
- Synonyms produce the same ranking — never rephrase, change vocabulary instead
- Use `--must` when working with one provider in a multi-provider codebase:
  ```
  ck find-scope --query "card-present refund terminal payment" --must "adyen"
  ck find-scope --query "card-present refund terminal payment" --must "stripe"
  ```
  Each call returns the provider's own folders plus shared neutral infra, without
  the other provider's folders bleeding in.

## After this step

**Commit to these folders.** Pass them to `.claude/skills/ck/ck signatures <folder>/` to list all members, then use `.claude/skills/ck/ck get-method-source` or `Read` within them. Use grep/rg within these folders freely.

Do not re-run find-scope with rephrased queries. Do not search outside these folders.

## Behaviour

- Auto-builds index on first call (~30s for large repos).
- Reflects live working tree (untracked files included).
- Large `--top` values are fine — the output is one line per folder.
