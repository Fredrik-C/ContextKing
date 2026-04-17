## Code Search Protocol — mandatory

This codebase has a large number of C# and TypeScript files across many folders. Searching without
narrowing scope first produces false positives, wastes tokens, and reads the wrong files.

**Before any Glob, Grep, or Read on an unknown file location, use a CK command first.**

### Step 0 — Plan before searching

Before starting code exploration, list the **specific things you need to find** (interfaces,
implementations, callers, DTOs, etc.). Work through them **one at a time** — fully resolve
each item with find-scope → signatures → get-method-source before moving to the next. Do not
interleave searches for different items; that causes you to revisit the same areas repeatedly.

### Step 1 — Choose the right entry point

**If you have a symbol name** (class, method, interface, property):
```bash
.claude/skills/ck/ck search --query "<domain description>" --type <class|method|member|file> --name "<SymbolName>"
```
This is the preferred mode. `--type` generates language-aware regex automatically — you do not
need to write regex. Types: `class` (includes interface/struct/record/enum), `method`, `member`
(property/field), `file` (filename match only, no content grep).

Examples:
```bash
# Find an interface declaration
.claude/skills/ck/ck search --query "payment terminal gateway" --type class --name "ITerminalGateway"

# Find method declarations and call sites
.claude/skills/ck/ck search --query "payment refund" --type method --name "RefundPaymentAsync"

# Find files by name
.claude/skills/ck/ck search --query "adyen integration" --type file --name "AdyenGateway"
```

**If you need a raw regex pattern** (rare — only when `--type` doesn't fit):
```bash
.claude/skills/ck/ck search --query "<domain description>" --pattern "<regex>"
```

**If you only need to discover the right area** (no specific keyword yet):
```bash
.claude/skills/ck/ck find-scope --query "<multi-keyword description>"
```

Windows PowerShell equivalents use `.claude\skills\ck\ck.cmd` instead.

### Rules

1. **Always start with `ck search` or `ck find-scope`** when the relevant folder is unknown.
   Never jump straight to Glob, Grep, Read, or `grep -r`.
2. **Use `--type` and `--name` instead of `--pattern`** for all symbol searches. `--type class`
   matches declarations; `--type method` matches calls and definitions. Only use `--pattern` when
   you need a regex that doesn't map to a symbol type.
3. Use **multiple keywords** in `--query` — combine the module name, concept, operation, and type.
   Good: `"stripe payment disbursement payout reconciliation"`
   Bad:  `"stripe"` or `"payment"`
4. **Never use `grep -r`, `grep -rn`, or bash grep** to search source files. Use `ck search`
   instead. Bash grep bypasses semantic scoping and wastes tokens scanning irrelevant files.
5. Scope all subsequent Glob/Grep searches to the folder(s) returned — never use `**/*.cs` or
   `**/*.ts` from the repo root. Pass the returned folder as the `path` parameter to Glob/Grep.
6. Do NOT run a broad Grep or Glob across the repo as a supplement to CK results. If CK results
   don't cover what you need, run another `ck search` with different `--name` or `--type`.
7. Before reading ANY `.cs`, `.ts`, or `.tsx` file, run `ck signatures` first to confirm it
   contains what you need:
   ```bash
   .claude/skills/ck/ck signatures <folder-or-file>
   ```
   Pass the **folder path** to get all signatures in one call.
   Proceed to Read only after signatures output confirms the file is relevant.
8. After signatures identifies the right file and method, use `ck get-method-source` to read just
   that method — not the whole file:
   ```bash
   .claude/skills/ck/ck get-method-source <file> <MethodName>
   ```
   Fall back to a full Read only when you need 3+ members from the same file.
9. Never Read a `.cs`, `.ts`, or `.tsx` file speculatively. If you are not certain it is the
   right file, run signatures first.
10. **Do NOT use `find`, `ls`, or `Search` to enumerate files** after CK has returned results.
    Pass the folder directly to `ck signatures` — it recursively processes all supported files.
11. **Do NOT re-run `ck search` or `ck find-scope` with rephrased query text.** The semantic
    ranking is stable across synonym variations — rephrasing produces the same folders. If you
    got the right folders but wrong matches, change `--name`/`--type`. If the folders
    are wrong, use different domain vocabulary (class names, namespace segments), not synonyms.
12. **Do NOT pipe `ck search` output through `grep` or `head`.** The output is already structured.
    Filtering it discards folder scores and grouping. Use `--top` or `--min-score` to limit results.
    Piped CK commands will be **blocked** by the guard hook.
13. **`ck get-method-source <member-name>` is an exact name match**, not a keyword search.
    `Refund` will not match `RefundPaymentAsync`. Always get the exact name from
    `ck signatures` output before calling `get-method-source`.
