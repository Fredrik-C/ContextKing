---
name: ck-get-method-source
description: Extract a single C# or TypeScript method/property body with exact source spans. Use after ck signatures identifies the target member.
---

# ck get-method-source — Reference

## Syntax

```bash
.claude/skills/ck/ck get-method-source <file> <ExactMemberName> [--type <TypeName>] [--mode <mode>]
```

**`<ExactMemberName>` must match exactly** — copy it from `ck signatures` output.
`Refund` will NOT match `RefundPaymentAsync`.

## Modes

| Mode | Returns |
|---|---|
| `signature_plus_body` | Full member (default) |
| `signature_only` | Signature line only |
| `body_only` | Body block only |
| `body_without_comments` | Body with comments stripped |

## Output (JSON)

```json
[{
  "file": "src/Payment/Processor.cs",
  "member_name": "ProcessPayment",
  "containing_type": "PaymentProcessor",
  "signature": "public async Task<Result> ProcessPayment(PaymentRequest req)",
  "start_line": 42, "end_line": 87,
  "content": "..."
}]
```

## Tips

- Add `--type ClassName` to disambiguate overloads across types.
- After you reach this step, continue with targeted file/member reads in the same area; do not bounce back to `expand-folder` for that folder unless you intentionally reset direction with a new `find-files` query.
- For files <50 lines, use `ck read-full-file <file>` directly.
- No index required — always reads live from disk.
