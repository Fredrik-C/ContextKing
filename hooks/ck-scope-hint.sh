#!/usr/bin/env bash
# ck-scope-hint: PostToolUse hook for the Bash tool.
#
# Fires after ck find-scope completes. Inspects the score column
# in the output and appends an additionalContext hint when the average gap
# between adjacent scores is <= 0.01 and scores are above the noise floor
# (0.70) — a signal that --top N sliced through a relevant cluster, silently
# dropping folders that should be in scope.
#
# Using avg_gap = spread / (count - 1) rather than a fixed spread threshold
# makes the check scale correctly with --top N: a --top 30 run with spread
# 0.29 is just as suspicious as a --top 5 run with spread 0.04.
#
# The hint suggests a concrete --min-score value so the agent can re-run and
# capture the full cluster rather than an arbitrary count.

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)

# Only fire on the Bash tool
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)
[ "$TOOL" != "Bash" ] && exit 0

# Only fire when the command ran ck find-scope or ck search
COMMAND=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty' 2>/dev/null)
printf '%s' "$COMMAND" | grep -qE 'ck\s+find-scope\b' || exit 0

# Extract stdout
OUTPUT=$(printf '%s' "$INPUT" | jq -r '.tool_response.output // empty' 2>/dev/null)
[ -z "$OUTPUT" ] && exit 0

# Parse score column from lines formatted as "<float>\t<folder-path>".
# Compute count, min, and max in a single awk pass.
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

# Require at least 5 results; check avg_gap = spread/(count-1) <= 0.01 and min > 0.70
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
