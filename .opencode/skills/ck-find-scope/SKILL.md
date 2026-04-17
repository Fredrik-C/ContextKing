---
name: ck-find-scope
description: Semantic folder search to narrow scope before file/method searches in large C# and TypeScript repos. ALWAYS run this first when the relevant folder area is unknown. Use multiple descriptive keywords for best results.
---

# ck find-scope — Semantic Scope Search

**Run this before any file or method search** when the relevant part of the codebase is not already
known. It narrows the search to the most relevant folder subtree, eliminating false-positive matches
in unrelated areas and avoiding unnecessary file reads.

## This is step 1 — always run it first

Before using Glob, Grep, or Read to explore unknown code:

1. **Run `ck find-scope`** with a rich multi-keyword query → get the top-scored folder path(s).
2. **Scope all subsequent searches** to that folder path.
3. Only read full files once you have a specific target.

Skipping this step in a large codebase means searching the whole tree — many false positives,
many wasted file reads, many wasted tokens.

## Two distinct use cases — choose the right --top

### Targeted search (find the code for feature X)
Default `--top 10` is fine. You want the best-matching folder and a few neighbours.

### Impact analysis (find ALL code affected by a change)
Use `--min-score <threshold>` instead of `--top`. This returns **every** folder above the
relevance threshold — no silent truncation, no need to guess the right count. A broad change
like "make all X async" or "find all callers of Y" touches many folders; `--top 10` (or even
`--top 25`) can silently drop relevant areas.

```bash
# Finding all controllers in an API area for a cross-cutting async rewrite:
.opencode/skills/ck/ck find-scope \
  --query "Catalog API products controllers endpoints" --min-score 0.5

# Finding all callers/implementations of an interface across modules:
.opencode/skills/ck/ck find-scope \
  --query "IPaymentGateway implementation process charge" --min-score 0.5
```

**Picking a threshold:** scores cluster in a narrow band that shifts per codebase and per query
(typically 0.69–0.82). Run one query first to see your range, then set the threshold slightly
above the bottom of the cluster you want to keep. There is no universal good value — 0.5 sounds
intuitive but will return the entire index on a typical codebase where the floor is already 0.69+.

You can combine both filters: `--min-score <threshold> --top 30` caps a score-filtered list as
a safety bound when the score distribution is unusually flat.

After finding the folders, run `ck signatures` on **all files** in each returned folder
(not just the ones you already know about). See the `ck-signatures` skill for the folder
sweep pattern.

**How to pick the threshold:** run one query first and look at the scores. All results will
cluster in a narrow band (typically 0.69–0.82 for large codebases). Set `--min-score` just
below the cluster floor to return the whole relevant cluster, or a few points above the bottom
to trim the tail. The exact value is codebase-dependent — do not use a fixed threshold without
first checking your own score range.

## Use multiple keywords — this matters

The search combines semantic similarity with exact keyword matching. More keywords in the query
means more signal on both axes:
- **Semantic**: a longer phrase anchors the embedding more precisely in concept space.
- **Exact match**: each query term that appears literally in a folder path or filename adds a
  tiebreaker bonus.

**Prefer:**
```
--query "stripe payment disbursement payout"
--query "adyen interchange fee calculation"
--query "order cancellation refund policy"
```

**Avoid (too vague):**
```
--query "stripe"
--query "payment"
--query "fee"
```

Use the vocabulary of the domain and the codebase: include the integration name, the business
concept, the operation, and the data type if known. Even if some words do not appear literally
in folder names, they improve the semantic embedding that drives the primary ranking.

### Query the structural location, not just the domain concept

The index scores folder paths and filenames. When looking for code in a specific layer of the
architecture (controllers, repositories, handlers, consumers), **include that structural term
in the query** alongside the domain concept:

**Less effective** (domain only — may return DTOs, helpers, tests instead of the right layer):
```
--query "orders async helpers GetOrders"
```

**More effective** (domain + structural layer):
```
--query "Catalog API products controllers endpoints"
--query "orders repository data access query"
--query "order domain handlers commands events"
```

