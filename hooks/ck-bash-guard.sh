#!/usr/bin/env bash
# ck-bash-guard: PreToolUse hook for the Bash tool.
# Enforces the CK navigation protocol by blocking repo-wide search anti-patterns
# and enforcing CK workflow boundaries (file-first by default).
#
# grep/rg/glob are allowed freely within boundaries from find-files.
# Blocked: piping ck output through filters, broad recursive grep/find from source
# roots, full-file bulk reads (find -exec cat), and repeated/premature scope calls.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | jq -r '
  .tool_input.command //
  .toolInput.command //
  empty' 2>/dev/null)

[ -z "$COMMAND" ] && exit 0

emit_guard_json() {
  local decision="$1"
  local reason="$2"
  jq -n \
    --arg decision "$decision" \
    --arg reason "$reason" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": $decision,
        "permissionDecisionReason": $reason
      }
    }'
}

# ── Knowledge JSONL guardrail ────────────────────────────────────────────────
# Prevent direct raw reads/writes of the knowledge store from shell tools.
# Backfill and persistence are centralized in ck commands.
if printf '%s' "$COMMAND" | grep -qE '(^|[[:space:]])((\./)?\.ck-knowledge[/\\].*\.jsonl)([[:space:]]|$)'; then
  if ! printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])([^[:space:]]*/)?ck(\.exe)?[[:space:]]'; then
    jq -n \
      --arg reason "[ck-guard] BLOCKED — direct access to CK knowledge JSONL files is not allowed.

Use CK commands so migration/backfill and writes stay centralized in CLI:
  ck recall --folder <path>
  ck learn --content \"...\" --folders \"...\"
  ck forget --id <uuid>" \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": $reason
        }
      }'
    exit 0
  fi
fi

# ── Stateful anti-loop guards ───────────────────────────────────────────────
REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
[ -f "$REPO_ROOT/.ck.json" ] || exit 0

STATE_DIR="$REPO_ROOT/.ck-index"
STATE_FILE="$STATE_DIR/.ck-guard-state.json"

pending_keyword_map=false
keyword_map_seen=false
pending_query=""
last_find_scope=""
last_expand_folder=""
no_match_folder=""
no_match_count=0
known_target_file=""
known_target_from=""
expand_folder_count=0
signatures_folder_count=0
scoped_folders=()
recent_search_token=""
recent_search_count=0
recent_search_first_ts=0
last_build_check_command=""
last_build_check_ts=0
last_build_check_tree=""

if [ -f "$STATE_FILE" ]; then
  pending_keyword_map=$(jq -r '.pendingKeywordMap // false' "$STATE_FILE" 2>/dev/null)
  keyword_map_seen=$(jq -r '.keywordMapSeen // false' "$STATE_FILE" 2>/dev/null)
  pending_query=$(jq -r '.pendingQuery // ""' "$STATE_FILE" 2>/dev/null)
  last_find_scope=$(jq -r '.lastFindFilesCommand // ""' "$STATE_FILE" 2>/dev/null)
  last_expand_folder=$(jq -r '.lastExpandFolderCommand // ""' "$STATE_FILE" 2>/dev/null)
  no_match_folder=$(jq -r '.noMatchFolder // ""' "$STATE_FILE" 2>/dev/null)
  no_match_count=$(jq -r '.noMatchCount // 0' "$STATE_FILE" 2>/dev/null)
  known_target_file=$(jq -r '.knownTargetFile // ""' "$STATE_FILE" 2>/dev/null)
  known_target_from=$(jq -r '.knownTargetFrom // ""' "$STATE_FILE" 2>/dev/null)
  expand_folder_count=$(jq -r '.expandFolderCount // 0' "$STATE_FILE" 2>/dev/null)
  signatures_folder_count=$(jq -r '.signaturesFolderCount // 0' "$STATE_FILE" 2>/dev/null)
  recent_search_token=$(jq -r '.recentSearchToken // ""' "$STATE_FILE" 2>/dev/null)
  recent_search_count=$(jq -r '.recentSearchCount // 0' "$STATE_FILE" 2>/dev/null)
  recent_search_first_ts=$(jq -r '.recentSearchFirstTs // 0' "$STATE_FILE" 2>/dev/null)
  last_build_check_command=$(jq -r '.lastBuildCheckCommand // ""' "$STATE_FILE" 2>/dev/null)
  last_build_check_ts=$(jq -r '.lastBuildCheckTs // 0' "$STATE_FILE" 2>/dev/null)
  last_build_check_tree=$(jq -r '.lastBuildCheckTree // ""' "$STATE_FILE" 2>/dev/null)
  while IFS= read -r folder; do
    [ -n "$folder" ] && scoped_folders+=("$folder")
  done < <(jq -r '.scopedFolders[]? // empty' "$STATE_FILE" 2>/dev/null)
