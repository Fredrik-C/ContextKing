---
name: ck-forget
description: Remove a stale knowledge snippet by ID when previously learned context is no longer valid.
---

# ck forget — Reference

Removes one snippet from CK knowledge JSONL files by UUID.

## Syntax

```bash
.claude/skills/ck/ck forget --id <uuid> [--repo <path>]
```

## When to Use

- A snippet became incorrect after refactor or behaviour change.
- A snippet duplicates newer, clearer knowledge.
- A snippet was recorded against the wrong folder context.

## Typical Workflow

1. Run `ck recall --folder <path>` or `ck recall --query "<text>"`.
2. Copy the stale snippet ID from output.
3. Run `ck forget --id <uuid>`.
4. Optionally add replacement context with `ck learn`.

## Notes

- This is a targeted delete of one snippet, not a full reset.
- Silent no-op when CK Brain is disabled (`"brain": false` in `.ck.json`).
