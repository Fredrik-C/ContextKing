#!/usr/bin/env bash
# ck-bash-grep-guard: PreToolUse hook for the Bash tool.
# When the command looks like a grep targeting .cs files, reminds the agent
# to follow the ck code-search protocol instead. Never blocks the command.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // .toolInput.command // empty' 2>/dev/null)

# Only act when the command contains grep and references .cs files
if ! printf '%s' "$COMMAND" | grep -qE 'grep' 2>/dev/null; then
  exit 0
fi
if ! printf '%s' "$COMMAND" | grep -qE '\.cs|--include.*cs|-r|-rn' 2>/dev/null; then
  exit 0
fi

jq -n \
  '{
    "hookSpecificOutput": {
      "hookEventName": "PreToolUse",
      "permissionDecision": "allow",
      "permissionDecisionReason": "[ck-guard] bash grep on C# files detected.\n\nDo NOT use bash grep to search this codebase — follow the code search protocol:\n  rules/ck-code-search-protocol.md\n\nShort version:\n  1. Run ck find-scope to locate the right folder\n  2. Run ck signatures before reading any .cs file\n  3. Use the Grep tool (not bash grep) only within the scoped folder"
    }
  }'
