## Code Navigation Protocol — mandatory for all source files (.cs, .ts, .tsx)

This codebase is large. Searching without narrowing scope first wastes tokens and reads wrong files.
CK tools work on both C# and TypeScript/TSX files.

**Command prefix:** `ck` (if in PATH), `~/.ck/bin/ck` (macOS/Linux fallback), or `%USERPROFILE%\.ck\bin\ck.exe` (Windows fallback).
Use whichever resolves in your environment.

### The workflow

```
0. KEYWORDS → ck get-keyword-map --query "domain area concept operation"   ← REQUIRED BEFORE SEARCH (grep/rg/glob/find)
1. SCOPE    → ck find-scope --query "..." (use refined terms from step 0)  ← REQUIRED BEFORE SEARCH (folders = source of truth)
2. EXPLORE  → ck expand-folder --pattern "<keyword>" <folder>   (preferred when you have a keyword)
              ck signatures <folder>/                            (use when no useful keyword; large folders are smart-ranked unless --all)
              grep -rn "<keyword>" <folder>/                     (grep / rg / glob are fine within scoped folders)
2.5 RECALL  → ck recall --folder <confirmed-folder>             ← BEFORE reading any method body
3. LOCATE   → ck find-symbol "<symbol>" [--path <folder-or-file>] [--kind type|member]
              ck refs "<symbol>" [--path <folder-or-file>]       ← use when you need call-sites/usages
4. READ     → ck get-method-source <file> <MemberName>          ← prefer over full file reads
              ck get-constructors <file>                          ← when you need constructor(s)
              ck get-usings <file>                                ← when you need import context
              ck get-base-types <file>                            ← when you need inheritance info
              ck get-type-source <file> <TypeName> [--kind ...]   ← when you need one type/record/interface/enum declaration
              ck get-enum-members <file> <EnumName>               ← when you need enum values without reading full file
              ck read-full-file <file>                            ← use this when you already know the file and need full context
              ck build-check <project.csproj>                     ← compact build diagnostics (instead of build|tail/grep)
5. EDIT     → make your changes
6. LEARN    → ck learn — MUST run before final response if CK tools were used this session
```

**Steps 0 and 1 establish the source of truth for search.** Keywords from `get-keyword-map` and folders from `find-scope` govern grep/rg/glob/find and broad exploration. If you already have a concrete file path from the task/plan, skip search bootstrapping and start directly with `ck read-full-file <file>` (or targeted reads on that same file). Native source `Read` is allowed, but use it only immediately before editing a specific file — not for exploration.

**Step 2.5 is mandatory once you have confirmed the folder you will work in** — unless the repo has `"brain": false` in `.ck.json`, in which case all recall/learn/forget commands exit silently and step 2.5 is skipped.
Run it after `ck expand-folder` or `ck signatures` has confirmed relevance — not after every
`find-scope` result. One `ck recall --folder` call per folder you actually intend to edit.
Run it **before** reading any method body — it may surface exactly what you need and save step 4 entirely.
Silent output means no knowledge exists yet — proceed normally.

**Step 6:** You MUST run `ck learn` before your final response if you used any CK tool this session
(find-scope, signatures, find-symbol, refs, get-method-source, recall, expand-folder). Record what would have been
useful to know at the start: routing logic not visible from folder structure, architectural
constraints, cross-module dependencies, or design decisions made. Do not record implementation
details findable via signatures. If nothing non-obvious was learned, verify this explicitly, then skip.

`ck find-scope` output is `<score>\t<folder-path>`. The score is a **relevance score** —
higher means more relevant. It is not a percentage; scores are relative values used for
ranking within a result set. On large codebases they typically cluster between 0.69 and 0.82.

### Playbook A — Find and read a specific symbol

```bash
# C# example:
ck get-keyword-map --query "adyen terminal card-present refund"
ck find-scope --query "adyen terminal card-present refund"
ck expand-folder --pattern "Refund" <folder>
ck find-symbol "RefundInPersonPaymentAsync" --path <folder> --kind member
ck get-method-source <file.cs> <ExactMemberName>

# TypeScript example:
ck get-keyword-map --query "backend rendering template fetcher"
ck find-scope --query "backend rendering template fetcher"
ck expand-folder --pattern "fetch\|render" <folder>
ck find-symbol "renderInvoiceTemplate" --path <folder> --kind member
ck get-method-source <file.ts> <functionOrMethodName>
```

### Playbook B — Implement a feature using an existing pattern

