## Code Navigation Protocol — mandatory for all source files (.cs, .ts, .tsx)

This codebase is large. Searching without narrowing scope first wastes tokens and reads wrong files.
CK tools work on both C# and TypeScript/TSX files.

### The workflow

```
1. SCOPE    → .opencode/skills/ck/ck find-scope --query "domain area concept operation"
2. EXPLORE  → .opencode/skills/ck/ck expand-folder --pattern "<keyword>" <folder>   (when you have a keyword)
              .opencode/skills/ck/ck signatures <folder>/                            (when you need everything)
3. READ     → .opencode/skills/ck/ck get-method-source <file> <MemberName>
4. EDIT     → make your changes
```

**Step 1 gives you folders. Steps 2–4 happen within those folders. Do not re-scope.**

`ck find-scope` output is `<score>\t<folder-path>`. The score is a **relevance score** —
higher means more relevant. It is not a percentage; scores are relative values used for
ranking within a result set. On large codebases they typically cluster between 0.69 and 0.82.

### Playbook A — Find and read a specific symbol

```bash
# C# example:
.opencode/skills/ck/ck find-scope --query "adyen terminal card-present refund"
.opencode/skills/ck/ck expand-folder --pattern "Refund" <folder>
.opencode/skills/ck/ck get-method-source <file.cs> <ExactMemberName>

# TypeScript example:
.opencode/skills/ck/ck find-scope --query "backend rendering template fetcher"
.opencode/skills/ck/ck expand-folder --pattern "fetch\|render" <folder>
.opencode/skills/ck/ck get-method-source <file.ts> <functionOrMethodName>
```

### Playbook B — Implement a feature using an existing pattern

```bash
# 1. Scope both the reference and the target
#    Use --must to prevent competing providers from bleeding into results
.opencode/skills/ck/ck find-scope --query "terminal card-present refund payment" --must "stripe"  # reference
.opencode/skills/ck/ck find-scope --query "terminal card-present refund payment" --must "adyen"   # target

# 2. Explore reference implementation
.opencode/skills/ck/ck expand-folder --pattern "Refund" <stripe-folder>
.opencode/skills/ck/ck get-method-source <file> RefundInPersonPaymentAsync

# 3. Explore target (what exists today)
.opencode/skills/ck/ck expand-folder --pattern "Refund" <adyen-folder>
.opencode/skills/ck/ck get-method-source <file> RefundPaymentAsync

# 4. Edit — you now have enough context
```

### Playbook C — Impact analysis (cross-cutting change)

```bash
.opencode/skills/ck/ck find-scope --query "payment gateway refund async" --min-score 0.5 --top 30
# returns ALL folders above threshold — may be 15-20 folders, that's fine
.opencode/skills/ck/ck signatures <folder1>/
.opencode/skills/ck/ck signatures <folder2>/
# ... for each relevant folder
# use grep/rg WITHIN these folders for cross-references
grep -rn "RefundPaymentAsync" <folder1>/ <folder2>/
```

### Rules

1. **Always start with `ck find-scope`** when the relevant folder is unknown. Use `--top 15` or `--top 20` for broad tasks — don't cap too tight.
2. **Commit to your folders.** Once find-scope returns results, work within them. Don't re-run find-scope with rephrased queries — synonyms return the same ranking.
3. **Use `ck expand-folder` to explore folders.** Pass `--pattern "<keyword>"` to filter to only relevant files and signatures. Use `ck signatures <folder>` only when you genuinely need everything in the folder.
4. **Use `ck get-method-source` for single methods.** Use exact member names from signatures output — `Refund` won't match `RefundPaymentAsync`. Fall back to `Read` only when you need 3+ members from one file.
5. **Any search tool works within scoped folders.** After scoping, use ugrep, bfs, grep, rg, Glob, Grep, or Read freely — but always scoped to the folders find-scope returned. Never search from repo root.
6. **Don't guess symbol names.** If you don't know the name, run signatures first. Don't invent class names for searches.
7. **Budget: 2 find-scope calls per task.** One for the reference area, one for the target. If you need a third, you're re-scoping — stop and use the folders you have.
