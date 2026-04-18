#!/usr/bin/env bash
# agent-usage-guard: SubagentStart hook.
# Injects the CK code search protocol into every sub-agent's context via
# the documented additionalContext field, so sub-agents use CK tools
# instead of broad grep/glob searches.
#
# Event: SubagentStart (fires when a subagent is spawned)
# Cannot block — only injects context.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

# Find the protocol file relative to this hook (.claude/hooks/ → .claude/rules/)
HOOK_DIR="$(cd "$(dirname "$0")" && pwd)"
PROTOCOL_FILE="$HOOK_DIR/../rules/ck-code-search-protocol.md"

if [[ ! -f "$PROTOCOL_FILE" ]]; then
  exit 0
fi

PROTOCOL=$(cat "$PROTOCOL_FILE")

CONTEXT="$(printf '## Code Search Protocol (mandatory)

%s

The ck binary is at: .claude/skills/ck/ck
Use ck find-scope, ck signatures, and ck get-method-source instead of broad grep/glob.' "$PROTOCOL")"

jq -n --arg ctx "$CONTEXT" '{
  "additionalContext": $ctx
}'
