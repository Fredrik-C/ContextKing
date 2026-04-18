#!/usr/bin/env bash
# deploy.sh — unified Context King deployer.
#
# Detects which AI CLI tools are configured in the target repo and deploys
# Context King support for each one found:
#   .claude/    → Claude Code
#   .codex/     → Codex CLI
#   .opencode/  → OpenCode
#
# Usage:
#   bash scripts/deploy.sh <target-repo-root>
#   bash scripts/deploy.sh <target-repo-root> --all    # deploy regardless of detection
#
# Individual deployers (advanced / standalone use):
#   bash scripts/deploy-codex.sh [--codex-home <path>] [--target-repo <path>]
#   bash scripts/deploy-opencode.sh <target-repo-root>

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT_DIR="$REPO_DIR/scripts"

TARGET=""
DEPLOY_ALL=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --all)   DEPLOY_ALL=true; shift ;;
    -h|--help)
      echo "Usage: deploy.sh <target-repo-root> [--all]"
      exit 0
      ;;
    -*) echo "Unknown flag: $1" >&2; exit 1 ;;
    *)  TARGET="$1"; shift ;;
  esac
done

if [ -z "$TARGET" ]; then
  echo "Usage: deploy.sh <target-repo-root> [--all]" >&2
  exit 1
fi

normalize_path() {
  local input="$1"
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -u "$input" 2>/dev/null || printf '%s' "$input"
  elif [[ "$input" =~ ^([A-Za-z]):[\\/]?(.*)$ ]]; then
    local drive="${BASH_REMATCH[1],,}"
    local rest="${BASH_REMATCH[2]}"
    rest="${rest//\\//}"
    printf '/mnt/%s/%s' "$drive" "$rest"
  else
    printf '%s' "$input"
  fi
}

TARGET="$(normalize_path "$TARGET")"

if [ ! -d "$TARGET" ]; then
  echo "Error: target directory does not exist: $TARGET" >&2
  exit 1
fi

# ── Detect configured CLI tools ────────────────────────────────────────────────
HAS_CLAUDE=false
HAS_CODEX=false
HAS_OPENCODE=false
HAS_AGENTS=false

if [ "$DEPLOY_ALL" = true ]; then
  HAS_CLAUDE=true
  HAS_CODEX=true
  HAS_OPENCODE=true
  HAS_AGENTS=true
else
  [ -d "$TARGET/.claude" ]   && HAS_CLAUDE=true
  [ -d "$TARGET/.codex" ]    && HAS_CODEX=true
  [ -d "$TARGET/.opencode" ] && HAS_OPENCODE=true
  [ -d "$TARGET/.agents" ]   && HAS_AGENTS=true
fi

if [ "$HAS_CLAUDE" = false ] && [ "$HAS_CODEX" = false ] && [ "$HAS_OPENCODE" = false ] && [ "$HAS_AGENTS" = false ]; then
  echo ""
  echo "No AI CLI configuration found in: $TARGET"
  echo ""
  echo "Context King supports the following CLI tools:"
  echo "  Claude Code  → initialize by running 'claude' in your repo  (.claude/ will be created)"
  echo "  Codex CLI    → initialize by running 'codex init' in your repo  (.codex/ will be created)"
  echo "  OpenCode     → initialize by running 'opencode' in your repo  (.opencode/ will be created)"
  echo "  Agents       → create .agents/ directory manually"
  echo ""
  echo "Create at least one of these directories, then re-run deploy.sh."
  echo "Or use --all to deploy for all supported CLI tools regardless of detection."
  exit 1
fi

# Build a label for the summary
DETECTED_LABELS=""
[ "$HAS_CLAUDE" = true ]   && DETECTED_LABELS="${DETECTED_LABELS}Claude Code, "
[ "$HAS_CODEX" = true ]    && DETECTED_LABELS="${DETECTED_LABELS}Codex CLI, "
[ "$HAS_OPENCODE" = true ] && DETECTED_LABELS="${DETECTED_LABELS}OpenCode, "
[ "$HAS_AGENTS" = true ]   && DETECTED_LABELS="${DETECTED_LABELS}Agents, "
DETECTED_LABELS="${DETECTED_LABELS%, }"

echo "Deploying Context King to: $TARGET"
echo "Detected CLI tools: $DETECTED_LABELS"
echo ""

