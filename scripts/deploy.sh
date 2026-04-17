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

if [ "$DEPLOY_ALL" = true ]; then
  HAS_CLAUDE=true
  HAS_CODEX=true
  HAS_OPENCODE=true
else
  [ -d "$TARGET/.claude" ]   && HAS_CLAUDE=true
  [ -d "$TARGET/.codex" ]    && HAS_CODEX=true
  [ -d "$TARGET/.opencode" ] && HAS_OPENCODE=true
fi

if [ "$HAS_CLAUDE" = false ] && [ "$HAS_CODEX" = false ] && [ "$HAS_OPENCODE" = false ]; then
  echo ""
  echo "No AI CLI configuration found in: $TARGET"
  echo ""
  echo "Context King supports the following CLI tools:"
  echo "  Claude Code  → initialize by running 'claude' in your repo  (.claude/ will be created)"
  echo "  Codex CLI    → initialize by running 'codex init' in your repo  (.codex/ will be created)"
  echo "  OpenCode     → initialize by running 'opencode' in your repo  (.opencode/ will be created)"
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

  # 4. Copy hooks
  echo "  Copying hooks..."
  mkdir -p "$DOT_CLAUDE/hooks"
  cp "$REPO_DIR/hooks/ck-read-guard.sh"       "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/ck-read-guard.ps1"     "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/ck-search-guard.sh"    "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/ck-search-guard.ps1"   "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/agent-usage-guard.sh"  "$DOT_CLAUDE/hooks/"
  cp "$REPO_DIR/hooks/agent-usage-guard.ps1" "$DOT_CLAUDE/hooks/"
  chmod +x "$DOT_CLAUDE/hooks/ck-read-guard.sh" "$DOT_CLAUDE/hooks/ck-search-guard.sh" "$DOT_CLAUDE/hooks/agent-usage-guard.sh"

  # 5. Register hooks in settings.json (idempotent)
  SETTINGS="$DOT_CLAUDE/settings.json"
  if [ ! -f "$SETTINGS" ]; then
    echo '{}' > "$SETTINGS"
  fi

  if command -v jq >/dev/null 2>&1; then
    if ! jq -e '.permissions.allowedTools // [] | any(test("ck/ck"))' "$SETTINGS" >/dev/null 2>&1; then
      jq '.permissions.allowedTools = ((.permissions.allowedTools // []) + [
        "Bash(.claude/skills/ck/ck *)",
        "Bash(.claude\\skills\\ck\\ck.cmd *)"
      ])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Added ck allowedTools permissions to settings.json"
    else
      echo "  ck allowedTools permissions already present — skipping."
    fi
    if ! jq -e '[.hooks.PreToolUse[]?.hooks[]?.command // empty] | any(test("ck-read-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse += [{"matcher":"Read","hooks":[
        {"type":"command","command":".claude/hooks/ck-read-guard.sh"},
        {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-read-guard.ps1 || exit 0'\''"}
      ]}]' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Registered Read hook in settings.json"
    else
      echo "  Read hook already registered — skipping."
    fi
    if ! jq -e '[.hooks.PreToolUse[]?.hooks[]?.command // empty] | any(test("ck-search-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse += [
        {"matcher":"Glob","hooks":[
          {"type":"command","command":".claude/hooks/ck-search-guard.sh"},
          {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-search-guard.ps1 || exit 0'\''"}
        ]},
        {"matcher":"Grep","hooks":[
          {"type":"command","command":".claude/hooks/ck-search-guard.sh"},
          {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-search-guard.ps1 || exit 0'\''"}
        ]}
      ]' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Registered Glob/Grep hooks in settings.json"
    else
      echo "  Glob/Grep hooks already registered — skipping."
    fi
    if ! jq -e '[.hooks.PreToolUse[]?.hooks[]?.command // empty] | any(test("agent-usage-guard"))' \
         "$SETTINGS" >/dev/null 2>&1; then
      jq '.hooks.PreToolUse += [{"matcher":"Agent","hooks":[
        {"type":"command","command":".claude/hooks/agent-usage-guard.sh"},
        {"type":"command","command":"bash -c '\''command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/agent-usage-guard.ps1 || exit 0'\''"}
      ]}]' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      echo "  Registered Agent hook in settings.json"
    else
      echo "  Agent hook already registered — skipping."
    fi
  else
    echo "  WARNING: jq not found. Add hooks to $SETTINGS manually:"
    echo '  {"hooks":{"PreToolUse":[{"matcher":"Read","hooks":[{"type":"command","command":".claude/hooks/ck-read-guard.sh"}]}]}}'
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

# ── Summary ────────────────────────────────────────────────────────────────────
echo "Done. Context King deployed for: $DETECTED_LABELS"
echo ""
echo "First use: run 'ck find-scope' — the index will be built automatically."