```bash
# 1. Establish keywords then scope both the reference and the target
#    Use --must to prevent competing providers from bleeding into results
ck get-keyword-map --query "terminal card-present refund payment"
ck find-scope --query "terminal card-present refund payment" --must "stripe"  # reference
ck find-scope --query "terminal card-present refund payment" --must "adyen"   # target

# 2. Explore reference implementation
ck expand-folder --pattern "Refund" <stripe-folder>
ck find-symbol "RefundInPersonPaymentAsync" --path <stripe-folder> --kind member
ck get-method-source <file> RefundInPersonPaymentAsync

# 3. Explore target (what exists today)
ck expand-folder --pattern "Refund" <adyen-folder>
ck find-symbol "RefundPaymentAsync" --path <adyen-folder> --kind member
ck get-method-source <file> RefundPaymentAsync

# 4. Edit — you now have enough context
```

### Playbook C — Impact analysis (cross-cutting change)

```bash
ck get-keyword-map --query "payment gateway refund async"
ck find-scope --query "payment gateway refund async" --min-score 0.5 --top 30 --explain
# returns ALL folders above threshold — may be 15-20 folders, that's fine
ck signatures <folder1>/
ck signatures <folder2>/
# ... for each relevant folder
# refs/grep within scoped folders for cross-references
ck refs "RefundPaymentAsync" --path <folder1>
grep -rn "RefundPaymentAsync" <folder1>/ <folder2>/
```

### Rules

1. **Before any source search, run `ck get-keyword-map` then `ck find-scope`.** This is mandatory for grep/rg/glob/find and broad discovery. If a concrete file is already known, skip straight to `ck read-full-file`, targeted reads, or a single native `Read` immediately before editing.
2. **Keywords/folders from steps 0-1 are the source of truth for search in that direction.** Keep grep/rg/glob/find inside returned folders. If scope seems wrong, use `--explain` and refine — don't restart with random synonyms.
3. **Commit to your folders. Don't re-scope the same area.** Once find-scope returns results for an area, work within those folders — re-running the same query with synonyms returns the same ranking. Use a new find-scope call only for a genuinely different sub-task area (reference vs. target, separate module).
4. **Use `ck expand-folder` as the preferred exploration tool within scoped folders.** Always pass `--pattern` with 2-4 high-signal terms (domain + workflow + symbol/DTO/type), for example `Refund|Terminal|Interac` or `CardPresent|Refund|Command`. Use `ck signatures <folder>` when you have no useful keyword or want to see all members — for large folders, `signatures` applies adaptive relevance ranking unless `--all` is passed intentionally. Budget: at most 3 `expand-folder` calls per direction before moving to targeted file reads.
5. **Run `ck recall --folder <path>` before reading any method body** (step 2.5). Do this once per folder you intend to edit — not for every find-scope result. Run it before step 4, not after — recall may surface exactly what you need and save the read entirely. Silent output means no knowledge exists yet, or brain is disabled for this repo.
6. **Avoid full file reads — use targeted commands instead.** Never read a full file when a targeted command will do:
   - `ck get-method-source <file> <Name>` — single method or property
   - `ck get-constructors <file>` — constructor(s) (avoids needing to know class name = constructor name in C#)
   - `ck get-usings <file>` — using/import directives only (when adding a new dependency)
   - `ck get-base-types <file>` — type declarations with base classes and interfaces
   Fall back to `ck read-full-file <file>` only when you need 3+ members, full class-level context, or non-member content.
   For large files, `ck read-full-file` refuses by default; rerun with `--allow-large` only when full context is truly required.
   Native `Read` is allowed only as an edit pre-step for a known target file; do not use it for exploration.
7. **Never re-run the exact same `find-scope` or `expand-folder` command.** If output was broad/no-match, refine via `get-keyword-map` (for scope) or tighten the pattern (for expand-folder) before retrying.
8. **Once you have a concrete file target, stop using `expand-folder` in that area.** Continue with `ck signatures <file>` and `ck get-method-source <file> <MemberName>`. If direction changed, reset with a new `find-scope` query.
9. **After 2 consecutive `expand-folder` no-match results in the same folder, stop expanding that folder.** Either run `get-keyword-map` + refined `find-scope` or switch to another scoped folder.
10. **grep / rg / glob / find require established scope.** First run `get-keyword-map` + `find-scope`, then keep searches inside returned folders. Never run source search from repo root/module roots, and never pipe CK output through filters; rerun CK commands with narrower query/pattern instead.
11. **Don't guess symbol names.** If you don't know the name, run `ck signatures` first, then `ck find-symbol` to locate the declaration.
12. **Prefer `ck refs` over broad text search for usage checks.** Once a symbol is confirmed, use `ck refs "<symbol>" --path <scoped-folder>` before fallback grep. Use grep/rg when references include dynamic strings or non-symbol patterns.
13. **Use `ck recall --query "<text>"` for unfamiliar domain concepts** that span multiple folders — e.g., "how does X work across the system?" This is optional and supplementary; the folder-scoped recall at step 2.5 covers most cases.
14. **You MUST run `ck learn`** (step 6) before your final response if you used any CK tool this session. Record the WHY and cross-cutting relationships — not implementation details. If nothing non-obvious was learned, verify this explicitly, then skip.
