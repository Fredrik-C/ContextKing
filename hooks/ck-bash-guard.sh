#!/usr/bin/env bash
# ck-bash-guard: PreToolUse hook for the Bash tool.
# Detects two anti-patterns:
#   1. Piping ck output through head/grep/tail/wc (wastes context, discards structure)
#   2. Using raw grep/rg on source files instead of ck search
#
# Pattern 1 BLOCKS the call — allow-with-warning was tested and agents ignore it.
# Pattern 2 allows with a hint (blocking grep entirely is too aggressive).

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | jq -r '
  .tool_input.command //
  .toolInput.command //
  empty' 2>/dev/null)

[ -z "$COMMAND" ] && exit 0

# ── Pattern 1: ck search/find-scope piped through head/grep/tail ──────────────
# Block pipes on search and find-scope (destroys folder scores and grouping).
# Allow pipes on signatures and get-method-source (filtering large output is fine).
if printf '%s' "$COMMAND" | grep -qE 'ck\s+(search|find-scope)\b' && \
   printf '%s' "$COMMAND" | grep -qE '\|\s*(head|tail|grep|wc|sort|awk|sed|cut|less|more)\b'; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — do not pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  • Reduce output with --top <n> or --min-score <f>
  • Use --type and --name for precise matching:
    ck search --query \"<scope>\" --type class --name \"ClassName\"
  • Types: class, method, member, file

Remove the pipe and re-run the ck command directly." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

# ── Pattern 2: raw grep/rg on a specific source file ──────────────────────────
# When grepping a known file, the agent should use get-method-source instead.
# This is blocked (deny) because it always has a better CK alternative.
if printf '%s' "$COMMAND" | grep -qE '(^|\s)(grep|rg)\s+.*[^/]\.(cs|ts|tsx)\b' && \
   ! printf '%s' "$COMMAND" | grep -qE '(grep|rg)\s+-[a-zA-Z]*r'; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — use CK AST tools instead of grep on a known file.

You already know the file. Use:

  .claude/skills/ck/ck signatures <file.cs>              # list all members
  .claude/skills/ck/ck get-method-source <file.cs> <Name> # read one method

These return structured output with exact line spans. grep loses context." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

# ── Pattern 3: raw grep -r on source files (broad search) ─────────────────────
if printf '%s' "$COMMAND" | grep -qE '(^|\s)(grep|rg)\s+.*\.(cs|ts|tsx)\b'; then
  jq -n \
    --arg reason "[ck-guard] bash grep on source files detected.

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
