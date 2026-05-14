---
name: ck-read-full-file
description: Read a complete C#, TypeScript, Kotlin, and Python source file when full-file context is required, with a large-file guardrail and explicit override.
---

# ck read-full-file — Reference

## Syntax

```bash
.claude/skills/ck/ck read-full-file <file> [--max-lines <n>] [--allow-large]
```

## Guardrail behavior

- Default guardrail threshold is `300` lines.
- If the file is larger than the threshold, the command refuses and suggests targeted CK reads.
- To proceed anyway, rerun with `--allow-large` (or `--force`).

## Examples

```bash
.claude/skills/ck/ck read-full-file src/Modules/Payments/Service.cs
.claude/skills/ck/ck read-full-file --max-lines 200 src/Modules/Payments/Service.cs
.claude/skills/ck/ck read-full-file --allow-large src/Modules/Payments/Service.cs
```

## Tips

- Prefer `ck get-method-source`, `ck get-type-source`, and `ck get-usings` first.
- Use this when full-file context is genuinely required (cross-member flow, file-level comments, nested type layout).
