---
name: ck-expand-folder
description: List files in a folder with their signatures, filtered by an optional regex pattern. Use this in fallback folder-scoped exploration after ck find-files.
---

# ck expand-folder — Filtered Signature Listing

Expands a folder into a per-file signature list, optionally filtered by a regex pattern.
Use this in the **fallback scope path** after `ck find-files` when the area is still uncharted.
It shows which files contain relevant members without dumping everything.

## When to use

- You are in fallback scope mode and got a folder from `ck find-files`, and want to narrow it down to relevant files.
- The folder is large and `ck signatures` would return too much to scan.
- You have 2-4 high-signal keywords (provider/domain + workflow + symbol/DTO/type),
  and want to see which files and members match before deciding which method to read.
- You are still in map-building mode (before a concrete target file is known).

## When NOT to use

- You already know the exact file/member in this area → use `ck signatures <file>` or `ck get-method-source` directly.
- You need to see **all** members in a small folder → use `ck signatures <folder>`.

## Command

```bash
.claude/skills/ck/ck expand-folder [--pattern <regex>] [--limit <n>] [--offset <n>] [--max-signatures <n>] [--all] <folder>
```

## Options

| Option | Default | Description |
|---|---|---|
| `--pattern <regex>` | show all for small folders only | Case-insensitive regex matched against `containingType`, `memberName`, and `signature` text. Files with zero matches are excluded from output. Broad matches are refused with keyword hints. Use 2-4 high-signal terms, not a single generic word. |
| `--limit <n>` | 20 | Page size for matched files (max 50). |
| `--offset <n>` | 0 | Page offset into ranked matched files. |
| `--max-signatures <n>` | 25 | Max signatures printed per file (`0` = unlimited). |
| `--all` | off | Allow broad output intentionally. Do not use this as a workaround for an imprecise pattern. |

If the command says `Pattern is too broad`, do not pipe or truncate the output. Rerun with more precision using the printed `add-keyword-hints`, for example provider + workflow + DTO/type/member words.

If you already reached a concrete file in this direction, do not go back to `expand-folder`. Continue with `ck signatures <file>` and `ck get-method-source <file> <MemberName>`. If direction changed, run a new `find-files` first.

Budget rule: use at most 3 `expand-folder` calls per direction. After that, either move to targeted file reads or re-scope.

## Output

```
<file-path>
  <line>  <containingType>  <memberName>  <signature>

<file-path>
  <line>  <containingType>  <memberName>  <signature>
```

One block per file. Files with no matching signatures are omitted entirely.
Pagination metadata is printed to stderr:

```
[ck expand-folder] pagination: offset=<n> limit=<n> returned=<n> total_estimate=<n> has_more=<true|false> [next_offset=<n>]
```

## Examples

```bash
# All signatures grouped by file (small folders only)
.claude/skills/ck/ck expand-folder src/Modules/Payment/Adyen/

# Filter to members mentioning "Refund"
.claude/skills/ck/ck expand-folder --pattern "Refund" src/Modules/Payment/Adyen/

# Filter to async methods
.claude/skills/ck/ck expand-folder --pattern "async Task" src/Modules/Payment/

# Filter to interface definitions
.claude/skills/ck/ck expand-folder --pattern "^I[A-Z]" src/Modules/Payment/Adyen/
```

## Workflow integration

```
0. ck find-files --query "..." --task "..."                         → primary file-level entrypoint
1. If needed, fallback: ck get-keyword-map + ck find-files          → folders = source of truth
2. ck expand-folder --pattern "<2-4 precise keywords>" <folder>     → see which files and members match
3. ck get-method-source <file> <MemberName>                         → read the method body
```
