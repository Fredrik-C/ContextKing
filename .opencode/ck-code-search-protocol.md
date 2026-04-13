## Code Search Protocol — mandatory

This codebase has a large number of C# files across many folders. Searching without narrowing scope
first produces false positives, wastes tokens, and reads the wrong files.

**Before any Glob, Grep, or Read on an unknown file location, run `ck find-scope`:**

Mac / Linux / Git Bash:
```bash
.opencode/skills/ck/ck find-scope --query "<multi-keyword description of what you need>"
```

Windows PowerShell / cmd:
```powershell
.opencode\skills\ck\ck.cmd find-scope --query "<multi-keyword description of what you need>"
```

Rules:
1. Always run `ck find-scope` first when the relevant folder is not already known.
2. Use **multiple keywords** — combine the module name, concept, operation, and type.
   Good: `"stripe payment disbursement payout reconciliation"`
   Bad:  `"stripe"` or `"payment"`
3. Scope all subsequent Glob/Grep searches to the folder(s) returned — never use `**/*.cs` from
   the repo root after find-scope has identified the area. Pass the returned folder as the `path`
   parameter to Glob/Grep.
4. Do NOT run a broad Grep or Glob across the repo as a supplement to find-scope. If you need to
   search for a symbol, scope it to the returned folder.
5. Process find-scope results before issuing more find-scope queries. Run one query, inspect the
   returned folders, proceed to signatures — only issue a second find-scope if the first results
   did not cover the area you need.
6. Before reading ANY `.cs` file, run `ck signatures` first to confirm it contains what you need:

   Mac / Linux / Git Bash:
   ```bash
   .opencode/skills/ck/ck signatures <file.cs>
   ```
   Windows PowerShell / cmd:
   ```powershell
   .opencode\skills\ck\ck.cmd signatures <file.cs>
   ```
   Proceed to Read only after signatures output confirms the file is relevant.
7. After signatures identifies the right file and method, use `ck get-method-source` to read just
   that method — not the whole file:

   Mac / Linux / Git Bash:
   ```bash
   .opencode/skills/ck/ck get-method-source <file.cs> <MethodName>
   ```
   Fall back to a full Read only when you need several members from the same file.
8. Never Read a `.cs` file speculatively. If you are not certain it is the right file, run signatures first.