fi

normalize_path_token() {
  local path="$1"
  path="${path#./}"
  path="${path%/}"
  printf '%s' "$path"
}

is_within_scoped_folders() {
  local raw="$1"
  local path
  path="$(normalize_path_token "$raw")"
  [ -z "$path" ] && return 1
  for folder in "${scoped_folders[@]}"; do
    local norm
    norm="$(normalize_path_token "$folder")"
    [ -z "$norm" ] && continue
    if [ "$path" = "$norm" ] || [[ "$path" == "$norm/"* ]]; then
      return 0
    fi
  done
  return 1
}

extract_command_paths() {
  local cmd="$1"
  printf '%s\n' "$cmd" \
    | grep -oE '(\.?/)?src(/[[:alnum:]_.-]+)+' \
    | sed 's|^\./||' \
    | awk 'length($0)>0' \
    | sort -u
}

extract_search_token_family() {
  local cmd="$1"
  local token=""
  if printf '%s' "$cmd" | grep -qE '(^|[;&|[:space:]])(grep|rg)\b'; then
    token="$(printf '%s\n' "$cmd" | sed -nE 's/.*(grep|rg)[^"]*"([^"]{3,})".*/\2/p' | head -n 1)"
    [ -z "$token" ] && token="$(printf '%s\n' "$cmd" | sed -nE "s/.*(grep|rg)[^']*'([^']{3,})'.*/\2/p" | head -n 1)"
    [ -z "$token" ] && token="$(printf '%s\n' "$cmd" | awk '
      {for(i=1;i<=NF;i++){
        if($i=="grep"||$i=="rg"){
          for(j=i+1;j<=NF;j++){
            if($j ~ /^-/) continue;
            print $j; exit
          }
        }
      }}')"
  elif printf '%s' "$cmd" | grep -qE '(^|[;&|[:space:]])find\b'; then
    token="$(printf '%s\n' "$cmd" | sed -nE 's/.*-name[[:space:]]+"([^"]{3,})".*/\1/p' | head -n 1)"
    [ -z "$token" ] && token="$(printf '%s\n' "$cmd" | sed -nE "s/.*-name[[:space:]]+'([^']{3,})'.*/\1/p" | head -n 1)"
  fi
  token="$(printf '%s' "$token" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9_]+/ /g' | awk '{print $1}')"
  printf '%s' "$token"
}

git_tree_fingerprint() {
  if command -v sha256sum >/dev/null 2>&1; then
    git -C "$REPO_ROOT" status --porcelain --untracked-files=no 2>/dev/null | sha256sum | awk '{print $1}'
  else
    git -C "$REPO_ROOT" status --porcelain --untracked-files=no 2>/dev/null | shasum -a 256 | awk '{print $1}'
  fi
}

if [ "$pending_keyword_map" = "true" ] && \
   printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && \
   ! printf '%s' "$COMMAND" | grep -qE 'ck\s+get-keyword-map\b'; then
  [ -z "$pending_query" ] && pending_query="<same query>"
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — previous ck find-files was broad/ambiguous.

Run keyword mapping before more expand-folder calls:

  ck get-keyword-map --query \"$pending_query\"

Then treat keyword-map/session-keyword-atlas as source-of-truth for this direction. Pick 3-7 precision terms (provider/domain + workflow + symbol/DTO/type), then rerun ck find-files with refined terms."
  exit 0
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+find-files\b' && [ "$COMMAND" = "$last_find_scope" ]; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — repeated identical ck find-files command.

Do not rerun the same scope command unchanged. If previous output was broad:
  ck get-keyword-map --query \"<same query>\"
Then rerun find-files with refined terms."
  exit 0
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && [ "$COMMAND" = "$last_expand_folder" ]; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — repeated identical ck expand-folder command.

Refine --pattern using add-keyword-hints instead of rerunning the same command." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && \
   [ -n "$no_match_folder" ] && [ "$no_match_count" -ge 2 ] && \
   printf '%s' "$COMMAND" | grep -Fq "$no_match_folder"; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — this folder already had 2 consecutive expand-folder no-match results.

Stop expanding the same folder. Either:
  1) run ck get-keyword-map + refined ck find-files, or
  2) switch to another scoped folder." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && \
   [ -n "$known_target_file" ]; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — expand-folder is for uncharted map-building, not after a concrete file target is known in this direction.

Known target from $known_target_from:
  $known_target_file

Next step in this direction:
  ck signatures \"$known_target_file\"
  ck get-method-source \"$known_target_file\" <MemberName>

If your direction changed, reset scope explicitly with:
  ck find-files --query \"<new direction query>\"" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && \
   [ "$expand_folder_count" -ge 3 ] && \
   [ -z "$known_target_file" ]; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — expand-folder map-building budget reached (3 calls for this direction).

Use targeted reads now:
  ck signatures <file.cs>
  ck get-method-source <file.cs> <MemberName>

If still uncharted, reset direction first:
  ck get-keyword-map --query \"<same query>\"
  ck find-files --query \"<refined query>\"" \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi


# ── Pattern 0: filtering saved Claude tool-result files ─────────────────────
# This rehydrates prior large outputs and encourages grep-churn instead of using
# the structured CK result already in context.
if printf '%s' "$COMMAND" | grep -qE '\.claude/projects/.*/tool-results/' && \
   printf '%s' "$COMMAND" | grep -qE '\|\s*(grep|rg|awk|sed|head|tail|less|more)\b'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — avoid grepping saved Claude tool-result files.

Filtering tool-result files rehydrates previous large outputs and wastes context.
Use the CK command with a narrower pattern instead:

  ck expand-folder --pattern \"<keyword>\" <folder>
  ck get-method-source <file> <MemberName>"
  exit 0
fi

# ── Pattern 1: ck find-files piped through content-filtering tools ───────────
# Block content-filtering pipes on find-files (destroys folder scores and grouping).
# Allow: head, wc — output truncation/counting, harmless.
# Block: grep, tail, sort, awk, sed, cut — filter or reorder scored results.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+find-files\b' && \
   printf '%s' "$COMMAND" | grep -qE '\|\s*(tail|grep|sort|awk|sed|cut|less|more)\b'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — avoid piping ck find-files through grep/sort/awk.

ck find-files output is already ranked by relevance score. Filtering or sorting
destroys that structure. Instead:

  • Reduce output with --top <n> or --min-score <f>

Remove the pipe and re-run the ck command directly."
  exit 0
fi

# Block content-filtering pipes on expand-folder. If expansion output needs
# filtering, the pattern was too broad and should be refined at the source.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b' && \
   printf '%s' "$COMMAND" | grep -qE '\|\s*(head|tail|grep|rg|sort|awk|sed|cut|less|more|wc)\b'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — avoid piping ck expand-folder output.

ck expand-folder now refuses broad output and provides keyword hints. Filtering
or truncating the output hides that guidance and wastes context. Rerun directly
with a more precise pattern:

  ck expand-folder --pattern \"<provider>|<workflow>|<symbol>\" <folder>"
  exit 0
fi

# ── Pattern 2: dotnet build output piped through tail/grep filters ───────────
# These pipelines still stream substantial output into context and invite retries.
# Prefer compact build-check summaries.
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])dotnet[[:space:]]+build\b' && \
   printf '%s' "$COMMAND" | grep -qE '\|\s*(tail|grep|sed|awk|head)\b'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — dotnet build output is being post-filtered.

Use compact build diagnostics directly:

  ck build-check <project.csproj>

This runs dotnet build -v q and emits concise summaries."
  exit 0
fi

# Prefer ck build-check for normal verification loops to avoid duplicate
# dotnet-build + build-check churn. Raw dotnet build is still available as an
# explicit fallback by prefixing the command with CK_ALLOW_RAW_BUILD=1.
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])dotnet[[:space:]]+build\b' && \
   ! printf '%s' "$COMMAND" | grep -qE 'CK_ALLOW_RAW_BUILD=1'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — prefer ck build-check as default verification.

