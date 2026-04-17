## Code Search Protocol — mandatory

This codebase has a large number of C# and TypeScript files across many folders. Searching without
narrowing scope first produces false positives, wastes tokens, and reads the wrong files.

**Before any Glob, Grep, or Read on an unknown file location, use a CK command first.**

### Step 1 — Choose the right entry point

**If you have a keyword** (method name, class name, symbol, error message):
```bash
.claude/skills/ck/ck search --query "<domain description>" --pattern "<keyword>"
```
This is the most common case. It combines semantic folder ranking with git grep in one
call — no need for separate find-scope + grep round-trips. Use `--top 20` to widen scope.

**If you only need to discover the right area** (no specific keyword yet):
```bash
.claude/skills/ck/ck find-scope --query "<multi-keyword description>"
```
Use this when you're exploring — e.g. "where does payment processing live?" — and don't
yet have a symbol to search for.

Windows PowerShell equivalents use `.claude\skills\ck\ck.cmd` instead.

### Rules

1. **Always start with `ck search` or `ck find-scope`** when the relevant folder is unknown.
   Never jump straight to Glob, Grep, Read, or `grep -r`.
2. Use **multiple keywords** in `--query` — combine the module name, concept, operation, and type.
   Good: `"stripe payment disbursement payout reconciliation"`
   Bad:  `"stripe"` or `"payment"`
3. **Never use `grep -r`, `grep -rn`, or bash grep** to search source files. Use `ck search`
   with `--pattern` instead. This is the single most important rule — bash grep bypasses
   semantic scoping and wastes tokens scanning irrelevant files.
4. Scope all subsequent Glob/Grep searches to the folder(s) returned — never use `**/*.cs` or
   `**/*.ts` from the repo root. Pass the returned folder as the `path` parameter to Glob/Grep.
5. Do NOT run a broad Grep or Glob across the repo as a supplement to CK results. If CK results
   don't cover what you need, run another `ck search` or `ck find-scope` with different keywords.
6. Before reading ANY `.cs`, `.ts`, or `.tsx` file, run `ck signatures` first to confirm it
   contains what you need:
   ```bash
   .claude/skills/ck/ck signatures <folder-or-file>
   ```
   Pass the **folder path** to get all signatures in one call.
   Proceed to Read only after signatures output confirms the file is relevant.
7. After signatures identifies the right file and method, use `ck get-method-source` to read just
   that method — not the whole file:
   ```bash
   .claude/skills/ck/ck get-method-source <file> <MethodName>
   ```
   Fall back to a full Read only when you need 3+ members from the same file.
8. Never Read a `.cs`, `.ts`, or `.tsx` file speculatively. If you are not certain it is the
   right file, run signatures first.
9. **Do NOT use `find`, `ls`, or `Search` to enumerate files** after CK has returned results.
   Pass the folder directly to `ck signatures` — it recursively processes all supported files.
10. **Do NOT re-run `ck search` or `ck find-scope` with rephrased query text.** The semantic
    ranking is stable across synonym variations — rephrasing produces the same folders. If you
    got the right folders but wrong matches, change `--pattern` in `ck search`. If the folders
    are wrong, use different domain vocabulary (class names, namespace segments), not synonyms.
11. **Do NOT pipe `ck search` output through `grep` or `head`.** The output is already structured.
    Filtering it discards folder scores and grouping. Use `--top` or `--min-score` to limit results.
12. **`ck get-method-source <member-name>` is an exact name match**, not a keyword search.
    `Refund` will not match `RefundPaymentAsync`. Always get the exact name from
    `ck signatures` output before calling `get-method-source`.
