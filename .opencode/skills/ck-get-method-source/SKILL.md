---
name: ck-get-method-source
description: Extract a single C# method/property body or signature with exact source spans after ck signatures identifies the target member.
---

## ck get-method-source — extract a single method/property with exact span

**Step 3 of the code navigation workflow** (after `ck find-scope` → `ck signatures`).

Use this instead of a full file `Read` whenever you need only one member's implementation.
It reads live from disk and returns structured JSON — no index required.

```bash
.opencode/skills/ck/ck get-method-source \
  <file.cs> <member-name> [--type <TypeName>] [--mode <mode>]
```

### Modes (`--mode`)

| Mode | Returns |
|---|---|
| `signature_plus_body` | Full member — signature + body **(default)** |
| `signature_only` | Signature line only, no body |
| `body_only` | Body block or expression body only |
| `body_without_comments` | Body with all `//`, `/* */`, and doc comments removed |

### Output JSON

```json
[
  {
    "file": "src/Payment/Processor.cs",
    "member_name": "ProcessPayment",
    "containing_type": "PaymentProcessor",
    "signature": "public async Task<Result> ProcessPayment(PaymentRequest req, CancellationToken ct)",
    "mode": "signature_plus_body",
    "start_line": 42,
    "end_line": 87,
    "start_char": 1234,
    "end_char": 2567,
    "content": "..."
  }
]
```

`start_char` / `end_char` are zero-based UTF-16 character offsets within the file.
For `body_without_comments`, the span still reflects the original body position in the file;
only `content` has comments stripped.

### When to use each mode

- **`signature_plus_body`** — default; read the full implementation.
- **`signature_only`** — check parameter types or return type before deciding to read the body.
- **`body_only`** — skip repetitive boilerplate in the signature, get straight to the logic.
- **`body_without_comments`** — strip noise from heavily commented methods; span is still accurate for cross-referencing.

### Disambiguation

When a name has multiple overloads the result array contains all of them.
Add `--type ClassName` to filter to a specific containing type.

### Workflow reminder

```
ck find-scope  --query "..."            # 1. narrow to the right folder
ck signatures  <file.cs> [file2.cs]    # 2. confirm which file + member
ck get-method-source <file.cs> <name>  # 3. read just that member
```

Prefer this over a full file `Read` whenever you need a single member.
Fall back to `Read` only when you need multiple members from the same file
or the file is short enough that reading it whole is faster.

### Small files — skip this tool

For files under ~50 lines (DTOs, enums, simple interfaces, command/query records),
read the file directly with `Read`. Running `ck signatures` + `ck get-method-source`
on a 15-line record class adds two tool calls for no benefit.
