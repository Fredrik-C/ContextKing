#!/usr/bin/env bash
# ck-read-guard: PreToolUse hook for the Read tool.
# When a .cs, .ts, or .tsx file is about to be read, asks whether ck signatures was run first.
# Never blocks the read — allows the agent to self-correct.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
FILE_PATH=$(printf '%s' "$INPUT" | jq -r '
  .tool_input.file_path //
  .tool_input.path //
  .toolInput.file_path //
  .toolInput.path //
  empty' 2>/dev/null)

# Only act on supported source files
case "$FILE_PATH" in
  *.cs|*.ts|*.tsx) ;;
  *) exit 0 ;;
esac

jq -n \
  --arg reason "[ck-guard] STOP — you are about to read '$(basename "$FILE_PATH")' in full.

Do NOT read the entire file. Use targeted extraction instead:

  1. Run signatures first (if you haven't already):
     .claude/skills/ck/ck signatures $FILE_PATH

  2. Then extract only the method you need:
     .claude/skills/ck/ck get-method-source $FILE_PATH <MethodName>

Full file reads waste tokens. Read the whole file ONLY when you need 3+ methods from it.
If you have already confirmed via signatures that this file is relevant AND you need
multiple members, proceed with the Read." \
  '{
    "hookSpecificOutput": {
      "hookEventName": "PreToolUse",
      "permissionDecision": "allow",
      "permissionDecisionReason": $reason
    }
  }'
