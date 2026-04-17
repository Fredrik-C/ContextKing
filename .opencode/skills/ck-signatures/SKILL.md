---
name: ck-signatures
description: Extract all method/property signatures from C# and TypeScript files using live AST parsing. Use after ck find-scope has identified the relevant folder, when evaluating multiple candidate files to avoid reading full file content.
---

# ck signatures — Live AST Signature Extraction

Use this skill to list all method, constructor, and property signatures from one or more C# or
TypeScript/TSX files **without reading their full content**. Always reads directly from disk —
reflects uncommitted edits. Language is auto-detected from the file extension (`.cs` for C#,
`.ts`/`.tsx` for TypeScript).

## This is step 2 — run after ck find-scope

The full workflow for finding and reading code in a large codebase:

1. **`ck find-scope`** — identify the relevant folder subtree from a semantic query.
2. **Glob within that folder** — use the Glob tool with `path` set to the returned folder and a
   specific pattern (e.g. `*.cs` or `*Payment*.cs`). Never use `**/*.cs` from the repo root.
3. **`ck signatures <file1> <file2> ...`** — inspect signatures of candidates; pick the right file and member.
4. **`ck get-method-source <file> <memberName>`** — read just that member's body, not the whole file.
   This is the preferred next step after signatures — avoid full file reads when only one method is needed.
5. **Read** — fall back to a full file read only when you need several members from the same file.

## Command

**Preferred — pass the folder path directly (output of `ck find-scope`):**
```bash
# Mac / Linux
.opencode/skills/ck/ck signatures src/Hosts/Api/Catalog/V1/Products/

# Windows (PowerShell)
.opencode\skills\ck\ck.cmd signatures src\Hosts\Api\Catalog\V1\Products\
```

Passing a folder processes every `.cs` file in that subtree recursively. This is the natural
follow-up to `ck find-scope`: take the returned folder path and feed it straight in — no glob
expansion, no file enumeration, no risk of accidentally leaving files out.

**Specific files or glob patterns:**
```bash
# Mac / Linux — specific files or glob
.opencode/skills/ck/ck signatures <file1.cs> [file2.cs ...]
.opencode/skills/ck/ck signatures src/**/Services/*.cs

# Windows (PowerShell)
.opencode\skills\ck\ck.cmd signatures <file1.cs> [file2.cs ...]
```

## Output (stdout)

```
<filepath>:<line>\t<containingType>\t<memberName>\t<signature>
```

One line per method, constructor, or property. Tab-separated fields.

Example:
```
/repo/src/Payment/AdyenService.cs:42    AdyenService    ProcessPayment    public async Task<Result> ProcessPayment(PaymentRequest request)
/repo/src/Payment/AdyenService.cs:87    AdyenService    GetFees           private decimal GetFees(string currency)
```

## Feeding output directly into ck get-method-source

Every output line contains the exact arguments needed for the next step:

| signatures field | maps to |
|---|---|
| `<filepath>` (before the `:line`) | `ck get-method-source <file.cs>` |
| `<memberName>` (3rd column) | `ck get-method-source ... <member-name>` |
| `<containingType>` (2nd column) | `--type <TypeName>` (add when name is not unique) |

Example — line returned by signatures:
```
/repo/src/Payment/AdyenService.cs:42    AdyenService    ProcessPayment    public async Task<Result> ProcessPayment(...)
```

Corresponding get-method-source call:
```bash
.opencode/skills/ck/ck get-method-source \
  /repo/src/Payment/AdyenService.cs ProcessPayment --type AdyenService
```

## Impact analysis — the folder pipeline

