---
name: ck-search
description: Scoped keyword search combining semantic folder ranking with git grep. Use when you need to find a specific symbol, method name, or keyword across the codebase without resorting to broad grep.
---

# ck search — Scoped Keyword Search

Combines **semantic folder ranking** (same as `ck find-scope`) with **keyword search**
(git grep) in a single call. Use this instead of running `ck find-scope` followed by
multiple `grep -r` commands.

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
.opencode/skills/ck/ck search --query "<scope description>" --pattern "<keyword>" [options]
```

**Windows (PowerShell):**
```powershell
.opencode\skills\ck\ck.cmd search --query "<scope description>" --pattern "<keyword>" [options]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | (required) | Semantic scope description — same multi-keyword query as `find-scope` |
| `--pattern <text>` | (required) | Keyword or regex to search for within the scoped folders |
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
.opencode/skills/ck/ck search \
  --query "payment terminal refund card present" \
  --pattern "RequestTerminalAuthorization"

# Find interface implementations in a specific domain
.opencode/skills/ck/ck search \
  --query "payment gateway adyen stripe" \
  --pattern "IPaymentGateway"

# Find all usages of an enum value
.opencode/skills/ck/ck search \
  --query "order status fulfillment" \
  --pattern "OrderStatus.Cancelled"

# Widen scope for cross-cutting searches
.opencode/skills/ck/ck search \
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
