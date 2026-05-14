#!/usr/bin/env bash
set -euo pipefail

payload="$(cat)"

if command -v jq >/dev/null 2>&1; then
  cmd="$(printf '%s' "$payload" | jq -r '.tool_input.command // ""')"
else
  cmd="$(printf '%s' "$payload" | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
fi

if [ -z "$cmd" ]; then
  exit 0
fi

# Guard 1: preserve CK ranking output shape.
if printf '%s' "$cmd" | grep -Eiq '(^|[[:space:]])ck[[:space:]]+find-files([^|;]|$).*\|[[:space:]]*(head|tail|grep|awk|sed)\b'; then
  cat <<'JSON'
{"decision":"deny","reason":"Do not pipe ck find-files output. Ranking and signals are lost.","additionalContext":"Use `ck find-files --query \"...\" --top 20` directly, then continue with `ck find-symbol` / `ck get-method-source`."}
JSON
  exit 2
fi

# Guard 2: avoid broad source grep before CK scoping.
if printf '%s' "$cmd" | grep -Eiq '\b(rg|grep|find)\b' &&
   printf '%s' "$cmd" | grep -Eiq '\.(cs|ts|tsx)\b|src/|lib/|app/'; then
  if ! printf '%s' "$cmd" | grep -Eiq '\-\-path[[:space:]]+|[[:space:]](src/[^[:space:]]+|lib/[^[:space:]]+|app/[^[:space:]]+)'; then
    cat <<'JSON'
{"decision":"deny","reason":"Broad source search blocked. Scope with Context King first.","additionalContext":"Run `ck find-files --query \"<domain terms>\" --top 20` first, then narrow with `ck find-symbol` or `ck get-method-source` in the confirmed folder."}
JSON
    exit 2
  fi
fi

exit 0
