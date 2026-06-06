---
name: ck-find-files
description: Source discovery over path, file, type, and member names. Use this as the default first step before signatures or method extraction.
---

# ck find-files — Reference

Use this as the default entrypoint for source discovery.

## Syntax

```bash
.claude/skills/ck/ck find-files "<query>" --task <text> [--must <text>] [--top <n>] [--min-score <f>] [--path <folder-or-file>] [--explain]
```

## What It Does

- Performs weighted lexical retrieval across indexed file metadata:
  path segments, file names, type names, and member/signature tokens.
- Requires task intent to improve the final ranked subset while keeping
  the lexical query as the retrieval anchor.
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
| `--task <text>` | Required task intent for candidate reranking context |
| `--top <n>` | Number of ranked matches to return |
| `--min-score <f>` | Filter out low-confidence results |
| `--path <folder-or-file>` | Scope retrieval to a specific subtree |
| `--explain` | Include compact diagnostics (`types=<n> signatures=<n>`) |

## Typical Usage

```bash
.claude/skills/ck/ck find-files "order reservation inventory allocation" --task "Find inventory reservation allocation logic." --top 20 --path src/
.claude/skills/ck/ck find-files "terminal refund adyen async" --task "Find async terminal refund handling for Adyen." --must payment --top 15
.claude/skills/ck/ck find-files "adyen terminal refund retry" --task "Find retry handling for terminal refunds after transient provider errors. Ignore normal card refund flows."
```

## Protocol Placement

1. `ck find-files` (default first step)
2. If results are weak: `ck get-keyword-map` then rerun `ck find-files`
3. Use `ck expand-folder` only for fallback folder exploration
4. Move to `ck signatures` and `ck get-method-source` once target files are identified
