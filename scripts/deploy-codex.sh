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

echo "Deploying Context King to Codex home: $CODEX_HOME_DIR"

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

  # Add a minimal pointer entry to AGENTS.md at the repo root — just enough
  # for Codex to know the protocol file exists, without bloating AGENTS.md.
  AGENTS_MD="$TARGET_REPO/AGENTS.md"
  if [ ! -f "$AGENTS_MD" ] || ! grep -q 'ck-code-search-protocol' "$AGENTS_MD" 2>/dev/null; then
    printf '\n## Context King — code search protocol\n\nThis repo has Context King installed for fast C# navigation.\nRead `.codex/ck-code-search-protocol.md` for mandatory instructions before browsing `.cs` files.\n' >> "$AGENTS_MD"
    echo "  Added Context King pointer to AGENTS.md"
  else
    echo "  AGENTS.md already references CK — skipping."
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
