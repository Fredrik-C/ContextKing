## Context King — code search protocol

This repo has Context King installed for fast C# and TypeScript/TSX navigation.
Full reference: `.opencode/ck-code-search-protocol.md`

### Mandatory workflow for .cs / .ts / .tsx files

```
1. SCOPE   → .opencode/skills/ck/ck find-scope --query "domain area concept operation"
2. EXPLORE → .opencode/skills/ck/ck expand-folder --pattern "<keyword>" <folder>
3. READ    → .opencode/skills/ck/ck get-method-source <file> <MemberName>
4. EDIT    → make your changes
```

Use `ck signatures <folder>` at step 2 only when you need all members with no filter.
Do not read source files before running step 1.
