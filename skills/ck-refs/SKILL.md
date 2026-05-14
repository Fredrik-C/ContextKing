---
name: ck-refs
description: Find textual references for a symbol in C#, TypeScript, Kotlin, and Python/TSX files within scoped folders or explicit paths.
---

# ck refs — Symbol Reference Locator

Use this command after `ck find-symbol` when you need fast reference locations.

## Syntax

```bash
.claude/skills/ck/ck refs "<symbol>" [--path <folder-or-file>] [--top <n>] [--ignore-case]
```

## Typical usage

```bash
.claude/skills/ck/ck refs "TypedGatewayPayment"
.claude/skills/ck/ck refs "RequestTerminalRefundAsync" --path src/Modules/PaymentProcessing/
```

## Output

Tab-separated rows:

```
<score>  <file:line>  <line snippet>
```

- `score` prioritizes exact full-symbol matches over identifier-only matches.

