#!/usr/bin/env bash
# ck-postsession: Stop hook — knowledge capture gating.
#
# Fires after every supported CLI turn (Claude/Codex Stop hook). Guards against over-firing by scoring
# the new portion of the session transcript for codebase exploration signals.
#
# Signal thresholds (evaluated against tool_use entries only, not raw JSONL):
#   Strong (any one → fire):   ck find-files · ck find-files · ck signatures · ck get-method-source
#   Moderate (need ≥2 → fire): source Read call (.cs/.ts/.tsx) · Edit/Write · ck recall
#   Large-session fallback:     many source reads and/or many edits in this turn window
#
# Per-session state: .ck-knowledge/.postsession-offset
#   Line 1: transcript_path  (detects new session → resets offset)
#   Line 2: line count at last run  (skips already-evaluated lines)

if ! command -v jq >/dev/null 2>&1; then
  exit 0
fi

INPUT=$(cat)

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null)
[ -z "$REPO_ROOT" ] && exit 0

# Find CK binary: global PATH → global install dir → per-repo deploy paths.
CK=""
if command -v ck >/dev/null 2>&1; then
  CK=$(command -v ck)
elif [ -x "$HOME/.ck/bin/ck" ]; then
  CK="$HOME/.ck/bin/ck"
elif [ -x "${CODEX_HOME:-$HOME/.codex}/skills/ck/ck" ]; then
  CK="${CODEX_HOME:-$HOME/.codex}/skills/ck/ck"
elif [ -x "$REPO_ROOT/ck" ]; then
  CK="$REPO_ROOT/ck"
fi
[ -n "$CK" ] || exit 0

# For global install, only fire in repos initialized with `ck init` (have .ck-knowledge/).
if [ ! -x "$REPO_ROOT/ck" ]; then
  [ -d "$REPO_ROOT/.ck-knowledge" ] || exit 0
fi

# Respect brain opt-out: if .ck.json exists and sets "brain": false, skip knowledge capture.
if [ -f "$REPO_ROOT/.ck.json" ]; then
  BRAIN_DISABLED=$(jq -r 'if .brain == false then "1" else "0" end' "$REPO_ROOT/.ck.json" 2>/dev/null || echo "0")
  [ "$BRAIN_DISABLED" = "1" ] && exit 0
fi

TRANSCRIPT=$(printf '%s' "$INPUT" | jq -r '.transcript_path // empty' 2>/dev/null)
[ -z "$TRANSCRIPT" ] || [ ! -f "$TRANSCRIPT" ] && exit 0

KNOWLEDGE_DIR="$REPO_ROOT/.ck-knowledge"
mkdir -p "$KNOWLEDGE_DIR"
OFFSET_FILE="$KNOWLEDGE_DIR/.postsession-offset"

STORED_PATH=""
OFFSET=0
if [ -f "$OFFSET_FILE" ]; then
  STORED_PATH=$(sed -n '1p' "$OFFSET_FILE")
  RAW=$(sed -n '2p' "$OFFSET_FILE")
  OFFSET=${RAW:-0}
fi

# New session detected (transcript path changed) — start from the beginning.
[ "$STORED_PATH" != "$TRANSCRIPT" ] && OFFSET=0

TOTAL=$(awk 'END{print NR}' "$TRANSCRIPT")

# Persist updated offset now so the knowledge-capture turn's own content
# is not re-evaluated on the subsequent turn.
printf '%s\n%s\n' "$TRANSCRIPT" "$TOTAL" > "$OFFSET_FILE"

# Avoid infinite re-entry: hooks set stop_hook_active=true on the
# follow-up turn triggered by this hook's own additionalContext output.
STOP_HOOK_ACTIVE=$(printf '%s' "$INPUT" | jq -r '.stop_hook_active // false' 2>/dev/null)
[ "$STOP_HOOK_ACTIVE" = "true" ] && exit 0

# Nothing new since last run.
[ "$TOTAL" -le "$OFFSET" ] && exit 0

# ── Signal scoring ──────────────────────────────────────────────────────────────
# Parse tool_use entries only — avoids skill_listing attachment false positives.
STRONG=0
MODERATE=0
SOURCE_READ_COUNT=0
EDIT_COUNT=0