# ── Deploy: Claude Code (.claude/) ────────────────────────────────────────────
if [ "$HAS_CLAUDE" = true ]; then
  echo "── Claude Code (.claude/) ────────────────────────────────────────────────"
  DOT_CLAUDE="$TARGET/.claude"
  mkdir -p "$DOT_CLAUDE"

  # 1. Copy models
  echo "  Copying models..."
  mkdir -p "$DOT_CLAUDE/models"
  cp -r "$REPO_DIR/models/." "$DOT_CLAUDE/models/"

  # 2. Copy skills
  echo "  Copying skills..."
  mkdir -p "$DOT_CLAUDE/skills"
  cp -r "$REPO_DIR/skills/." "$DOT_CLAUDE/skills/"

  chmod +x \
    "$DOT_CLAUDE/skills/ck/ck" \
    2>/dev/null || true
  # Platform binaries (ck-osx-arm64 etc.) get chmod from install.sh

  # 3. Copy rules
  echo "  Copying rules..."
  mkdir -p "$DOT_CLAUDE/rules"
  cp "$REPO_DIR/rules/ck-code-search-protocol.md" "$DOT_CLAUDE/rules/"

  # 4. Copy hooks (only deny-mode guards — allow-with-warning guards are proven useless)
  echo "  Copying hooks..."
  mkdir -p "$DOT_CLAUDE/hooks"

  # Remove old guards that no longer exist (allow-only guards removed in v1.2.9)
  for old_guard in ck-read-guard.sh ck-read-guard.ps1 ck-search-guard.sh ck-search-guard.ps1; do
    if [ -f "$DOT_CLAUDE/hooks/$old_guard" ]; then
      rm "$DOT_CLAUDE/hooks/$old_guard"
      echo "  Removed obsolete hook: $old_guard"
    fi
  done

  cp "$REPO_DIR/hooks/agent-usage-guard.sh"  "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/agent-usage-guard.ps1" "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/ck-bash-guard.sh"      "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/ck-bash-guard.ps1"     "$DOT_CLAUDE/hooks/"
  chmod +x "$DOT_CLAUDE/hooks/agent-usage-guard.sh" "$DOT_CLAUDE/hooks/ck-bash-guard.sh"

  # 5. Register hooks in settings.json (idempotent)
  SETTINGS="$DOT_CLAUDE/settings.json"
  if [ ! -f "$SETTINGS" ]; then
    echo '{}' > "$SETTINGS"
  fi

  if command -v jq >/dev/null 2>&1; then
    # Remove stale hook registrations for deleted guards (Read, Glob, Grep)
    if jq -e '[.hooks.PreToolUse[]?.hooks[]?.command // empty] | any(test("ck-read-guard|ck-search-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse = [.hooks.PreToolUse[]? | select(.hooks | all(.command | test("ck-read-guard|ck-search-guard") | not))]' \
        "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Removed obsolete hook registrations (Read/Glob/Grep guards) from settings.json"
    fi

    if ! jq -e '.permissions.allowedTools // [] | any(test("ck/ck"))' "$SETTINGS" >/dev/null 2>&1; then
      jq '.permissions.allowedTools = ((.permissions.allowedTools // []) + [
        "Bash(.claude/skills/ck/ck *)",
        "Bash(.claude\\skills\\ck\\ck.cmd *)"
      ])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Added ck allowedTools permissions to settings.json"
    else
      echo "  ck allowedTools permissions already present — skipping."
    fi
    # Migrate: remove old PreToolUse Agent hook if present
    if jq -e '[.hooks.PreToolUse[]? | select(.matcher == "Agent") | .hooks[]?.command // empty] | any(test("agent-usage-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse = [.hooks.PreToolUse[]? | select(.matcher != "Agent" or (.hooks | all(.command | test("agent-usage-guard") | not)))]' \
        "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Migrated: removed old PreToolUse Agent hook"
    fi
    # Register SubagentStart hook (injects CK protocol into sub-agent context)
    if ! jq -e '[.hooks.SubagentStart[]?.hooks[]?.command // empty] | any(test("agent-usage-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.SubagentStart = ((.hooks.SubagentStart // []) + [{"matcher":"*","hooks":[
        {"type":"command","command":".claude/hooks/agent-usage-guard.sh"},
        {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/agent-usage-guard.ps1 || exit 0'\''"}
      ]}])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Registered SubagentStart hook in settings.json"
    else
      echo "  SubagentStart hook already registered — skipping."
    fi
    if ! jq -e '[.hooks.PreToolUse[]?.hooks[]?.command // empty] | any(test("ck-bash-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse += [{"matcher":"Bash","hooks":[
        {"type":"command","command":".claude/hooks/ck-bash-guard.sh"},
        {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-bash-guard.ps1 || exit 0'\''"}
      ]}]' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Registered Bash hook in settings.json"
    else
      echo "  Bash hook already registered — skipping."
    fi
  else
    echo "  WARNING: jq not found. Add hooks to $SETTINGS manually."
    echo "  See hooks/ directory for Agent and Bash guards to register."
  fi

  # 6. Add .ck-index/ to .gitignore
  GITIGNORE="$TARGET/.gitignore"
  if [ -f "$GITIGNORE" ]; then
    if ! grep -qF '.ck-index' "$GITIGNORE"; then
      echo '' >> "$GITIGNORE"
      echo '# Context King index' >> "$GITIGNORE"
      echo '.ck-index/' >> "$GITIGNORE"
      echo "  Added .ck-index/ to .gitignore"
    fi
  else
    printf '# Context King index\n.ck-index/\n' > "$GITIGNORE"
    echo "  Created .gitignore with .ck-index/"
  fi

  echo ""
