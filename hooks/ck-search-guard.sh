#!/usr/bin/env bash
# ck-search-guard: PreToolUse hook for Glob and Grep tools.
# When searching for .cs files across a wide, unscoped path, reminds the agent
# to run ck find-scope first. Never blocks the search.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // .toolName // empty' 2>/dev/null)

# ── Glob ──────────────────────────────────────────────────────────────────────
if [[ "$TOOL" == "Glob" ]]; then
  PATTERN=$(printf '%s' "$INPUT" | jq -r '.tool_input.pattern // .toolInput.pattern // empty' 2>/dev/null)
  SEARCH_PATH=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .toolInput.path // empty' 2>/dev/null)

  # Only act on .cs glob patterns
  [[ "$PATTERN" != *".cs"* ]] && exit 0

  # Count path depth: scoped searches (depth > 3) don't need the hint
  DEPTH=$(echo "$SEARCH_PATH" | tr '/' '\n' | grep -c '[^[:space:]]' 2>/dev/null || echo 0)
  [[ "$DEPTH" -gt 3 ]] && exit 0

  jq -n \
    --arg reason "[ck-guard] Globbing for C# files across a wide path (pattern: '$PATTERN', path: '${SEARCH_PATH:-(repo root)}').

Run ck find-scope FIRST to narrow scope:
  .claude/skills/ck/ck find-scope --query \"<multi-keyword description — module, concept, operation, type>\"

Then scope this search to the returned folder path.
Proceed only if the scope is already narrowed to a specific folder." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

# ── Grep ──────────────────────────────────────────────────────────────────────
if [[ "$TOOL" == "Grep" ]]; then
  GLOB=$(printf '%s' "$INPUT" | jq -r '.tool_input.glob // .toolInput.glob // empty' 2>/dev/null)
  TYPE=$(printf '%s' "$INPUT" | jq -r '.tool_input.type // .toolInput.type // empty' 2>/dev/null)
  SEARCH_PATH=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .toolInput.path // empty' 2>/dev/null)

  # Only act when targeting C# files
  IS_CS=0
  [[ "$GLOB" == *".cs"* ]] && IS_CS=1
  [[ "$TYPE" == "cs" ]]    && IS_CS=1
  [[ "$IS_CS" -eq 0 ]]     && exit 0

  # Count path depth
  DEPTH=$(echo "$SEARCH_PATH" | tr '/' '\n' | grep -c '[^[:space:]]' 2>/dev/null || echo 0)
  [[ "$DEPTH" -gt 3 ]] && exit 0

  jq -n \
    --arg reason "[ck-guard] Grepping C# files across a wide path (path: '${SEARCH_PATH:-(repo root)}').

Run ck find-scope FIRST to narrow scope:
  .claude/skills/ck/ck find-scope --query \"<multi-keyword description — module, concept, operation, type>\"

Then scope this Grep to the returned folder path.
Proceed only if the scope is already narrowed to a specific folder." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi
