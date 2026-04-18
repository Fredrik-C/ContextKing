#!/usr/bin/env bash
# agent-usage-guard: PreToolUse hook for the Agent tool.
# When a sub-agent is about to be launched for code navigation tasks,
# injects the CK code search protocol into the sub-agent's prompt so it
# uses CK tools natively instead of doing expensive unguided searches.
# Never blocks — allows the agent to proceed with the enriched prompt.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
PROMPT=$(printf '%s' "$INPUT" | jq -r '.tool_input.prompt // .toolInput.prompt // empty' 2>/dev/null)
DESC=$(printf '%s' "$INPUT" | jq -r '.tool_input.description // .toolInput.description // empty' 2>/dev/null)

# Only act when the agent appears to be doing code search/navigation
if ! printf '%s\n%s' "$PROMPT" "$DESC" | grep -qiE '(\.cs\b|find|search|explore|scope|controller|component|service|repository|business|layer|interface|namespace)'; then
  exit 0
fi

# Find the protocol file relative to this hook (.claude/hooks/ → .claude/rules/)
HOOK_DIR="$(cd "$(dirname "$0")" && pwd)"
PROTOCOL_FILE="$HOOK_DIR/../rules/ck-code-search-protocol.md"

if [[ ! -f "$PROTOCOL_FILE" ]]; then
  # Protocol not found — fall back to a plain guidance message
  jq -n \
    --arg reason "[ck-guard] Use ck find-scope before delegating navigation to a sub-agent." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

PROTOCOL=$(cat "$PROTOCOL_FILE")

# Prepend the CK protocol to the sub-agent prompt so it uses CK tools natively.
MODIFIED_PROMPT="$(printf 'The following code search protocol is mandatory for this task:\n\n%s\n\nThe ck binary is at: .claude/skills/ck/ck (Mac/Linux/Git Bash) or .claude\\\\skills\\\\ck\\\\ck.cmd (Windows)\n\n---\n\n%s' "$PROTOCOL" "$PROMPT")"

# Rebuild tool input with modified prompt, preserving all other fields
TOOL_INPUT=$(printf '%s' "$INPUT" | jq '.tool_input // .toolInput')
UPDATED_INPUT=$(printf '%s' "$TOOL_INPUT" | jq --arg p "$MODIFIED_PROMPT" '.prompt = $p')

jq -n \
  --arg reason "[ck-guard] CK code search protocol injected into sub-agent prompt — sub-agent will use ck tools instead of broad searches." \
  --argjson updatedInput "$UPDATED_INPUT" \
  '{
    "hookSpecificOutput": {
      "hookEventName": "PreToolUse",
      "permissionDecision": "allow",
      "permissionDecisionReason": $reason,
      "updatedInput": $updatedInput
    }
  }'
