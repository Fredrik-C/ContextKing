#!/usr/bin/env bash
# deploy-codex.sh — installs Context King assets into Codex home.
#
# Usage:
#   bash scripts/deploy-codex.sh
#   bash scripts/deploy-codex.sh --codex-home /path/to/.codex
#   bash scripts/deploy-codex.sh --target-repo /path/to/repo
#
# Notes:
# - Copies models and skills into Codex home.
# - Rewrites skill command examples from .claude paths to Codex-home paths.
# - Does not install Claude-specific hooks/settings.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
CODEX_HOME_DIR="${CODEX_HOME:-$HOME/.codex}"
TARGET_REPO=""

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

while [ "$#" -gt 0 ]; do
  case "$1" in
    --codex-home)
      CODEX_HOME_DIR="$2"
      shift 2
      ;;
    --target-repo)
      TARGET_REPO="$2"
      shift 2
      ;;
    -h|--help)
      echo "Usage: deploy-codex.sh [--codex-home <path>] [--target-repo <path>]"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      echo "Usage: deploy-codex.sh [--codex-home <path>] [--target-repo <path>]" >&2
      exit 1
      ;;
  esac
done

CODEX_HOME_DIR="$(normalize_path "$CODEX_HOME_DIR")"
if [ -n "$TARGET_REPO" ]; then
  TARGET_REPO="$(normalize_path "$TARGET_REPO")"
fi

mkdir -p "$CODEX_HOME_DIR"
CODEX_HOME_DIR="$(cd "$CODEX_HOME_DIR" && pwd)"

MODELS_DIR="$CODEX_HOME_DIR/models"
SKILLS_DIR="$CODEX_HOME_DIR/skills"

# ── Purge helpers ──────────────────────────────────────────────────────────────
purge_ck_skills() {
  local skills_root="$1"
  [ -d "$skills_root" ] || return 0
  [ -d "$skills_root/ck" ] && rm -rf "$skills_root/ck"
  for d in "$skills_root"/ck-*; do
    [ -d "$d" ] || continue
    rm -rf "$d"
  done
}

purge_ck_models() {
  local models_root="$1"
  [ -d "$models_root/bge-small-en-v1.5" ] && rm -rf "$models_root/bge-small-en-v1.5"
  return 0
}

purge_ck_index() {
  local repo_root="$1"
  if [ -d "$repo_root/.ck-index" ]; then
    rm -rf "$repo_root/.ck-index"
    echo "  Removed .ck-index/ (will rebuild on next ck find-scope)"
  fi
}

echo "Deploying Context King to Codex home: $CODEX_HOME_DIR"

# Purge previously-deployed CK assets in Codex home so removed skills don't linger.
purge_ck_skills "$SKILLS_DIR"
purge_ck_models "$MODELS_DIR"

echo "  Copying models..."
mkdir -p "$MODELS_DIR"
cp -r "$REPO_DIR/models/." "$MODELS_DIR/"

echo "  Copying skills..."
mkdir -p "$SKILLS_DIR"
cp -r "$REPO_DIR/skills/." "$SKILLS_DIR/"

chmod +x \
  "$SKILLS_DIR/ck/ck" \
  "$SKILLS_DIR/ck/ck-linux-x64" \
  "$SKILLS_DIR/ck/ck-osx-arm64" \
  "$SKILLS_DIR/ck/ck-osx-x64" \
  2>/dev/null || true

echo "  Rewriting skill paths for Codex..."
while IFS= read -r -d '' skill_file; do
  tmp_file="$(mktemp)"
  sed \
    -e 's|\.claude\\skills\\ck\\ck\.cmd|\& "$($env:CODEX_HOME ? $env:CODEX_HOME : (Join-Path $HOME ".codex"))\\skills\\ck\\ck.cmd"|g' \
    -e 's|\.claude/skills/ck/|${CODEX_HOME:-$HOME/.codex}/skills/ck/|g' \
    "$skill_file" > "$tmp_file"
  mv "$tmp_file" "$skill_file"
done < <(find "$SKILLS_DIR" -type f -name 'SKILL.md' -print0)

