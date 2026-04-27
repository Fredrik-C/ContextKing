---
name: ck-get-enum-members
description: List enum members for a specific C# or TypeScript enum without reading full file content.
---

# ck get-enum-members — Reference

## Syntax

```bash
.claude/skills/ck/ck get-enum-members <file> <EnumName>
```

## Output (JSON)

```json
{
  "file": "src/Payments/MessageCategory.cs",
  "enum_name": "MessageCategory",
  "start_line": 8,
  "end_line": 20,
  "members": ["Payment", "Reversal", "Refund"]
}
```

## Tips

- Prefer this over `ck read-full-file` when you only need enum values.
- Works for C# and TypeScript/TSX files.
