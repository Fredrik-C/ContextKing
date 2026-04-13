---
name: ck-index
description: Build or update the Context King source-map index. Rarely needed explicitly — ck find-scope auto-builds on first use. Useful after branch switches or to force a full rebuild.
---

# ck index — Source Map Index Management

Under normal usage the index is built automatically on the first call to `ck find-scope`.
Use this skill explicitly when you need precise control over the index state.

## When to use explicitly

- After `git checkout <branch>` in the same worktree, to refresh the index without waiting for the
  next `ck find-scope` call.
- After a large merge or rebase that changed many files.
- To force a full rebuild (`--force`) if the index appears incorrect.
- To check whether the index is current without triggering a search.

## Commands

**Mac / Linux:**
```bash
# Check status
.opencode/skills/ck/ck index --status

# Incremental update (only changed folders re-embedded)
.opencode/skills/ck/ck index

# Full rebuild
.opencode/skills/ck/ck index --force
```

**Windows (PowerShell):**
```powershell
.opencode\skills\ck\ck.cmd index [--status] [--force]
```

## Options

| Option | Description |
|---|---|
| `--status` | Print `fresh`, `stale`, or `missing` and exit |
| `--force` | Delete existing index and rebuild from scratch |
| `--repo <path>` | Repo root (defaults to `git rev-parse --show-toplevel`) |

## Index properties

- Stored at `<worktree-root>/.ck-index/index.db` (gitignored).
- Each linked worktree has its own index; no action needed when switching between worktrees.
- Incremental update only re-embeds folders where any tracked file's git object hash changed.
- Index is per-worktree; `git checkout` in the same worktree makes it stale.
