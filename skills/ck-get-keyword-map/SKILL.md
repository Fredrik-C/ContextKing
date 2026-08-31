---
name: ck-get-keyword-map
description: Build a seed-to-related keyword map from indexed results. Use when file-first retrieval is weak/noisy and you need fallback folder scoping with ck find-files.
---

# ck get-keyword-map — Query Precision Helper

Use this for the **fallback scope path**, not as the default first step.
Default discovery is `ck find-files`; run `ck get-keyword-map` when you need to pivot to `ck find-files`.

## Syntax

```bash
.claude/skills/ck/ck get-keyword-map --query "<multi-keyword description>" [--must "<provider>"] [--top <n>] [--per-keyword <n>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | required | Multi-keyword description of the area you need |
| `--must <text>` | off | Required provider/concept focus (repeatable) |
| `--top <n>` | 8 | Number of top lexical files analyzed |
| `--per-keyword <n>` | 3 | Related terms kept per query seed |
| `--repo <path>` | auto | Repo root |
| `--verbose` | off | Prints index build/refresh progress |

## Query Wording (Important)

- Use lexical query terms that likely exist in indexed code metadata:
  path/folder words, file-name words, type names, and method/member words.
- Avoid natural-language questions; this command works best with concrete code-like tokens.
- Use 3-7 high-signal terms (domain + workflow + operation/symbol).

Good:
- `adyen terminal card-present refund`
- `inventory reservation allocate async`

Weak:
- `where is refund logic`
- `how does this feature work`

## Output shape

- `related-keyword-hints`: up to three type-name-weighted lexical terms
- `semantic-keyword-hints`: up to three terms from an on-demand rerank of six lexical candidates, when the local embedding model is available
- `suggested-next-step`: a safe copyable `find-files` command retaining the original query

## Usage pattern

1. Run `ck find-files --query "..." --task "..."` first
2. If file-first results are weak/noisy, run `ck get-keyword-map --query "..."`
3. Choose at most one or two useful hints; keep the original query terms and rerun `ck find-files --query "..." --task "..."`
4. Folders from `find-files` are source of truth for fallback exploration
