---
name: ck-expand-folder
description: List files in a folder with their signatures, filtered by an optional regex pattern. Files with no matching signatures are excluded. Use this as the EXPLORE step after ck find-scope when you have a keyword in mind.
---

# ck expand-folder — Filtered Signature Listing

Expands a folder into a per-file signature list, optionally filtered by a regex pattern.
Use this as the **EXPLORE step** after `ck find-scope` — it shows which files contain
relevant members without dumping everything.

## When to use

- You got a folder from `ck find-scope` and want to narrow it down to relevant files.
- The folder is large and `ck signatures` would return too much to scan.
- You have a keyword (e.g. "Refund", "async Task", "ITerminal") and want to see which
  files and members match before deciding which to read in full.

## When NOT to use

- You already know the exact file → use `ck signatures <file>` or `ck get-method-source` directly.
- You need to see **all** members in a small folder → use `ck signatures <folder>`.

## Command

```bash
.claude/skills/ck/ck expand-folder [--pattern <regex>] <folder>
```

## Options

| Option | Default | Description |
|---|---|---|
| `--pattern <regex>` | show all | Case-insensitive regex matched against `containingType`, `memberName`, and `signature` text. Files with zero matches are excluded from output. |

## Output

```
<file-path>
  <line>  <containingType>  <memberName>  <signature>

<file-path>
  <line>  <containingType>  <memberName>  <signature>
```

One block per file. Files with no matching signatures are omitted entirely.

## Examples

```bash
# All signatures grouped by file (no filter)
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
1. ck find-scope   → discover the right folder
2. ck expand-folder --pattern "<keyword>" <folder>  → see which files and members match
3. ck get-method-source <file> <MemberName>         → read the full method body
```