if [ -n "$TARGET_REPO" ]; then
  if [ ! -d "$TARGET_REPO" ]; then
    echo "Target repo does not exist or is not a directory: $TARGET_REPO" >&2
    exit 1
  fi

  # Purge stale CK index in the target repo so it rebuilds cleanly.
  purge_ck_index "$TARGET_REPO"

  GITIGNORE="$TARGET_REPO/.gitignore"
  if [ -f "$GITIGNORE" ]; then
    if ! grep -qF '.ck-index' "$GITIGNORE"; then
      {
        echo ''
        echo '# Context King index'
        echo '.ck-index/'
      } >> "$GITIGNORE"
      echo "  Added .ck-index/ to .gitignore"
    else
      echo "  .ck-index/ already present in .gitignore"
    fi
  else
    printf '# Context King index\n.ck-index/\n' > "$GITIGNORE"
    echo "  Created .gitignore with .ck-index/"
  fi

  # Write the full CK code search protocol to a dedicated file so it doesn't
  # pollute the repo's own AGENTS.md with a large block of instructions.
  CODEX_REPO_DIR="$TARGET_REPO/.codex"
  mkdir -p "$CODEX_REPO_DIR"
  PROTOCOL_FILE="$CODEX_REPO_DIR/ck-code-search-protocol.md"
  cp "$REPO_DIR/rules/ck-code-search-protocol.md" "$PROTOCOL_FILE"
  # Rewrite .claude/ binary paths to the global Codex home path
  sed -i.bak \
    -e 's|\.claude/skills/ck/ck |${CODEX_HOME:-$HOME/.codex}/skills/ck/ck |g' \
    -e 's|\.claude\\skills\\ck\\ck\.cmd|& "$($env:CODEX_HOME ? $env:CODEX_HOME : (Join-Path $HOME ".codex"))\\skills\\ck\\ck.cmd"|g' \
    "$PROTOCOL_FILE" && rm -f "$PROTOCOL_FILE.bak"
  echo "  Wrote CK code search protocol to .codex/ck-code-search-protocol.md"

  # Write the inline 4-step CK workflow to AGENTS.md so agents see it without
  # needing to follow a pointer to the protocol file.
  write_ck_agents_block() {
    cat <<'AGENTSEOF'

## Context King — code search protocol

This repo has Context King installed for fast C# and TypeScript/TSX navigation.
Full reference: `.codex/ck-code-search-protocol.md`

### Mandatory workflow for .cs / .ts / .tsx files

```
1. SCOPE   → ${CODEX_HOME:-$HOME/.codex}/skills/ck/ck find-scope --query "domain area concept operation"
2. EXPLORE → ${CODEX_HOME:-$HOME/.codex}/skills/ck/ck expand-folder --pattern "<keyword>" <folder>
3. READ    → ${CODEX_HOME:-$HOME/.codex}/skills/ck/ck get-method-source <file> <MemberName>
4. EDIT    → make your changes
```

Use `ck signatures <folder>` at step 2 only when you need all members with no filter.
Do not read source files before running step 1.
AGENTSEOF
  }

  AGENTS_MD="$TARGET_REPO/AGENTS.md"
  if grep -q 'expand-folder' "$AGENTS_MD" 2>/dev/null; then
    echo "  AGENTS.md already has CK expand-folder workflow — skipping."
  elif grep -q 'ck-code-search-protocol' "$AGENTS_MD" 2>/dev/null; then
    # Upgrade: remove old pointer-only CK section, append new inline workflow
    tmp_file="$(mktemp)"
    awk '
      /^## Context King/ { in_ck=1; next }
      in_ck && /^## /    { in_ck=0 }
      !in_ck             { print }
    ' "$AGENTS_MD" > "$tmp_file"
    write_ck_agents_block >> "$tmp_file"
    mv "$tmp_file" "$AGENTS_MD"
    echo "  Upgraded Context King section in AGENTS.md (added expand-folder workflow)"
  else
    write_ck_agents_block >> "$AGENTS_MD"
    echo "  Added Context King inline workflow to AGENTS.md"
  fi
fi

echo ""
echo "Done. Context King is deployed for Codex."
echo ""
echo "Notes:"
echo "  - Claude-specific hooks/settings were intentionally not installed."
echo "  - Use ck via skills or directly from:"
echo "    $SKILLS_DIR/ck/ck.cmd (Windows)"
echo "    $SKILLS_DIR/ck/ck     (Mac/Linux)"
