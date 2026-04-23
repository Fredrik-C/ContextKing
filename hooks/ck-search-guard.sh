#!/usr/bin/env bash
# ck-search-guard: PreToolUse hook for the built-in Grep and Glob tools.
#
# These tools bypass the Bash hook entirely, so the bash guard cannot catch them.
# This guard blocks two patterns:
#
#   Glob **/*.cs|ts|tsx  — broad source-file discovery (always wrong; use ck find-scope)
#   Grep with include *.cs|ts|tsx — content search across source files (use ck expand-folder)
#
# Both patterns indicate the agent is trying to find or explore source files without
# first running ck find-scope to scope the search.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)

[ -z "$TOOL" ] && exit 0
[ "$TOOL" != "Grep" ] && [ "$TOOL" != "Glob" ] && exit 0

DENY_MSG='[ck-guard] BLOCKED — run ck find-scope before searching source files.

You are trying to search .cs/.ts/.tsx files across a broad path.
Use CK to scope and explore instead:

  .claude/skills/ck/ck find-scope --query "<what you are looking for>"
  .claude/skills/ck/ck expand-folder --pattern "<keyword>" <returned-folder>

find-scope ranks folders semantically. expand-folder filters files and signatures
within a folder by keyword. Together they replace broad Grep/Glob discovery.'

# ── Glob: **/*.cs|ts|tsx at any depth ────────────────────────────────────────
if [ "$TOOL" = "Glob" ]; then
  PATTERN=$(printf '%s' "$INPUT" | jq -r '.tool_input.pattern // empty' 2>/dev/null)
  if printf '%s' "$PATTERN" | grep -qE '\*\*?/\*\.(cs|ts|tsx)$'; then
    jq -n --arg reason "$DENY_MSG" \
      '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":$reason}}'
    exit 0
  fi
fi

# ── Grep: include filter targets source files ─────────────────────────────────
# After ck find-scope you use ck expand-folder (not Grep) to explore a folder.
# Cross-reference greps go through Bash grep -rn, which the bash guard allows.
# The built-in Grep tool with a source-file include is therefore always avoidable.
if [ "$TOOL" = "Grep" ]; then
  INCLUDE=$(printf '%s' "$INPUT" | jq -r '.tool_input.include // empty' 2>/dev/null)
  if printf '%s' "$INCLUDE" | grep -qE '\*\.(cs|ts|tsx)$'; then
    jq -n --arg reason "$DENY_MSG" \
      '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":$reason}}'
    exit 0
  fi
fi