fi

# ── Deploy: Codex CLI (.codex/) ────────────────────────────────────────────────
if [ "$HAS_CODEX" = true ]; then
  echo "── Codex CLI (.codex/) ───────────────────────────────────────────────────"
  bash "$SCRIPT_DIR/deploy-codex.sh" --target-repo "$TARGET"
  echo ""
fi

# ── Deploy: OpenCode (.opencode/) ─────────────────────────────────────────────
if [ "$HAS_OPENCODE" = true ]; then
  echo "── OpenCode (.opencode/) ─────────────────────────────────────────────────"
  bash "$SCRIPT_DIR/deploy-opencode.sh" "$TARGET"
  echo ""
fi

# ── Deploy: Agents (.agents/) ──────────────────────────────────────────────────
if [ "$HAS_AGENTS" = true ]; then
  echo "── Agents (.agents/) ─────────────────────────────────────────────────────"
  DOT_AGENTS="$TARGET/.agents"

  # 1. Copy models
  echo "  Copying models..."
  mkdir -p "$DOT_AGENTS/models"
  cp -r "$REPO_DIR/models/." "$DOT_AGENTS/models/"

  # 2. Copy skills
  echo "  Copying skills..."
  mkdir -p "$DOT_AGENTS/skills"
  cp -r "$REPO_DIR/skills/." "$DOT_AGENTS/skills/"

  chmod +x "$DOT_AGENTS/skills/ck/ck" 2>/dev/null || true

  # 3. Rewrite .claude/ paths to .agents/ in SKILL.md files
  echo "  Rewriting skill paths for .agents/..."
  while IFS= read -r -d '' skill_file; do
    tmp_file="$(mktemp)"
    sed \
      -e 's|\.claude/skills/ck/|.agents/skills/ck/|g' \
      -e 's|\.claude\\skills\\ck\\|.agents\\skills\\ck\\|g' \
      "$skill_file" > "$tmp_file"
    mv "$tmp_file" "$skill_file"
  done < <(find "$DOT_AGENTS/skills" -type f -name 'SKILL.md' -print0)

  # 4. Copy rules (protocol)
  echo "  Copying rules..."
  mkdir -p "$DOT_AGENTS/rules"
  PROTOCOL_FILE="$DOT_AGENTS/rules/ck-code-search-protocol.md"
  cp "$REPO_DIR/rules/ck-code-search-protocol.md" "$PROTOCOL_FILE"
  # Rewrite binary paths
  sed -i.bak \
    -e 's|\.claude/skills/ck/|.agents/skills/ck/|g' \
    -e 's|\.claude\\skills\\ck\\|.agents\\skills\\ck\\|g' \
    "$PROTOCOL_FILE" && rm -f "$PROTOCOL_FILE.bak"

  # 5. Add .ck-index/ to .gitignore
  GITIGNORE="$TARGET/.gitignore"
  if [ -f "$GITIGNORE" ]; then
    if ! grep -qF '.ck-index' "$GITIGNORE"; then
      echo '' >> "$GITIGNORE"
      echo '# Context King index' >> "$GITIGNORE"
      echo '.ck-index/' >> "$GITIGNORE"
      echo "  Added .ck-index/ to .gitignore"
    fi
  fi

  # 6. Add CK reference to AGENTS.md (idempotent)
  AGENTS_MD="$TARGET/AGENTS.md"
  if [ -f "$AGENTS_MD" ]; then
    if ! grep -qF 'ck-code-search-protocol' "$AGENTS_MD"; then
      cat >> "$AGENTS_MD" <<'AGENTS_EOF'

## Code Navigation

This repository uses Context King (CK) for semantic code search. **You must follow the protocol in `.agents/rules/ck-code-search-protocol.md` before searching or reading source files.** The CK binary is at `.agents/skills/ck/ck`.
AGENTS_EOF
      echo "  Added CK reference to AGENTS.md"
    else
      echo "  AGENTS.md already references CK — skipping."
    fi
  fi

  echo ""
fi

# ── Summary ────────────────────────────────────────────────────────────────────
echo "Done. Context King deployed for: $DETECTED_LABELS"
echo ""
echo "First use: run 'ck find-scope' — the index will be built automatically."
