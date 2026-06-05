---
name: ck-init
description: Initialize Context King in the current repository, or migrate a repo from a legacy per-repo deployment to the global install.
---

# ck init — Repository Initialization

Run once per repository after installing Context King globally. Do not confuse with
`ck index` (which builds the semantic search index).

## Commands

**Initialize a new repo:**
```bash
.claude/skills/ck/ck init
```

Creates `.ck.json` (minimum version requirement and default `findFiles` settings), adds `.ck-index/` to `.gitignore`,
and creates the `.ck-knowledge/` directory. Commit these files to share the setup
with your team.

**Migrate a legacy repo** (previously set up with the old per-repo `deploy.sh`):
```bash
.claude/skills/ck/ck init --migrate
```

Detects and removes per-repo artifacts (binary, model, hook scripts, rule file) and
cleans up relative-path hook registrations and CK `allowedTools` entries from
`.claude/settings.json`, leaving all non-CK content intact.

## Execution Protocol

When the user asks for `ck init`:

1. Run `ck init` directly first.
2. Do not pre-check `.claude/settings.json`, `.ck`, or other legacy artifacts unless:
   - the user explicitly asked for migration, or
   - `ck init` output indicates legacy deployment conflicts.
3. Only run `ck init --migrate` when explicitly requested by the user (or after confirming migration is needed from command output).

## Options

| Option | Description |
|---|---|
| `--migrate` | Remove legacy per-repo deploy artifacts and clean up settings.json |
| `--force` | Re-initialize even if `.ck.json` already exists |
| `--quiet` | Suppress informational output |

## After init

Commit `.ck.json` and `.ck-knowledge/` to the repository. The `.ck-index/` directory
is gitignored and will be built automatically on the first `ck find-files` call.