# Single-pass extraction:
# - Any tool_use entry with input.command is treated as a shell-like command.
# - tool_use entries with input.file_path are treated as file-read calls.
# - All other tool names are tracked for edit signals (Edit/Write/apply_patch).
SIGNALS=$(tail -n +"$((OFFSET + 1))" "$TRANSCRIPT" | \
  jq -r '
    try (
      .message.content[]? |
      if .type == "tool_use" then
        if   ((.input.command // "") | tostring | length) > 0 then "BASH:" + (.input.command   // "")
        elif ((.input.file_path // "") | tostring | length) > 0 then "READ:" + (.input.file_path // "")
        else                      "TOOL:" + (.name            // "")
        end
      else empty
      end
    ) catch empty
  ' 2>/dev/null || true)

BASH_CMDS=$(printf '%s\n' "$SIGNALS" | sed -n 's/^BASH://p')
READ_PATHS=$(printf '%s\n' "$SIGNALS" | sed -n 's/^READ://p')
TOOL_NAMES=$(printf '%s\n' "$SIGNALS" | sed -n 's/^TOOL://p')

# Strong signals: CK code-search tool usage (Bash commands only)
printf '%s' "$BASH_CMDS" | grep -qF 'find-files'        && STRONG=1
printf '%s' "$BASH_CMDS" | grep -qF 'find-files'        && STRONG=1
printf '%s' "$BASH_CMDS" | grep -qF 'ck signatures'     && STRONG=1
printf '%s' "$BASH_CMDS" | grep -qF 'get-method-source' && STRONG=1

# Moderate signals
SOURCE_READ_COUNT=$(printf '%s\n' "$READ_PATHS" | grep -E '\.(cs|ts|tsx)$|(\.(cs|ts|tsx)[^a-zA-Z])' | wc -l | tr -d ' ')
EDIT_COUNT=$(printf '%s\n' "$TOOL_NAMES" | grep -E '^(Edit|Write|apply_patch)$' | wc -l | tr -d ' ')

[ "$SOURCE_READ_COUNT" -gt 0 ] && MODERATE=$((MODERATE + 1))
[ "$EDIT_COUNT" -gt 0 ]        && MODERATE=$((MODERATE + 1))
printf '%s' "$BASH_CMDS"  | grep -qF  'ck recall'           && MODERATE=$((MODERATE + 1))

LARGE_SESSION=0
if [ "$SOURCE_READ_COUNT" -ge 3 ] || [ "$EDIT_COUNT" -ge 3 ] || \
   { [ "$SOURCE_READ_COUNT" -ge 2 ] && [ "$EDIT_COUNT" -ge 1 ]; }; then
  LARGE_SESSION=1
fi

[ "$STRONG" -eq 0 ] && [ "$MODERATE" -lt 2 ] && [ "$LARGE_SESSION" -eq 0 ] && exit 0

# ── Inject knowledge-capture prompt ───────────────────────────────────────────
CONTEXT="## Knowledge capture (ck-postsession)

Before this session ends, record institutional knowledge and review existing snippets.

**1. Record new discoveries**

\`\`\`bash
$CK learn \\
  --content \"<2-4 sentences>\" \\
  --folders \"<comma-separated folder paths>\" \\
  --tags \"<comma-separated keywords>\"
\`\`\`

**What to record:** architectural patterns not obvious from folder structure, the WHY behind
decisions, gotchas and constraints, cross-module relationships that span folders.

**Before writing the content, strip:**
- File paths — those go in \`--folders\`, not in the text
- Function/method names discovered via \`ck signatures\` — an agent can find those in 1 call
- Internal helper names, parameter types, specific flag names — implementation detail
- Anything a future agent would see immediately by reading the relevant file

**Length check:** if your draft is more than 4 sentences, you are including implementation
detail. Cut until only the non-obvious insight remains.

**Good:** \"Commander backend rendering: C# renders placeholder divs; AMD page scripts push
React components into a Zustand store; BackendComponents portals each into its placeholder.
A feature flag routes DOM/JSX refs to module-level Maps to prevent detached DOM retention
in memoized props after navigation — without it, navigating away leaks memory.\"

**Bad:** the same text with file paths in the prose and internal helper names like
\`getInitChainWithCheck\` or \`getMountElement\` — those are findable via signatures.

**2. Review existing snippets for staleness**

\`\`\`bash
$CK recall --folder <path-you-worked-in>
\`\`\`

If a snippet describes something refactored, renamed, or no longer true — remove it:

\`\`\`bash
$CK forget --id <snippet-id>
\`\`\`

If you worked in no source folders or found nothing worth recording, do nothing."

jq -n --arg ctx "$CONTEXT" '{
  "additionalContext": $ctx
}'
