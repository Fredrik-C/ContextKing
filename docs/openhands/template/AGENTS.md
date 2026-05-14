# Context King Protocol (OpenHands)

This repository uses Context King (`ck`) for C#/TypeScript navigation. Use CK tools first and avoid broad grep/glob across the repo.

## Mandatory navigation flow

1. `ck find-files --query "<domain terms>" --top 20 [--path <scope>]`
2. `ck find-symbol "<TypeOrMember>" --path <folder-or-file>` (or `ck signatures <folder>`)
3. `ck get-method-source <file> <member>` (or `ck get-type-source <file> <type>`)
4. Edit
5. `ck learn` when you discover non-obvious architecture/domain knowledge

## Rules

- Do not pipe `ck find-files` output through `head|tail|grep|awk|sed`.
- Prefer `ck get-method-source` / `ck get-type-source` over full-file reads.
- Use `ck expand-folder` only after `ck find-files` if results are weak/noisy.
- Use `grep`/`rg` only inside a confirmed narrow folder scope.
