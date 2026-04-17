#!/usr/bin/env bash
# ck-bash-guard: PreToolUse hook for the Bash tool.
# Detects two anti-patterns:
#   1. Piping ck output through head/grep/tail/wc (wastes context, discards structure)
#   2. Using raw grep/rg on source files instead of ck search
# Never blocks — allows with a corrective hint.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | jq -r '
  .tool_input.command //
  .toolInput.command //
  empty' 2>/dev/null)

[ -z "$COMMAND" ] && exit 0

# ── Pattern 1: ck commands piped through head/grep/tail ───────────────────────
if printf '%s' "$COMMAND" | grep -qE 'ck\s+(search|find-scope|signatures|get-method-source)\b.*\|'; then
  jq -n \
    --arg reason "[ck-guard] Do NOT pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  • Reduce output with --top <n> or --min-score <f>
  • Use --type and --name for precise matching:
    ck search --query \"<scope>\" --type class --name \"ClassName\"
  • Types: class, method, member, file

Remove the pipe and run the ck command directly." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

# ── Pattern 2: raw grep/rg on source files ────────────────────────────────────
if printf '%s' "$COMMAND" | grep -qE '(^|\s)(grep|rg)\s+.*\.(cs|ts|tsx)\b'; then
  jq -n \
    --arg reason "[ck-guard] bash grep on C# files detected.

Do NOT use bash grep to search this codebase — follow the code search protocol:

  1. .claude/skills/ck/ck find-scope --query \"<module, concept, operation, type>\"
  2. .claude/skills/ck/ck signatures <file.cs> [file2.cs ...]
  3. .claude/skills/ck/ck get-method-source <file.cs> <MemberName>

Use the native grep tool (not bash grep) only within a scoped folder." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi
