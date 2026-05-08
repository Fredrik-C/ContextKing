#!/usr/bin/env bash
# ck-scope-hint: PostToolUse hook for the Bash tool.
#
# Responsibilities:
# 1) Existing hint: for tight find-files score clusters, suggest --min-score.
# 2) Stateful loop control support for PreToolUse guard:
#    - track file-first boundaries from find-files
#    - mark broad find-files as requiring get-keyword-map before next scope/explore
#    - track last find-files / expand-folder command (dedupe)
#    - track consecutive expand-folder no-match count per folder

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)

TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)
[ "$TOOL" != "Bash" ] && exit 0

COMMAND=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty' 2>/dev/null)
[ -z "$COMMAND" ] && exit 0

OUTPUT=$(printf '%s' "$INPUT" | jq -r '.tool_response.output // empty' 2>/dev/null)

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
STATE_DIR="$REPO_ROOT/.ck-index"
STATE_FILE="$STATE_DIR/.ck-guard-state.json"
mkdir -p "$STATE_DIR"

if [ ! -f "$STATE_FILE" ]; then
  cat > "$STATE_FILE" <<'JSON'
{"keywordMapSeen":false,"pendingKeywordMap":false,"pendingQuery":"","lastFindFilesCommand":"","lastExpandFolderCommand":"","noMatchFolder":"","noMatchCount":0,"knownTargetFile":"","knownTargetFolder":"","knownTargetFrom":"","expandFolderCount":0,"signaturesFolderCount":0,"scopedFolders":[],"recentSearchToken":"","recentSearchCount":0,"recentSearchFirstTs":0,"lastBuildCheckCommand":"","lastBuildCheckTs":0,"lastBuildCheckTree":""}
JSON
fi

