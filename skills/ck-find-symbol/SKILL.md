---
name: ck-find-symbol
description: Find type/member declarations in C#, TypeScript, Kotlin, and Python/TSX files with ranked matches. Use after ck find-files scopes the source area or when a symbol is already known.
---

# ck find-symbol — Symbol Declaration Locator

For an unknown source location, run `ck find-files` first, then use this command to locate a known declaration in the candidate scope. Do not use `rg`/`grep` loops for normal source discovery or candidate inspection; use CK signatures and source extraction. `rg` remains useful for exact literal searches or questions CK cannot answer.

## Syntax

```bash
.claude/skills/ck/ck find-symbol "<symbol>" [--path <folder-or-file>] [--kind type|member] [--top <n>]
```

## Typical usage

```bash
.claude/skills/ck/ck find-symbol "TypedGatewayPayment"
.claude/skills/ck/ck find-symbol "RefundPaymentAsync" --path src/Modules/PaymentProcessing/ --kind member
.claude/skills/ck/ck find-symbol "AdyenBalancePaymentGateway" --kind type
```

## Output

Tab-separated rows:

```
<score>  <file:line>  <kind>  <symbol>  <container>  <signature>
```

- `score`: 0.000–1.000, higher = more exact
- `kind`: `type` or `member`
- `container`: containing type for members