This distinction matters most for impact analysis, where you must find the right structural
layer (e.g. all controllers in an API area) rather than just any folder mentioning the domain.

## Command

**Mac / Linux:**
```bash
.opencode/skills/ck/ck find-scope --query "<description>" [--top <n>] [--repo <path>]
```

**Windows (PowerShell):**
```powershell
.opencode\skills\ck\ck.cmd find-scope --query "<description>" [--top <n>] [--repo <path>]
```

## Options

| Option | Default | Description |
|---|---|---|
| `--query <text>` | (required) | Multi-keyword description of the code area |
| `--top <n>` | 10 | Hard cap on result count. When `--min-score` is set without `--top`, this cap is removed. |
| `--min-score <f>` | off | Exclude folders below this score. Returns all above-threshold folders when used without `--top`. Check your score range first — scores cluster in a narrow band that varies by codebase (often 0.69–0.82). |
| `--repo <path>` | auto | Repo root (defaults to `git rev-parse --show-toplevel`) |

## Output (stdout)

```
<score>\t<relative-folder-path>
```

One line per result. Scores are not capped at 1.0 — they are relative, so use them for ranking,
not as absolute confidence values.

## Behaviour

- **Auto-builds index on first call** if no index exists for this repo/worktree. Progress goes to
  stderr; wait for it to complete (typically under 30 s for large repos).
- The index reflects the live working tree: untracked files and working-tree deletions are included,
  not just committed state.
- Subsequent calls are fast (in-memory scoring after index load).
- Each linked worktree has its own index.

## Interpreting results

- Use the **top-scored folder path** to scope Glob/Grep/Read.
- If the top score is much higher than the rest, the answer is likely in that subtree.
- If scores are tightly clustered, consider `--min-score 0.65` to keep only the clearly relevant
  results rather than a fixed count.
- Scores cluster in a narrow band — a spread of 0.03–0.07 across 10 results is normal. The
  absolute values shift per query; use them for relative ranking within a result set, not as
  absolute confidence measures.
- If all scores seem low compared to previous queries, the query vocabulary probably doesn't
  match the codebase's naming conventions — try different structural or domain terms.

## Examples

```bash
# Good: rich multi-keyword query
.opencode/skills/ck/ck find-scope \
  --query "stripe payment disbursement payout reconciliation"

# Good: specific integration + concept + operation
.opencode/skills/ck/ck find-scope \
  --query "adyen interchange fee calculation"

# When uncertain about which module: widen to top 5
.opencode/skills/ck/ck find-scope \
  --query "order cancellation refund" --top 5
```

## After this step

Use the returned folder path to scope the **next** operation.

**Step 2 — pass the folder path directly to `ck signatures`:**
```bash
.opencode/skills/ck/ck signatures <returned-folder-path>/
```

This produces every method signature in the folder in one command. **Use the raw output** —
do not summarise it or pre-select files before running signatures. Pre-selecting files means
silently skipping files you haven't thought of yet.

Never use a repo-wide `**/*.cs` Glob or Grep after find-scope has narrowed the area.

**Step 3 — read only what you need** using `ck get-method-source` for a single method, or
a full Read only when several members from the same file are needed.

## When NOT to use find-scope

`find-scope` is for **semantic folder discovery** — finding *where* in the codebase a concept
lives. It is **not** the right tool for:

- **Cross-cutting reference searches** (who calls X, who implements interface Y, all usages of
  an enum value). Use `grep -rn` scoped to the relevant module for these.
- **Exact symbol lookup** when you already know the file or folder. Go straight to
  `ck signatures` or `ck get-method-source`.
- **Finding a specific class by name** (e.g. "where is `TerminalPaymentComponent` defined?").
  Use `grep -rn 'class TerminalPaymentComponent'` scoped to the relevant module — find-scope
  searches folder/file names semantically, not class declarations.
- **Non-C#/TypeScript files** (SQL, config, YAML, etc.).
