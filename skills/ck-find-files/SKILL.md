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
| `--top <n>` | Number of ranked matches to return (defaults to 5) |
| `--min-score <f>` | Filter out low-confidence results |
| `--path <folder-or-file>` | Scope retrieval to a specific subtree |
| `--explain` | Include lexical/metadata/method scores, best member, and up to three evidence identifiers |
| `--verbose` | Report stage counts, failures, duration, and advisory task warnings |

## Typical Usage

```bash
.claude/skills/ck/ck find-files "order reservation inventory allocation" --task "Find inventory reservation allocation logic." --top 5 --path src/
.claude/skills/ck/ck find-files "terminal refund adyen async" --task "Find async terminal refund handling for Adyen." --must payment --top 5
.claude/skills/ck/ck find-files "adyen terminal refund retry transient" --task "Find terminal refund handling that retries after transient provider errors."
```

Start with the five-result default (or fewer when you have a specific target). Increase `--top` only after the initial shortlist is ambiguous; this keeps discovery output from crowding the agent context.

### Write `--task` as positive retrieval intent

Describe only the code and behavior you want to find. Avoid negations,
exclusions, and instructions about what to ignore; embedding-based reranking
may treat excluded concepts as relevant signals.

Good:

```bash
ck find-files "adyen terminal refund retry transient" \
  --task "Find terminal refund handling that retries after transient provider errors."
```

Avoid:

```bash
ck find-files "adyen terminal refund retry" \
  --task "Find retry handling for terminal refunds. Ignore card refunds."
```

When unwanted concepts must be excluded, omit them from both the lexical query
and `--task`. Narrow positively using provider, channel, operation, error type,
or expected behavior.

`--task` improves positive semantic matching; it does not implement exclusion
logic. Terms appearing in negative phrases such as "not", "exclude", or
"ignore" may still increase semantic similarity.

Method reranking is enabled by default and uses the local code model included in
standard installations. Set `findFiles.methodRerank=false` to opt out. It keeps file-level results,
does not persist method embeddings, and falls back when unavailable. Do not
interpret semantic similarity as proof of behavior; inspect the ranked code.

## Protocol Placement

1. `ck find-files` (default first step)
2. If results are weak: `ck get-keyword-map` then rerun `ck find-files`
3. Use `ck expand-folder` only for fallback folder exploration
4. Move to `ck signatures` and `ck get-method-source` once target files are identified
