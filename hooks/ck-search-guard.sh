#!/usr/bin/env bash
# ck-search-guard: PreToolUse hook for the built-in Grep and Glob tools.
#
# These tools bypass the Bash hook, so the bash guard cannot catch them.
# This guard blocks two patterns:
#
#   Glob **/*.cs|ts|tsx  — broad source-file discovery without a narrow path prefix
#   Grep with include *.cs|ts|tsx — source search across a broad (unscoped) path
#
# Both are ALLOWED only when CK navigation state is established:
#   - file-first boundaries from ck find-files, or
#   - fallback scoped folders from ck find-files
# Then Grep/Glob must stay inside those boundaries.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)

[ -z "$TOOL" ] && exit 0
[ "$TOOL" != "Grep" ] && [ "$TOOL" != "Glob" ] && exit 0

# Block raw Grep/Glob access to the knowledge JSONL.
PATTERN_RAW=$(printf '%s' "$INPUT" | jq -r '.tool_input.pattern // empty' 2>/dev/null)
PATH_RAW=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .tool_input.cwd // empty' 2>/dev/null)
INCLUDE_RAW=$(printf '%s' "$INPUT" | jq -r '.tool_input.include // empty' 2>/dev/null)
if printf '%s\n%s\n%s\n' "$PATTERN_RAW" "$PATH_RAW" "$INCLUDE_RAW" | grep -qiE '\.ck-knowledge[/\\].*\.jsonl'; then
  jq -n --arg reason "[ck-guard] BLOCKED — direct Grep/Glob access to CK knowledge JSONL files is not allowed.

Use CK commands instead:
  ck recall --folder <path>
  ck learn --content \"...\" --folders \"...\"
  ck forget --id <uuid>" \
    '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":$reason}}'
  exit 0
fi

GUIDE_MSG='[ck-guard] ALLOW (guidance) — source search works better with file-first boundaries.

Before Grep/Glob searching in source files, run:

  ck get-keyword-map --query "<what you are looking for>"
  ck find-files --query "<what you are looking for>" --path src/

If results are weak/noisy, fallback to:
  ck get-keyword-map --query "<what you are looking for>"
  ck find-files --query "<what you are looking for>"

Then keep Grep/Glob paths inside those returned paths/folders.'

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
[ -f "$REPO_ROOT/.ck.json" ] || exit 0

STATE_FILE="$REPO_ROOT/.ck-index/.ck-guard-state.json"
scoped_folders=()
if [ -f "$STATE_FILE" ]; then
  while IFS= read -r folder; do
    [ -n "$folder" ] && scoped_folders+=("$folder")
  done < <(jq -r '.scopedFolders[]? // empty' "$STATE_FILE" 2>/dev/null)
fi

normalize_path() {
  local path="$1"
  path="${path#./}"
  path="${path%/}"
  printf '%s' "$path"
}

is_within_scoped_folders() {
  local path
  path="$(normalize_path "$1")"
  [ -z "$path" ] && return 1
  for folder in "${scoped_folders[@]}"; do
    local norm
    norm="$(normalize_path "$folder")"
    [ -z "$norm" ] && continue
    if [ "$path" = "$norm" ] || [[ "$path" == "$norm/"* ]]; then
      return 0
    fi
  done
  return 1
}

# Helper: true when path has ≥2 non-empty segments (e.g. src/Modules/Inventory)
is_narrow_path() {
  local path="$1"
  [ -z "$path" ] && return 1
  path="${path#./}"
  path="${path%/}"
  local slash_count
  slash_count=$(printf '%s' "$path" | tr -cd '/' | wc -c)
  [ "$slash_count" -ge 1 ]
}

# Extract the static prefix of a glob pattern — everything before the first wildcard.
# "src/Modules/Inventory/**/*.cs" → "src/Modules/Inventory"
glob_static_prefix() {
  local pattern="$1"
  printf '%s' "$pattern" | sed 's/[*?{[].*$//' | sed 's|/*$||'
}

# ── Glob: source file pattern without a narrow static prefix ─────────────────
if [ "$TOOL" = "Glob" ]; then
  PATTERN=$(printf '%s' "$INPUT" | jq -r '.tool_input.pattern // empty' 2>/dev/null)
  if printf '%s' "$PATTERN" | grep -qE '\.(cs|ts|tsx)$'; then
    PREFIX="$(glob_static_prefix "$PATTERN")"
    PATH_ARG=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // empty' 2>/dev/null)
    if [ "${#scoped_folders[@]}" -eq 0 ]; then
      jq -n --arg reason "$GUIDE_MSG" \
        '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":$reason}}'
      exit 0
    fi
    if [ "${#scoped_folders[@]}" -gt 0 ]; then
      local_target="$PATH_ARG"
      [ -z "$local_target" ] && local_target="$PREFIX"
      if ! is_within_scoped_folders "$local_target"; then
        jq -n --arg reason "[ck-guard] ALLOW (guidance) — Glob path is outside current CK boundaries (find-files)." \
          '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":$reason}}'
        exit 0
      fi
    fi
  fi
fi

# ── Grep: source file include without a narrow path ──────────────────────────
if [ "$TOOL" = "Grep" ]; then
  INCLUDE=$(printf '%s' "$INPUT" | jq -r '.tool_input.include // empty' 2>/dev/null)
  if printf '%s' "$INCLUDE" | grep -qE '\*\.(cs|ts|tsx)$'; then
    PATH_ARG=$(printf '%s' "$INPUT" | jq -r '.tool_input.path // .tool_input.cwd // empty' 2>/dev/null)
    if [ "${#scoped_folders[@]}" -eq 0 ]; then
      jq -n --arg reason "$GUIDE_MSG" \
        '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":$reason}}'
      exit 0
    fi
    if [ "${#scoped_folders[@]}" -gt 0 ]; then
      if ! is_within_scoped_folders "$PATH_ARG"; then
        jq -n --arg reason "[ck-guard] ALLOW (guidance) — Grep path is outside current CK boundaries (find-files)." \
          '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":$reason}}'
        exit 0
      fi
    fi
  fi
fi
