---
name: ck-get-base-types
description: Extract type declarations with base classes and interfaces from a C#, TypeScript, Kotlin, or Python file. Use when you need to understand inheritance hierarchy or find where a class fits in the type system.
---

# ck get-base-types — Reference

## Syntax

```bash
.claude/skills/ck/ck get-base-types <file>
```

## Output (JSON)

```json
[{
  "file": "src/Payment/AdyenGateway.cs",
  "name": "AdyenGateway",
  "kind": "class",
  "base_types": ["PaymentGatewayBase", "IPaymentGateway", "IDisposable"],
  "line": 12
}]
```

## Fields

| Field | Description |
|---|---|
| `name` | Type name |
| `kind` | `class`, `abstract class`, `interface`, `struct`, `record`, `record struct`, `enum` |
| `base_types` | All entries from the base/implements list (base class + interfaces combined) |
| `line` | 1-based line number of the declaration |

## Notes

- `base_types` is `[]` when the type has no base class or interfaces.
- Base class and interface entries are not distinguished — Roslyn requires compilation for that.
- For TypeScript, both `extends` and `implements` entries appear in `base_types`.
- No index required — always reads live from disk.