For impact analysis ("which methods need async?", "what implements this interface?", "what
does this call chain touch?"), **pass the folder path directly**. Combine with `ck find-scope`:

```bash
# Step 1 — find the relevant folder
.opencode/skills/ck/ck find-scope --query "Catalog API products controllers" --min-score 0.5

# Step 2 — get every method in that folder in one shot
.opencode/skills/ck/ck signatures src/Hosts/Api/Catalog/V1/Products/
```

The raw output lists every method across every file. **Do not summarise it — use it directly**
to decide what to investigate next. Summaries lose information; the raw tab-separated lines do not.

This is the correct approach for:
- Cross-cutting rewrites (async, cancellation tokens, logging)
- Finding all callers/implementors before changing an interface signature
- Estimating the scope of a change before starting work

## Multi-layer end-to-end tracing

When a task says "end-to-end" (controller → business layer → DB), one folder sweep is not
enough. You must **repeat the pipeline for each layer**:

```
Layer 1: ck find-scope "Catalog API products controllers"  → ck signatures <folder>
          → identify which business component methods each controller calls

Layer 2: ck find-scope "OrderFulfillment business component" → ck signatures <folder>
          → identify which repo/query methods each business component calls

Layer 3: ck find-scope "order query repository database"   → ck signatures <folder>
          → identify sync DB calls that need async versions
```

Each layer's signatures output tells you exactly which calls to trace in the next layer.
Do not stop at the first layer — "end-to-end" means tracing all the way to the I/O boundary.

## Filtering signatures with grep

Large files can produce hundreds of signature lines. **Pipe to `grep`** (or `Select-String`
on PowerShell) to filter to the relevant subset and save context:

**Mac / Linux:**
```bash
# Show only refund-related members:
.opencode/skills/ck/ck signatures src/Gateways/StripePaymentGateway.cs | grep -i refund

# Show members matching multiple terms:
.opencode/skills/ck/ck signatures src/Payments/PaymentComponent.cs | grep -iE "terminal|card.?present"

# Include a few lines of context around matches:
.opencode/skills/ck/ck signatures src/Queues/QueueItemComponent.cs | grep -A2 "TerminalPayment"
```

**Windows (PowerShell):**
```powershell
# Show only refund-related members:
.opencode\skills\ck\ck.cmd signatures src\Gateways\StripePaymentGateway.cs | Select-String -Pattern "refund" -CaseSensitive:$false

# Show members matching multiple terms:
.opencode\skills\ck\ck.cmd signatures src\Payments\PaymentComponent.cs | Select-String -Pattern "terminal|card.?present" -CaseSensitive:$false

# Include a few lines of context around matches:
.opencode\skills\ck\ck.cmd signatures src\Queues\QueueItemComponent.cs | Select-String -Pattern "TerminalPayment" -Context 0,2
```

This is especially useful when scanning a gateway or component with many methods and you only
care about a specific operation.

## IMPORTANT: always use get-method-source after identifying a target

Once you have identified the target member from signatures output, **always** use
`ck get-method-source` to read its implementation. **Do not** fall back to a full file `Read`
when you only need one method — that wastes tokens and context window. The only exception is
when you need several members from the same file; then a full `Read` is acceptable.

## When to use

- **Impact analysis**: pass the folder path to map the complete method surface before deciding
  what to read. Always use the raw output — never ask a sub-agent to summarise it.
- You have candidate `.cs` files and want to determine which contains the relevant code.
- You want a compact overview of a file's public surface without reading the implementation.
- You need to locate a specific method across several files efficiently.

## When NOT to use

- You have not yet run `ck find-scope` — do that first to narrow scope.
- **The folder contains only small files (DTOs, enums, interfaces, records under ~50 lines).**
  Running signatures on a folder of tiny files adds a tool call without providing useful
  filtering — just Read the files directly instead. The CLI will emit a `[ck hint]` on stderr
  when it detects this situation. If you see the hint, skip signatures for similar folders
  in the rest of the session and Read the files directly.

## Key properties

- **Always live**: reads from disk on every call. No index or cache involved.
- **No index required**: works without running `ck index` first.
- **Uncommitted edits reflected immediately**.
- Covers: methods, constructors, properties (get/set shown inline).
- Parse errors for a file are reported to stderr; that file is skipped.
