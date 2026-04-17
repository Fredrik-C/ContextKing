---
name: ck-search
description: Preferred entry point for code navigation. Scoped keyword search combining semantic folder ranking with git grep. Use this FIRST when you have a symbol, method name, or keyword to find. Replaces broad grep -r calls entirely.
---

# ck search — Scoped Keyword Search (preferred entry point)

The **recommended first step** for most code navigation tasks. Combines **semantic folder
ranking** with **keyword search** (git grep) in a single call. Use this instead of `grep -r`,
`grep -rn`, or running `ck find-scope` followed by manual grep commands.

## When to use

- You know the **domain area** (e.g. "payment terminal refund") AND a specific **symbol name**
  (e.g. `ITerminalGateway`) and want to find where it is declared or used.
- You need to find all usages of a method, class, or symbol within a semantically scoped area.
- You would otherwise run `ck find-scope` and then grep across the returned folders manually.

## When NOT to use

- You only need to find the right **folder** — use `ck find-scope` instead.
- You already know the exact file and need **method signatures** — use `ck signatures`.
- You need the **full method body** — use `ck get-method-source`.

## Budget your searches

Each `ck search` call adds output to your context window. For a typical task you should need
**3–6 searches** to locate all relevant code — one per distinct concept you need to find.
If you are past 8 searches, you are almost certainly re-searching areas you already found.
Stop searching and switch to `ck signatures` on the folders you have.

**Pattern:** Search once per concept → commit to the returned folders → use signatures and
get-method-source to drill in. Do not search again for the same concept with different wording.

## Command

### Typed search (preferred)

Use `--name` with `--type` to let the tool generate language-aware regex automatically.
You don't need to write regex — just provide the symbol name and its type.

```bash
ck search --query "<scope>" --name "<symbol>" --type <class|method|member|file>
```

**Types:**

| Type | What it matches | Example |
|---|---|---|
| `class` | Class, interface, struct, record, enum **declarations** | `--type class --name ITerminalGateway` |
| `method` | Method/function **declarations and calls** | `--type method --name ChargePayment` |
| `member` | Property, field, variable **declarations** | `--type member --name RefundAmount` |
| `file` | **Filenames** containing the name (no content grep) | `--type file --name AdyenTerminal` |

If you omit `--type` but provide `--name`, it searches as a plain keyword (same as `--pattern`
but without needing to think about regex).

### Raw pattern (fallback)

When you need full regex control, use `--pattern` directly:

```bash
ck search --query "<scope>" --pattern "<regex>"
```

`--pattern` and `--name`/`--type` cannot be combined.

## How --query works (semantic scope)

`--query` selects **which folders** to search using semantic embedding similarity.
Rephrasing the query with synonyms produces **nearly identical folder rankings**.
The semantic model treats `"stripe interac refund terminal"` and `"stripe refund
terminal card-present"` as the same concept.

**If you got the right folders but wrong matches:** change `--name`/`--type` or `--pattern`,
not `--query`. Only change `--query` when the returned folders are in the **wrong area**
of the codebase entirely.

## Do not pipe output through grep or head

The output is already structured — folder scores followed by matching lines grouped by folder.
**Do not** pipe through `grep`, `head`, or other filters. If output is too large, reduce
`--top` or add `--min-score`.

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | (required) | Semantic scope — determines which folders are searched |
| `--name <symbol>` | — | Symbol name to search for (generates language-aware pattern) |
| `--type <kind>` | — | Symbol type: `class`, `method`, `member`, `file` |
| `--pattern <regex>` | — | Raw regex fallback (cannot combine with --name/--type) |
| `--top <n>` | 10 | Number of top-ranked folders to search within |
| `--min-score <f>` | off | Exclude folders below this score threshold |
| `--case-sensitive` | off | Make matching case-sensitive |
| `--repo <path>` | auto | Repo root (defaults to `git rev-parse --show-toplevel`) |

## Output (stdout)

```
<score>\t<folder-path>
  <file>:<line>: <matching-content>
```

Matches are grouped by folder, ordered by semantic relevance score.

When no matches are found, the searched folders are printed to stderr so you can see
which areas were checked — use this to inform your next search.

## Examples

```bash
# Find a class/interface declaration
ck search --query "payment terminal gateway" --type class --name "ITerminalGateway"

# Find method declarations and calls
ck search --query "payment terminal adyen" --type method --name "ChargePayment"

# Find files by name
ck search --query "adyen payment integration" --type file --name "AdyenGateway"

# Find property/field declarations
ck search --query "payment refund" --type member --name "RefundAmount"

# Plain keyword (no type, no regex needed)
ck search --query "payment refund" --name "RefundPaymentAsync"

# Raw regex fallback
ck search --query "order status" --pattern "OrderStatus\.(Cancelled|Refunded)"
```

## Workflow integration

```
1. ck find-scope  → discover the right folder area (no keyword yet)
2. ck search      → find a specific symbol within the right area
3. ck signatures  → see all members in specific files
4. ck get-method-source → read the full body of a specific method
```

## Behaviour

- **Auto-builds index on first call** if no index exists (same as `find-scope`).
- Uses `git grep` internally — only searches tracked (committed/staged) files.
- Typed search uses Perl-compatible regex (`-P`) for precise matching.
- Case-insensitive by default; add `--case-sensitive` for exact matching.
