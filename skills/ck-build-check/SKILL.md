---
name: ck-build-check
description: Run dotnet build with compact diagnostics output to reduce token-heavy log churn.
---

# ck build-check — Reference

## Syntax

```bash
.claude/skills/ck/ck build-check <project.csproj> [--max <n>] [--configuration <Debug|Release>] [--framework <tfm>] [--runtime <rid>] [--no-restore]
```

## What It Does

- Runs `dotnet build` with `-v q`.
- Prints concise counts and top diagnostics (errors/warnings).
- Suppresses long tail/grep build-log loops.

## Tips

- Use this as default verification command during implementation loops.
- If build fails without parseable diagnostics, rerun plain `dotnet build` only when you need full logs.
