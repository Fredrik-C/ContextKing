---
name: ck-find-files
description: Semantic search to find the most relevant source files. Use this as the default first step before signatures or method extraction.
---

# ck find-files — Reference

Use this as the default entrypoint for source discovery.

## Syntax

```bash
.claude/skills/ck/ck find-files "<query>" [--must <text>] [--top <n>] [--min-score <f>] [--path <folder-or-file>] [--explain]
```

## What It Does

- Performs weighted lexical retrieval across indexed file metadata:
  path segments, file names, type names, and member/signature tokens.
- Returns ranked rows in the form:
  `<score>\t<relative-file-path>` (plus explain metadata when enabled).
- Triggers index refresh automatically when needed.

## Query Wording (Important)

- Write `--query` using lexical terms likely to exist in code:
  folder/path words, file-name words, type names, and method/member words.
- Prefer concrete identifiers and domain nouns/verbs over abstract intent phrasing.
- Use 3-7 high-signal terms (domain + workflow + operation/symbol).

Good:
- `terminal card-present refund adyen`
- `inventory reservation allocate async`
- `render invoice template`

Weak:
- `where is the refund logic implemented`
- `how does this feature work`
- `find code related to payments`

## Options

| Option | Description |
|---|---|
| `--must <text>` | Soft boost for required concepts (not a hard filter) |
| `--top <n>` | Number of ranked matches to return |
| `--min-score <f>` | Filter out low-confidence results |
| `--path <folder-or-file>` | Scope retrieval to a specific subtree |
| `--explain` | Include compact diagnostics (`types=<n> signatures=<n>`) |

## Typical Usage

```bash
.claude/skills/ck/ck find-files "order reservation inventory allocation" --top 20 --path src/
.claude/skills/ck/ck find-files "terminal refund adyen async" --must payment --top 15
```

## Protocol Placement

1. `ck find-files` (default first step)
2. If results are weak: `ck get-keyword-map` then rerun `ck find-files`
3. Use `ck expand-folder` only for fallback folder exploration
4. Move to `ck signatures` and `ck get-method-source` once target files are identified
