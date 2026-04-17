#!/usr/bin/env bash
# ck-search-guard: PreToolUse hook for Glob and Grep tools.
# When searching for source files (.cs, .ts, .tsx) across a wide, unscoped path,
# reminds the agent to run ck find-scope first. Never blocks the search.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // .toolName // empty' 2>/dev/null)

# ── Glob ──────────────────────────────────────────────────────────────────────
if [[ "$TOOL" == "Glob" ]]; then
  PATTERN=$(printf '%s' "$INPUT" | jq -r '.tool_input.pattern // .toolInput.pattern // empty' 2>/dev/null)
  SEARCH_PATH=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .toolInput.path // empty' 2>/dev/null)

  # Only act on source file glob patterns
  case "$PATTERN" in
    *.cs*|*.ts*|*.tsx*) ;;
    *) exit 0 ;;
  esac

  # Count path depth: scoped searches (depth > 3) don't need the hint
  DEPTH=$(echo "$SEARCH_PATH" | tr '/' '\n' | grep -c '[^[:space:]]' 2>/dev/null || echo 0)
  [[ "$DEPTH" -gt 3 ]] && exit 0

  jq -n \
    --arg reason "[ck-guard] Broad source file glob detected (pattern: '$PATTERN', path: '${SEARCH_PATH:-(repo root)}').

Use ck search to find what you need with semantic scoping:
  .claude/skills/ck/ck search --query \"<domain description>\" --pattern \"<keyword>\"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  .claude/skills/ck/ck find-scope --query \"<multi-keyword description>\"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files." \
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
  INCLUDE=$(printf '%s' "$INPUT" | jq -r '.tool_input.include // .toolInput.include // empty' 2>/dev/null)
  SEARCH_PATH=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .toolInput.path // empty' 2>/dev/null)

  # Only act when targeting source files
  IS_SOURCE=0
  case "$GLOB" in *.cs*|*.ts*|*.tsx*) IS_SOURCE=1 ;; esac
  case "$INCLUDE" in *.cs*|*.ts*|*.tsx*) IS_SOURCE=1 ;; esac
  [[ "$TYPE" == "cs" || "$TYPE" == "ts" || "$TYPE" == "tsx" ]] && IS_SOURCE=1
  [[ "$IS_SOURCE" -eq 0 ]] && exit 0

  # Count path depth
  DEPTH=$(echo "$SEARCH_PATH" | tr '/' '\n' | grep -c '[^[:space:]]' 2>/dev/null || echo 0)
  [[ "$DEPTH" -gt 3 ]] && exit 0

  jq -n \
    --arg reason "[ck-guard] Broad source file grep detected (path: '${SEARCH_PATH:-(repo root)}').

Use ck search to find what you need with semantic scoping:
  .claude/skills/ck/ck search --query \"<domain description>\" --pattern \"<keyword>\"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  .claude/skills/ck/ck find-scope --query \"<multi-keyword description>\"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi
