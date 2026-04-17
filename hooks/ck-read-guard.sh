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
  --arg reason "[ck-guard] About to read '$(basename "$FILE_PATH")' in full.

Have you run 'ck signatures' on this file yet?
If not, do it now — it shows all method/property signatures without reading the full file:
  .claude/skills/ck/ck signatures $FILE_PATH

Proceed with Read only after signatures output has confirmed this file contains
what you need. Reading the wrong file wastes tokens and context." \
  '{
    "hookSpecificOutput": {
      "hookEventName": "PreToolUse",
      "permissionDecision": "allow",
      "permissionDecisionReason": $reason
    }
  }'
