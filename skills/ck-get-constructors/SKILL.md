---
name: ck-get-constructors
description: Extract all constructors from a C#, TypeScript, Kotlin, or Python file with exact source spans. Use when you need to see constructor signatures or injected dependencies without knowing the class name.
---

# ck get-constructors — Reference

## Syntax

```bash
.claude/skills/ck/ck get-constructors <file> [--type <TypeName>] [--mode <mode>]
```

Use this instead of `ck get-method-source` when you want constructor(s) — avoids needing to know that in C# the constructor name equals the class name.

## Options

| Option | Description |
|---|---|
| `--type ClassName` | Filter to a specific class (when file has multiple) |
| `--mode <mode>` | `signature_plus_body` (default), `signature_only`, `body_only`, `body_without_comments` |

## Output (JSON)

```json
[{
  "file": "src/Payment/AdyenGateway.cs",
  "member_name": "AdyenGateway",
  "containing_type": "AdyenGateway",
  "signature": "public AdyenGateway(ILogger<AdyenGateway> logger, IAdyenClient client)",
  "start_line": 18, "end_line": 28,
  "content": "..."
}]
```

## Notes

- In C#, `member_name` equals the class name — this is expected Roslyn behaviour.
- In TypeScript, `member_name` is always `"constructor"`.
- No index required — always reads live from disk.
