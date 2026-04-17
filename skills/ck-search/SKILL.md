---
name: ck-search
description: Preferred entry point for code navigation. Scoped keyword search combining semantic folder ranking with git grep. Use this FIRST when you have a symbol, method name, or keyword to find. Replaces broad grep -r calls entirely.
---

# ck search — Scoped Keyword Search (preferred entry point)

The **recommended first step** for most code navigation tasks. Combines **semantic folder
ranking** with **keyword search** (git grep) in a single call. Use this instead of `grep -r`,
`grep -rn`, or running `ck find-scope` followed by manual grep commands.

## When to use

- You know the **domain area** (e.g. "payment terminal refund") AND a specific **keyword**
  (e.g. `RequestTerminalAuthorization`) and want to find where that keyword appears in
  the most relevant parts of the codebase.
- You need to find all usages of a method, class, or symbol within a semantically scoped area.
- You would otherwise run `ck find-scope` and then grep across the returned folders manually.

## When NOT to use

- You only need to find the right **folder** — use `ck find-scope` instead.
- You already know the exact file and need **method signatures** — use `ck signatures`.
- You need the **full method body** — use `ck get-method-source`.
- You need a truly repo-wide search with no semantic scoping — use `grep -rn` directly
  (but consider whether scoping would actually help).

## Command

**Mac / Linux:**
```bash
.claude/skills/ck/ck search --query "<scope description>" --pattern "<keyword>" [options]
```

**Windows (PowerShell):**
```powershell
.claude\skills\ck\ck.cmd search --query "<scope description>" --pattern "<keyword>" [options]
```

## How --query and --pattern work (two-stage pipeline)

`ck search` is a **two-stage pipeline**, not a single search box:

1. **`--query`** selects **which folders** to search. It uses semantic embedding similarity —
   the same mechanism as `ck find-scope`. Rephrasing the query with synonyms (`"stripe interac
   refund terminal"` vs `"stripe interac present refund terminal card-present"`) produces
   **nearly identical folder rankings**. The semantic model treats these as the same concept.

2. **`--pattern`** filters **lines within those folders** using literal `git grep` (regex).
   This is where specificity matters: `RefundPaymentAsync` finds different lines than `Refund`.

**If you got the right folders but wrong matches:** change `--pattern`, not `--query`.
Only change `--query` when the returned folders are in the **wrong area** of the codebase entirely.

**Do not re-run with rephrased query text.** If a search returned relevant folders, those
folders will not change with synonym variations. Proceed to `ck signatures` on the returned
folders, or run another `ck search` with a **different `--pattern`** targeting a different symbol.

## Do not pipe output through grep or head

The output of `ck search` is already structured and compact — folder scores followed by matching
lines grouped by folder. **Do not** pipe it through `grep`, `head`, or other filters. Filtering
discards the folder scores and grouping structure that you need to decide where to look next.

If the output is too large, reduce `--top` or add `--min-score` to narrow the folder set.

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | (required) | Semantic scope description — determines which folders are searched |
| `--pattern <text>` | (required) | Literal keyword or regex to match within the scoped folders (git grep) |
| `--top <n>` | 10 | Number of top-ranked folders to search within |
| `--min-score <f>` | off | Exclude folders below this score threshold |
| `--case-sensitive` | off | Make pattern matching case-sensitive (default: case-insensitive) |
| `--repo <path>` | auto | Repo root (defaults to `git rev-parse --show-toplevel`) |

## Output (stdout)

```
<score>\t<folder-path>
  <file>:<line>: <matching-content>
```

Matches are grouped by folder, ordered by semantic relevance score. Example:

```
0.9483  src/Modules/Payment/Terminal/
  src/Modules/Payment/Terminal/AdyenTerminalGateway.cs:42: public async Task RequestTerminalAuthorizationAsync(...)
  src/Modules/Payment/Terminal/AdyenTerminalGateway.cs:87: private async Task<Result> ProcessTerminalAuthorization(...)
0.8721  src/Modules/Payment/Gateway/
  src/Modules/Payment/Gateway/PaymentGatewayRouter.cs:156: var terminalAuth = await RequestTerminalAuthorizationAsync(request);
```

When no matches are found, the searched folders are printed to stderr so you can see
which areas were checked.

## Examples

```bash
# Find all references to a specific method across payment-related code
.claude/skills/ck/ck search \
  --query "payment terminal refund card present" \
  --pattern "RequestTerminalAuthorization"

# Find interface implementations in a specific domain
.claude/skills/ck/ck search \
  --query "payment gateway adyen stripe" \
  --pattern "IPaymentGateway"

# Find all usages of an enum value
.claude/skills/ck/ck search \
  --query "order status fulfillment" \
  --pattern "OrderStatus.Cancelled"

# Widen scope for cross-cutting searches
.claude/skills/ck/ck search \
  --query "async terminal payment authorization" \
  --pattern "TerminalPaymentComponent" --top 20
```

## Workflow integration

`ck search` fits between `ck find-scope` and `ck signatures` in the navigation workflow:

```
1. ck find-scope  → when you need to discover the right folder area
2. ck search      → when you know the area AND a keyword to find within it
3. ck signatures  → when you know the file(s) and want to see all members
4. ck get-method-source → when you know the exact member to read
```

Use `ck search` when step 1 alone would require follow-up grep calls. It eliminates
the round-trip and prevents the agent from falling back to broad `grep -r` searches.

## Behaviour

- **Auto-builds index on first call** if no index exists (same as `find-scope`).
- Uses `git grep` internally — only searches tracked (committed/staged) files.
- Case-insensitive by default; add `--case-sensitive` for exact matching.
- Pattern supports basic regex (git grep extended regex).
