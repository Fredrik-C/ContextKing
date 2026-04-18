#!/usr/bin/env bash
# install.sh — Context King installer.
#
# Downloads the platform-specific release archive from GitHub Releases,
# extracts it, and runs deploy.sh to install into the target repo.
#
# All assets come from the release archive — nothing is fetched from main.
# This ensures the installed version matches the release exactly.
#
# ── One-liner install (pipe from GitHub) ──────────────────────────────────────
#   curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install.sh | bash
#   curl -fsSL .../install.sh | bash -s -- /path/to/target-repo
#
# ── Download and run ──────────────────────────────────────────────────────────
#   curl -fsSL .../install.sh -o install.sh && bash install.sh [target-repo]
#
# ── Run from a cloned repo (uses local assets, no download needed) ────────────
#   bash scripts/install.sh [target-repo]
#
# If target-repo is omitted, the current working directory is used.

set -euo pipefail

GITHUB_OWNER="Fredrik-C"
GITHUB_REPO="ContextKing"
GITHUB_RELEASE="https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}/releases/latest/download"

# ── Helpers ────────────────────────────────────────────────────────────────────
die()      { echo "Error: $*" >&2; exit 1; }
info()     { echo "$*"; }

download() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL -o "$dest" "$url" || die "Failed to download: $url"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$dest" "$url" || die "Failed to download: $url"
  else
    die "Neither curl nor wget found. Install one and retry."
  fi
}

detect_platform() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os" in
    Darwin)
      case "$arch" in
        arm64)  echo "osx-arm64" ;;
        x86_64) echo "osx-x64" ;;
        *)      die "Unsupported macOS architecture: $arch" ;;
      esac ;;
    Linux)
      case "$arch" in
        x86_64|amd64) echo "linux-x64" ;;
        *)            die "Unsupported Linux architecture: $arch" ;;
      esac ;;
    CYGWIN*|MINGW*|MSYS*)
      echo "win-x64" ;;
    *)
      die "Unsupported OS: $os. On Windows, use install.ps1 instead." ;;
  esac
}

# ── Determine target repo ─────────────────────────────────────────────────────
TARGET="${1:-$(pwd)}"
[ -d "$TARGET" ] || die "Target directory does not exist: $TARGET"
TARGET="$(cd "$TARGET" && pwd)"

# ── Detect if running from inside the ContextKing repo ──────────────────────
LOCAL_REPO=""
_self="${BASH_SOURCE[0]:-}"
if [ -n "$_self" ] && [ -f "$(dirname "$_self")/deploy.sh" ]; then
  _scripts_dir="$(cd "$(dirname "$_self")" && pwd)"
  _repo_dir="$(dirname "$_scripts_dir")"
  if [ -f "$_repo_dir/skills/ck/ck" ]; then
    LOCAL_REPO="$_repo_dir"
  fi
fi
if [ -z "$LOCAL_REPO" ] && [ -f "$(pwd)/scripts/deploy.sh" ] && [ -f "$(pwd)/skills/ck/ck" ]; then
  LOCAL_REPO="$(pwd)"
fi

# ── Detect configured CLI tools ───────────────────────────────────────────────
HAS_CLAUDE=false
HAS_CODEX=false
HAS_OPENCODE=false
HAS_AGENTS=false

[ -d "$TARGET/.claude" ]   && HAS_CLAUDE=true
[ -d "$TARGET/.codex" ]    && HAS_CODEX=true
[ -d "$TARGET/.opencode" ] && HAS_OPENCODE=true
[ -d "$TARGET/.agents" ]   && HAS_AGENTS=true

if [ "$HAS_CLAUDE" = false ] && [ "$HAS_CODEX" = false ] && [ "$HAS_OPENCODE" = false ] && [ "$HAS_AGENTS" = false ]; then
  echo ""
  echo "No AI CLI configuration found in: $TARGET"
  echo ""
  echo "Context King supports the following CLI tools:"
  echo "  Claude Code  → run 'claude' in your repo to initialize (.claude/ will be created)"
  echo "  Codex CLI    → run 'codex init' in your repo (.codex/ will be created)"
  echo "  OpenCode     → run 'opencode' in your repo (.opencode/ will be created)"
  echo "  Agents       → create .agents/ directory manually"
  echo ""
  echo "Initialize at least one CLI tool, then re-run install.sh."
  exit 1
fi

DETECTED=""
[ "$HAS_CLAUDE" = true ]   && DETECTED="${DETECTED}Claude Code, "
[ "$HAS_CODEX" = true ]    && DETECTED="${DETECTED}Codex CLI, "
[ "$HAS_OPENCODE" = true ] && DETECTED="${DETECTED}OpenCode, "
[ "$HAS_AGENTS" = true ]   && DETECTED="${DETECTED}Agents, "
DETECTED="${DETECTED%, }"

echo ""
echo "Context King Installer"
echo "  Target : $TARGET"
echo "  Deploy : $DETECTED"
echo ""

# ── Acquire assets ────────────────────────────────────────────────────────────
if [ -n "$LOCAL_REPO" ]; then
  info "Using local assets from: $LOCAL_REPO"
  bash "$LOCAL_REPO/scripts/deploy.sh" "$TARGET"
else
  PLATFORM="$(detect_platform)"
  ARCHIVE="context-king-${PLATFORM}.tar.gz"
  ARCHIVE_URL="${GITHUB_RELEASE}/${ARCHIVE}"

  info "Downloading $ARCHIVE from latest release..."

  TMPDIR_CK="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$TMPDIR_CK'" EXIT

  download "$ARCHIVE_URL" "$TMPDIR_CK/$ARCHIVE"
  info "Extracting..."
  tar -xzf "$TMPDIR_CK/$ARCHIVE" -C "$TMPDIR_CK"

  ASSETS_DIR="$TMPDIR_CK/context-king"
  [ -d "$ASSETS_DIR" ] || die "Archive did not contain expected context-king/ directory"

  info ""
  bash "$ASSETS_DIR/scripts/deploy.sh" "$TARGET"
fi