Raw dotnet build often creates duplicate verification loops. Prefer:

  ck build-check <project.csproj>

If you explicitly need full MSBuild output (fallback only), rerun once with:

  CK_ALLOW_RAW_BUILD=1 dotnet build <project.csproj> -v q"
  exit 0
fi

# ── Pattern 2.2: repeated grep/find on same token family (loop collapse) ────
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])(grep|rg|find)\b'; then
  token_family="$(extract_search_token_family "$COMMAND")"
  now_epoch="$(date +%s)"
  if [ -n "$token_family" ] &&
     [ "$token_family" = "$recent_search_token" ] &&
     [ "${recent_search_count:-0}" -ge 4 ] &&
     [ $((now_epoch - ${recent_search_first_ts:-0})) -le 90 ]; then
    jq -n \
      --arg token "$token_family" \
      --arg reason "[ck-guard] BLOCKED — repeated grep/find loop on token family '$token'.

Switch to targeted symbol search instead:

  ck find-symbol \"$token\"
  ck refs \"$token\"

This avoids repeated broad text search churn." \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": $reason
        }
      }'
    exit 0
  fi
fi

# ── Pattern 2.4: source search requires established boundaries ──────────────
# Exception: grep/rg on a specific .cs/.ts/.tsx file (known file, not navigation)
# is allowed without scope. Block only when the target is a directory or uses
# --include=*.cs / --include=*.ts (broad search indicators).
if [ "${#scoped_folders[@]}" -eq 0 ] && \
   printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])(grep|rg|find)\b' && \
   printf '%s' "$COMMAND" | grep -qE '(^|[[:space:]])(src|\.\/src|src/Modules|src/Hosts)(/)?([[:space:]]|$)|\.(cs|ts|tsx)\b|--include=.*\.(cs|ts|tsx)'; then
  _p24_has_non_file=false
  printf '%s' "$COMMAND" | grep -qE '--include=.*\.(cs|ts|tsx)' && _p24_has_non_file=true
  if [ "$_p24_has_non_file" = false ]; then
    while IFS= read -r _p24_path; do
      [ -z "$_p24_path" ] && continue
      if ! [[ "$_p24_path" =~ \.(cs|ts|tsx)$ ]]; then
        _p24_has_non_file=true
        break
      fi
    done < <(extract_command_paths "$COMMAND")
  fi
  if [ "$_p24_has_non_file" = true ]; then
    emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — source search works better with file-first boundaries.

Before grep/glob/find-style searching, run:
  ck get-keyword-map --query \"<domain concept operation>\"
  ck find-files --query \"<domain concept operation>\" --path src/

If results are weak/noisy, fallback to:
  ck get-keyword-map --query \"<domain concept operation>\"
  ck find-files --query \"<refined query from keyword-map>\"

Then keep searches inside returned boundaries."
    exit 0
  fi
fi

# ── Pattern 2.5: strict boundary lock when boundaries exist ───────────────────
if [ "${#scoped_folders[@]}" -gt 0 ]; then
  # Enforce scoped paths for ck signatures folder/file targets.
  if printf '%s' "$COMMAND" | grep -qE 'ck\s+signatures\b'; then
    while IFS= read -r path; do
      [ -z "$path" ] && continue
      if ! is_within_scoped_folders "$path"; then
        jq -n \
          --arg reason "[ck-guard] BLOCKED — ck signatures path is outside current CK boundaries.

Active boundaries were set by latest ck find-files. Keep signatures inside:
  ${scoped_folders[*]}

If direction changed, run:
  ck get-keyword-map --query \"<new direction>\"
  ck find-files --query \"<new direction>\"" \
          '{
            "hookSpecificOutput": {
              "hookEventName": "PreToolUse",
              "permissionDecision": "deny",
              "permissionDecisionReason": $reason
            }
          }'
        exit 0
      fi
    done < <(extract_command_paths "$COMMAND")
  fi

  # Enforce scoped paths for grep/rg/find over source trees.
  if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])(grep|rg|find)\b'; then
    while IFS= read -r path; do
      [ -z "$path" ] && continue
      if ! is_within_scoped_folders "$path"; then
        jq -n \
          --arg reason "[ck-guard] BLOCKED — source search path is outside current CK boundaries.