update_state() {
  local tmp="$STATE_FILE.tmp"
  jq "$@" "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
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

extract_query_from_find_scope() {
  local cmd="$1"
  printf '%s\n' "$cmd" | sed -n 's/.*--query[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

extract_scoped_folders_from_find_files_json() {
  local output="$1"
  printf '%s\n' "$output" \
    | awk -F'\t' '/^[0-9]+\.[0-9]+\t/ {print $2}' \
    | grep -E '\.(cs|ts|tsx)$' \
    | sed 's|\\|/|g' \
    | sed 's|^\./||' \
    | xargs -I{} dirname "{}" 2>/dev/null \
    | awk 'length($0)>0' \
    | jq -R . \
    | jq -s 'unique'
}

extract_file_arg_for_tool() {
  local cmd="$1"
  local tool="$2"
  printf '%s\n' "$cmd" | sed -nE "s/.*ck[[:space:]]+$tool\\b[[:space:]]+\"?([^\"[:space:]]+\\.(cs|ts|tsx))\"?.*/\\1/p" | head -n 1
}

extract_scoped_folders_json() {
  local output="$1"
  printf '%s\n' "$output" \
    | awk -F'\t' '/^[0-9]+\.[0-9]+\t/ {print $2}' \
    | sed 's|\\|/|g' \
    | sed 's|^\./||' \
    | sed 's|/*$||' \
    | awk 'length($0)>0' \
    | jq -R . \
    | jq -s 'unique'
}

# Clear pending keyword-map requirement once get-keyword-map succeeds.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+get-keyword-map\b'; then
  update_state '.keywordMapSeen=true | .pendingKeywordMap=false | .pendingQuery="" | .noMatchFolder="" | .noMatchCount=0 | .knownTargetFile="" | .knownTargetFolder="" | .knownTargetFrom="" | .expandFolderCount=0 | .signaturesFolderCount=0 | .scopedFolders=[] | .recentSearchToken="" | .recentSearchCount=0 | .recentSearchFirstTs=0'
  exit 0
fi

# Track find-files outcomes.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+find-files\b'; then
  query="$(extract_query_from_find_scope "$COMMAND")"
  [ -z "$query" ] && query="<same query>"
  folders_json="$(extract_scoped_folders_json "$OUTPUT")"
  [ -z "$folders_json" ] && folders_json='[]'

  if printf '%s' "$OUTPUT" | grep -qF '[ck find-files] Scope is too broad or ambiguous.'; then
    update_state --arg cmd "$COMMAND" --arg q "$query" '
      .lastFindFilesCommand=$cmd
      | .pendingKeywordMap=true
      | .pendingQuery=$q
      | .noMatchFolder=""
      | .noMatchCount=0
      | .knownTargetFile=""
      | .knownTargetFolder=""
      | .knownTargetFrom=""
      | .expandFolderCount=0
      | .signaturesFolderCount=0
      | .scopedFolders=[]
    '
  else
    update_state --arg cmd "$COMMAND" --argjson folders "$folders_json" '
      .lastFindFilesCommand=$cmd
      | .pendingKeywordMap=false
      | .pendingQuery=""
      | .noMatchFolder=""
      | .noMatchCount=0
      | .knownTargetFile=""
      | .knownTargetFolder=""
      | .knownTargetFrom=""
      | .expandFolderCount=0
      | .signaturesFolderCount=0
      | .scopedFolders=$folders
    '
  fi
fi

# Track find-files outcomes (file-first boundaries).
if printf '%s' "$COMMAND" | grep -qE 'ck\s+find-files\b'; then
  folders_json="$(extract_scoped_folders_from_find_files_json "$OUTPUT")"
  [ -z "$folders_json" ] && folders_json='[]'

  if printf '%s' "$OUTPUT" | grep -qF '[ck find-files] No matches found.'; then
    update_state --arg cmd "$COMMAND" '
      .lastFindFilesCommand=$cmd
      | .scopedFolders=[]
      | .noMatchFolder=""
      | .noMatchCount=0
      | .knownTargetFile=""
      | .knownTargetFolder=""
      | .knownTargetFrom=""
    '
  else
    update_state --arg cmd "$COMMAND" --argjson folders "$folders_json" '
      .lastFindFilesCommand=$cmd
      | .pendingKeywordMap=false
      | .pendingQuery=""
      | .keywordMapSeen=true
      | .scopedFolders=$folders
      | .noMatchFolder=""
      | .noMatchCount=0
      | .knownTargetFile=""
      | .knownTargetFolder=""
      | .knownTargetFrom=""
      | .expandFolderCount=0
      | .signaturesFolderCount=0
    '
  fi
fi

# Track expand-folder outcomes.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+expand-folder\b'; then
  update_state --arg cmd "$COMMAND" '.lastExpandFolderCommand=$cmd'
  update_state '.expandFolderCount=((.expandFolderCount // 0)+1)'

  no_match_line="$(printf '%s' "$OUTPUT" | grep -F '[ck expand-folder] No signatures matched pattern' | head -n 1)"
  if [ -n "$no_match_line" ]; then
    folder="$(printf '%s\n' "$no_match_line" | sed -n "s/.* in '\([^']*\)'.*/\1/p" | head -n 1)"
    if [ -n "$folder" ]; then
      update_state --arg folder "$folder" '
        if .noMatchFolder == $folder
        then .noMatchCount = ((.noMatchCount // 0) + 1)
        else .noMatchFolder = $folder | .noMatchCount = 1
        end
      '
    fi
  elif printf '%s' "$OUTPUT" | grep -qE '^[^[]+\.((cs)|(ts)|(tsx))$'; then
    update_state '.noMatchFolder="" | .noMatchCount=0'
  fi
fi

# Track when navigation has reached a concrete file target.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+get-method-source\b'; then
  file="$(extract_file_arg_for_tool "$COMMAND" "get-method-source")"
  if [ -n "$file" ] && ! printf '%s' "$OUTPUT" | grep -qE '\b(ERROR|Error)\b'; then
    folder="$(dirname "$file")"
    update_state --arg file "$file" --arg folder "$folder" '
      .knownTargetFile=$file
      | .knownTargetFolder=$folder
      | .knownTargetFrom="get-method-source"
    '
  fi
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+(get-constructors|get-usings|get-base-types)\b'; then
  file="$(extract_file_arg_for_tool "$COMMAND" "get-constructors")"
  [ -z "$file" ] && file="$(extract_file_arg_for_tool "$COMMAND" "get-usings")"
  [ -z "$file" ] && file="$(extract_file_arg_for_tool "$COMMAND" "get-base-types")"
  if [ -n "$file" ] && ! printf '%s' "$OUTPUT" | grep -qE '\b(ERROR|Error)\b'; then
    folder="$(dirname "$file")"
    update_state --arg file "$file" --arg folder "$folder" --arg from "file-ast-read" '
      .knownTargetFile=$file
      | .knownTargetFolder=$folder
      | .knownTargetFrom=$from
    '
  fi
fi

if printf '%s' "$COMMAND" | grep -qE 'ck\s+signatures\b'; then
  file="$(extract_file_arg_for_tool "$COMMAND" "signatures")"
  if [ -n "$file" ] && ! printf '%s' "$OUTPUT" | grep -qE '\b(ERROR|Error)\b'; then
    folder="$(dirname "$file")"
    update_state --arg file "$file" --arg folder "$folder" --arg from "signatures-file" '
      .knownTargetFile=$file
      | .knownTargetFolder=$folder
      | .knownTargetFrom=$from
    '
  elif [ -z "$file" ] && ! printf '%s' "$OUTPUT" | grep -qE '\b(ERROR|Error)\b'; then
    update_state '.signaturesFolderCount=((.signaturesFolderCount // 0)+1)'
  fi
fi

# Track repetitive grep/find token families.
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|[:space:]])(grep|rg|find)\b'; then
  now_epoch="$(date +%s)"
  token_family="$(extract_search_token_family "$COMMAND")"
  if [ -n "$token_family" ]; then
    update_state --arg token "$token_family" --argjson now "$now_epoch" '
      if .recentSearchToken == $token and ((($now - (.recentSearchFirstTs // 0)) <= 90))
      then .recentSearchCount=((.recentSearchCount // 0)+1)
      else .recentSearchToken=$token | .recentSearchCount=1 | .recentSearchFirstTs=$now
      end
    '
  fi
fi

# Track last build-check invocation for dedupe guards.
if printf '%s' "$COMMAND" | grep -qE 'ck\s+build-check\b'; then
  now_epoch="$(date +%s)"
  tree_fp="$(git_tree_fingerprint)"
  update_state --arg cmd "$COMMAND" --argjson now "$now_epoch" --arg tree "$tree_fp" '
    .lastBuildCheckCommand=$cmd
    | .lastBuildCheckTs=$now
    | .lastBuildCheckTree=$tree
  '
fi

# Existing score-cluster hint (find-files only).
if ! printf '%s' "$COMMAND" | grep -qE 'ck\s+find-files\b'; then
  exit 0
fi

[ -z "$OUTPUT" ] && exit 0

STATS=$(printf '%s' "$OUTPUT" | awk -F'\t' '
  /^[0-9]+\.[0-9]+\t/ {
    s = $1 + 0
    if (count == 0 || s > max) max = s
    if (count == 0 || s < min) min = s
    count++
  }
  END { if (count > 0) printf "%d %.4f %.4f", count, min, max }
')

[ -z "$STATS" ] && exit 0

COUNT=$(printf '%s' "$STATS" | awk '{print $1}')
MIN=$(printf '%s'   "$STATS" | awk '{print $2}')
MAX=$(printf '%s'   "$STATS" | awk '{print $3}')

HINT=$(printf '%s %s %s' "$COUNT" "$MIN" "$MAX" | awk '{
  count = $1; min = $2; max = $3
  spread = max - min
  avg_gap = (count > 1) ? spread / (count - 1) : spread
  if (count >= 5 && avg_gap <= 0.01 && min > 0.70) {
    suggested = min - avg_gap
    printf "[ck-hint] Scores are tightly clustered (%.2f\xe2\x80\x93%.2f across %d folders). The cutoff is likely mid-cluster \xe2\x80\x94 relevant folders may be missing. Re-run with --min-score %.2f to capture the full cluster.", min, max, count, suggested
  }
}')

[ -z "$HINT" ] && exit 0

jq -n --arg hint "$HINT" '{
  "hookSpecificOutput": {
    "hookEventName": "PostToolUse",
    "additionalContext": $hint
  }
}'
