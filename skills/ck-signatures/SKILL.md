---
name: ck-signatures
description: Extract all method/property signatures from C#, TypeScript, Kotlin, and Python files using live AST parsing. Use after ck find-files has identified the relevant folder, when evaluating multiple candidate files to avoid reading full file content.
---

# ck signatures — Reference

## Syntax

```bash
# Folder (recursive — only for small folders or when no keyword exists)
.claude/skills/ck/ck signatures <folder-path>/
.claude/skills/ck/ck signatures --all <folder-path>/  # only when broad output is intentional

# Specific files
.claude/skills/ck/ck signatures <file1.cs> [file2.cs ...]
```

## Output

```
<filepath>:<line>\t<containingType>\t<memberName>\t<signature>
```

Tab-separated. One line per method, constructor, or property.

## Feeding into get-method-source

```
signatures output:  src/Payment/Service.cs:42  Service  ProcessPayment  public async Task<Result> ProcessPayment(...)
get-method-source:  .claude/skills/ck/ck get-method-source src/Payment/Service.cs ProcessPayment
```

Use the exact `memberName` column — it's the argument for `get-method-source`.

## Tips

- Pass the **leaf folder** from find-files, not a parent. For large folders, `ck signatures` now applies adaptive relevance ranking by default; pass `--all` to force full output.
- Prefer `ck expand-folder --pattern "<keyword>" <folder>` when you have any useful domain/symbol word.
- For files <50 lines (DTOs, enums, records), skip signatures and use `ck read-full-file <file>`.
- No index required — always reads live from disk.
