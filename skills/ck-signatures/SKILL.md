---
name: ck-signatures
description: Extract all method/property signatures from C# and TypeScript files using live AST parsing. Use after ck find-scope or ck search to inspect files without reading full content.
---

# ck signatures — Reference

## Syntax

```bash
# Folder (recursive — preferred after find-scope)
.claude/skills/ck/ck signatures <folder-path>/

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

- Pass the **leaf folder** from find-scope, not a parent. If >30 files processed, narrow the folder.
- For files <50 lines (DTOs, enums, records), skip signatures and just `Read` the file.
- No index required — always reads live from disk.
