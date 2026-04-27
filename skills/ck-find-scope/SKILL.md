---
name: ck-find-scope
description: Semantic folder search — always step 1 (after ck get-keyword-map). Returns the folders you'll work in for the rest of the task.
---

# ck find-scope — Reference

Step 1 in the navigation protocol — run after `ck get-keyword-map`. Returns ranked folders that
become the source of truth for all subsequent work in the session.

## Syntax

```bash
.claude/skills/ck/ck find-scope --query "<multi-keyword description>" [--must "<provider>"] [--top <n>] [--min-score <f>] [--explain]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description — domain, concept, operation, structural layer |
| `--must <text>` | off | Provider/concept to focus on. Boosts folders containing this term; auto-penalises competing providers detected via embedding similarity — without needing to name them. Repeatable. |
| `--top <n>` | 10 | Max folders. Use 15–20 for broad tasks, 30 for impact analysis. |
| `--min-score <f>` | off | Score threshold — returns all above it. Check your range first (typically 0.69–0.82). |
| `--explain` | off | Adds scoring columns: semantic, exact, must, noise, files, tokens, matched terms, and hint terms. Use when results look broad/noisy. |
| `--verbose` | off | Prints index build/refresh progress. Default output is quiet for agent token efficiency. |
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

## What the index contains

Each folder is indexed from its full path, all source filenames, and public surface symbols:
type names, methods, constructors, properties/fields, enum members, and exported TypeScript
declarations. Together they describe what a folder exposes without reading any file body.
This means your query can include operation-level terms, DTO/property names, and type names.

## Query tips

- Use 3–5 keywords: `"adyen terminal card-present refund"` not `"adyen"`
- Include structural terms: `"Catalog API controllers endpoints"` not just `"Catalog"`
- Include operation terms when known: `"AllocateReservation inventory"` will match a folder
  that contains a method by that name, even if the folder path says nothing about allocation
- Synonyms produce the same ranking — never rephrase, change vocabulary instead
- Use `--must` when working with one provider in a multi-provider codebase:
  ```
  ck find-scope --query "card-present refund terminal payment" --must "adyen"
  ck find-scope --query "card-present refund terminal payment" --must "stripe"
  ```
  Each call returns the provider's own folders plus shared neutral infra, without
  the other provider's folders bleeding in.

## After this step

**Folders returned here are the source of truth for the session.** grep, glob, signatures, and
file reads all happen within these folders. Do not search outside them. Do not re-run find-scope
with rephrased queries.

Use `.claude/skills/ck/ck expand-folder --pattern "<2-4 high-signal terms>" <folder>/` while the
area is still uncharted. grep/rg/glob are also freely allowed within the returned folders. Once a
concrete file is known, prefer `ck signatures <file>` + `ck get-method-source` and stop using
`expand-folder` until you explicitly re-scope. Budget: max 3 `expand-folder` calls and max 1
folder-level `ck signatures` call per direction.

If output starts with `Scope is too broad or ambiguous`, use the printed hints to tighten the
query with provider + workflow + symbol/DTO words, or add `--must`. The keyword terms from the
`ck get-keyword-map` call you ran in step 0 are the right source for refinement.

## Behaviour

- Auto-builds index on first call (~30s for large repos).
- Reflects live working tree (untracked files included).
- Large `--top` values are okay for impact analysis, but for feature work prefer `--top 10` or `--top 15`.
- If a top result is a grab-bag folder, rerun with `--explain` and prefer focused folders with lower `files`, lower `tokens`, and lower `noise`.
