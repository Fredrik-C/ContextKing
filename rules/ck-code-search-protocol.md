## Code Navigation Protocol — mandatory

This codebase is large. Searching without narrowing scope first wastes tokens and reads wrong files.

### The workflow

```
1. SCOPE    → .claude/skills/ck/ck find-scope --query "domain area concept operation"
2. EXPLORE  → .claude/skills/ck/ck signatures <folder>/     (for each relevant folder)
3. READ     → .claude/skills/ck/ck get-method-source <file> <MemberName>
4. EDIT     → make your changes
```

**Step 1 gives you folders. Steps 2–4 happen within those folders. Do not re-scope.**

### Playbook A — Find and read a specific symbol

```bash
.claude/skills/ck/ck find-scope --query "adyen terminal card-present refund"
# pick the matching folder(s) from results
.claude/skills/ck/ck signatures <folder>/
# find the member name in output
.claude/skills/ck/ck get-method-source <file> <ExactMemberName>
```

### Playbook B — Implement a feature using an existing pattern

```bash
# 1. Scope both the reference and the target
.claude/skills/ck/ck find-scope --query "stripe terminal card-present refund"         # reference
.claude/skills/ck/ck find-scope --query "adyen terminal payment gateway"              # target

# 2. Explore reference implementation
.claude/skills/ck/ck signatures <stripe-folder>/
.claude/skills/ck/ck get-method-source <file> RefundInPersonPaymentAsync

# 3. Explore target (what exists today)
.claude/skills/ck/ck signatures <adyen-folder>/
.claude/skills/ck/ck get-method-source <file> RefundPaymentAsync

# 4. Edit — you now have enough context
```

### Playbook C — Impact analysis (cross-cutting change)

```bash
.claude/skills/ck/ck find-scope --query "payment gateway refund async" --min-score 0.5 --top 30
# returns ALL folders above threshold — may be 15-20 folders, that's fine
.claude/skills/ck/ck signatures <folder1>/
.claude/skills/ck/ck signatures <folder2>/
# ... for each relevant folder
# use grep/rg WITHIN these folders for cross-references
grep -rn "RefundPaymentAsync" <folder1>/ <folder2>/
```

### Rules

1. **Always start with `ck find-scope`** when the relevant folder is unknown. Use `--top 15` or `--top 20` for broad tasks — don't cap too tight.
2. **Commit to your folders.** Once find-scope returns results, work within them. Don't re-run find-scope with rephrased queries — synonyms return the same ranking.
3. **Use `ck signatures` before reading files.** It shows every member without reading full content. Pass the folder path directly.
4. **Use `ck get-method-source` for single methods.** Use exact member names from signatures output — `Refund` won't match `RefundPaymentAsync`. Fall back to `Read` only when you need 3+ members from one file.
5. **Any search tool works within scoped folders.** After scoping, use grep, rg, Glob, Grep, or Read freely — but always scoped to the folders find-scope returned. Never `grep -r` from repo root.
6. **Don't guess symbol names.** If you don't know the name, run signatures first. Don't invent class names for searches.
7. **Budget: 2 find-scope calls per task.** One for the reference area, one for the target. If you need a third, you're re-scoping — stop and use the folders you have.
