---
name: ck-get-type-source
description: Extract a single C#, TypeScript, Kotlin, or Python type declaration with exact source span. Use when you need one class/interface/record/enum/type alias without reading the full file.
---

# ck get-type-source — Reference

## Syntax

```bash
.claude/skills/ck/ck get-type-source <file> <TypeName> [--kind <class|interface|struct|record|enum|type_alias>]
```

## Output (JSON)

```json
[
  {
    "file": "src/Domain/PaymentRequest.cs",
    "type_name": "PaymentRequest",
    "kind": "record",
    "start_line": 12,
    "end_line": 30,
    "start_char": 234,
    "end_char": 1034,
    "content": "..."
  }
]
```

## Tips

- Prefer this over `ck read-full-file` when you only need one declaration.
- Works for C#, TypeScript, Kotlin, and Python/TSX files.
- Use `--kind` to disambiguate names shared across declaration kinds.