Keep grep/rg/find inside boundaries from latest ck find-files:
  ${scoped_folders[*]}

If this is a new direction, refresh scope first:
  ck get-keyword-map --query \"<new direction>\"
  ck find-files --query \"<new direction>\"" \
          '{
            "hookSpecificOutput": {
              "hookEventName": "PreToolUse",
              "permissionDecision": "deny",
              "permissionDecisionReason": $reason
            }
          }'
        exit 0
      fi
    done < <(extract_command_paths "$COMMAND")
  fi
fi

# ── Pattern 2.6: repeated identical build-check without tree change ──────────
if printf '%s' "$COMMAND" | grep -qE 'ck\s+build-check\b'; then
  now_epoch="$(date +%s)"
  current_tree="$(git_tree_fingerprint)"
  if [ -n "$last_build_check_command" ] &&
     [ "$COMMAND" = "$last_build_check_command" ] &&
     [ "${last_build_check_tree:-}" = "${current_tree:-}" ] &&
     [ $((now_epoch - ${last_build_check_ts:-0})) -le 45 ]; then
    jq -n \
      --arg reason "[ck-guard] BLOCKED — repeated identical ck build-check with no workspace change.

Prefer delta verification:

  ck build-check --delta <project.csproj>

or continue coding before rerunning build-check." \
      '{
        "hookSpecificOutput": {
          "hookEventName": "PreToolUse",
          "permissionDecision": "deny",
          "permissionDecisionReason": $reason
        }
      }'
    exit 0
  fi
fi

# ── Pattern 3a: broad recursive grep/rg from source/module roots ─────────────
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])(grep|rg)\b' && \
   printf '%s' "$COMMAND" | grep -qE '(^|[[:space:]])-[A-Za-z]*r[A-Za-z]*\b|\b-rn\b|\b--recursive\b|\brg\b' && \
   printf '%s' "$COMMAND" | grep -qE '(^|[[:space:]])(src|\./src|src/Modules|src/Modules/[^/[:space:]]+|src/Hosts|src/Hosts/[^/[:space:]]+)(/)?([[:space:]]|$)' && \
   printf '%s' "$COMMAND" | grep -qE '(--include=.*\.(cs|ts|tsx)|\.(cs|ts|tsx)\b|grep|rg)'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — broad recursive grep over source/module root may be noisy.

Recursive grep from src/ or a module root scans too much. Use CK to narrow first:

  ck find-files --query \"<domain concept operation>\" --path src/

Fallback if needed:
  ck find-files --query \"<domain concept operation>\" --explain

If you already have focused folders, grep those exact folders."
  exit 0
fi

# ── Pattern 3b: broad source-tree find used as manual navigation ─────────────
# Plain find across src/ or a large module dumps unranked paths and bypasses the
# semantic scope step. Allow narrow finds inside already-specific folders.
if printf '%s' "$COMMAND" | grep -qE '\bfind\s+([^|;]*\s)?(src|\./src|src/Modules|src/Hosts)(\s|/)' && \
   printf '%s' "$COMMAND" | grep -qE '(-name\s+|--name\s+|\-type\s+[fd])'; then
  emit_guard_json "allow" "[ck-guard] ALLOW (guidance) — broad find over source folders may flood context.

Plain find across src/ returns unranked paths and often floods context. Use:

  ck find-files --query \"<domain concept operation>\" --path src/

Fallback if needed:
  ck find-files --query \"<domain concept operation>\"

If you already know the exact narrow folder, run find inside that folder only."
  exit 0
fi

# ── Pattern 4: find -exec cat / xargs cat (bulk file read) ────────────────────
# find … -exec cat or find … | xargs cat reads source files in bulk.
# Use ck signatures to list members or get-method-source to read specific ones.
if printf '%s' "$COMMAND" | grep -qE '\bfind\b' && \
   printf '%s' "$COMMAND" | grep -qE '(-exec\s+cat\b|\|\s*xargs\s+cat\b)'; then
  jq -n \
    --arg reason "[ck-guard] BLOCKED — use ck tools instead of find -exec cat.

Bulk-reading source files via find bypasses targeted reads. Use:

  ck signatures <folder>/              # list all members in a folder
  ck get-method-source <file> <Name>   # read one method

These return structured output with exact line spans." \
    '{
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": $reason
      }
    }'
  exit 0
fi
