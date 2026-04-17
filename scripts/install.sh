#!/usr/bin/env bash
# install.sh — Context King self-extracting installer.
#
# Downloads Context King and deploys it based on which AI CLI tools
# (.claude, .codex, .opencode) are already configured in the target repo.
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
GITHUB_BRANCH="main"
GITHUB_RAW="https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/${GITHUB_BRANCH}"
GITHUB_RELEASE="https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}/releases/latest/download"

# ── Helpers ────────────────────────────────────────────────────────────────────
die()      { echo "Error: $*" >&2; exit 1; }
info()     { echo "$*"; }
progress() { printf "  %s..." "$1"; }
done_()    { echo " done"; }

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
# Works for both: bash scripts/install.sh  AND  cat install.sh | bash
LOCAL_REPO=""
# When invoked as a script (not piped), $BASH_SOURCE[0] points to this file
_self="${BASH_SOURCE[0]:-}"
if [ -n "$_self" ] && [ -f "$(dirname "$_self")/deploy.sh" ]; then
  _scripts_dir="$(cd "$(dirname "$_self")" && pwd)"
  _repo_dir="$(dirname "$_scripts_dir")"
  if [ -f "$_repo_dir/skills/ck/ck" ]; then
    LOCAL_REPO="$_repo_dir"
  fi
fi
# Also check current directory (useful when running: bash install.sh from the repo root)
if [ -z "$LOCAL_REPO" ] && [ -f "$(pwd)/scripts/deploy.sh" ] && [ -f "$(pwd)/skills/ck/ck" ]; then
  LOCAL_REPO="$(pwd)"
fi

# ── Detect configured CLI tools ───────────────────────────────────────────────
HAS_CLAUDE=false
HAS_CODEX=false
HAS_OPENCODE=false

[ -d "$TARGET/.claude" ]   && HAS_CLAUDE=true
[ -d "$TARGET/.codex" ]    && HAS_CODEX=true
[ -d "$TARGET/.opencode" ] && HAS_OPENCODE=true

if [ "$HAS_CLAUDE" = false ] && [ "$HAS_CODEX" = false ] && [ "$HAS_OPENCODE" = false ]; then
  echo ""
  echo "No AI CLI configuration found in: $TARGET"
  echo ""
  echo "Context King supports the following CLI tools:"
  echo "  Claude Code  → run 'claude' in your repo to initialize (.claude/ will be created)"
  echo "  Codex CLI    → run 'codex init' in your repo (.codex/ will be created)"
  echo "  OpenCode     → run 'opencode' in your repo (.opencode/ will be created)"
  echo ""
  echo "Initialize at least one CLI tool, then re-run install.sh."
  exit 1
fi

DETECTED=""
[ "$HAS_CLAUDE" = true ]   && DETECTED="${DETECTED}Claude Code, "
[ "$HAS_CODEX" = true ]    && DETECTED="${DETECTED}Codex CLI, "
[ "$HAS_OPENCODE" = true ] && DETECTED="${DETECTED}OpenCode, "
DETECTED="${DETECTED%, }"

echo ""
echo "Context King Installer"
echo "  Target : $TARGET"
echo "  Deploy : $DETECTED"
echo ""

# ── Acquire assets ────────────────────────────────────────────────────────────
ASSETS_DIR=""
if [ -n "$LOCAL_REPO" ]; then
  info "Using local assets from: $LOCAL_REPO"
  ASSETS_DIR="$LOCAL_REPO"
else
  PLATFORM="$(detect_platform)"
  info "Downloading Context King assets for $PLATFORM from GitHub..."

  ASSETS_DIR="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$ASSETS_DIR'" EXIT

  # Deploy scripts
  mkdir -p "$ASSETS_DIR/scripts"
  for _script in deploy.sh deploy-codex.sh deploy-opencode.sh; do
    progress "scripts/$_script"
    download "${GITHUB_RAW}/scripts/${_script}" "$ASSETS_DIR/scripts/$_script"
    chmod +x "$ASSETS_DIR/scripts/$_script"
    done_
  done

  # Skills: SKILL.md files
  for _skill in ck-find-scope ck-search ck-signatures ck-get-method-source ck-index; do
    mkdir -p "$ASSETS_DIR/skills/$_skill"
    progress "skills/$_skill/SKILL.md"
    download "${GITHUB_RAW}/skills/${_skill}/SKILL.md" "$ASSETS_DIR/skills/$_skill/SKILL.md"
    done_
  done

  # Skills: binary wrapper scripts
  mkdir -p "$ASSETS_DIR/skills/ck"
  for _wrapper in ck ck.cmd; do
    progress "skills/ck/$_wrapper"
    download "${GITHUB_RAW}/skills/ck/${_wrapper}" "$ASSETS_DIR/skills/ck/$_wrapper"
    done_
  done
  chmod +x "$ASSETS_DIR/skills/ck/ck"

  # Platform binary (largest file — downloaded from GitHub Releases)
  _bin="ck-${PLATFORM}"
  [ "$PLATFORM" = "win-x64" ] && _bin="ck-win-x64.exe"
  progress "binary: $_bin (~30–46 MB, from latest release)"
  download "${GITHUB_RELEASE}/${_bin}" "$ASSETS_DIR/skills/ck/${_bin}"
  [ "$PLATFORM" != "win-x64" ] && chmod +x "$ASSETS_DIR/skills/ck/${_bin}"
  done_

  # Hooks
  mkdir -p "$ASSETS_DIR/hooks"
  for _hook in ck-read-guard.sh ck-read-guard.ps1 ck-search-guard.sh ck-search-guard.ps1 agent-usage-guard.sh agent-usage-guard.ps1; do
    progress "hooks/$_hook"
    download "${GITHUB_RAW}/hooks/${_hook}" "$ASSETS_DIR/hooks/$_hook"
    done_
  done
  chmod +x \
    "$ASSETS_DIR/hooks/ck-read-guard.sh" \
    "$ASSETS_DIR/hooks/ck-search-guard.sh" \
    "$ASSETS_DIR/hooks/agent-usage-guard.sh"

  # Models
  mkdir -p "$ASSETS_DIR/models/bge-small-en-v1.5/onnx"
  progress "models/bge-small-en-v1.5/vocab.txt"
  download "${GITHUB_RAW}/models/bge-small-en-v1.5/vocab.txt" \
    "$ASSETS_DIR/models/bge-small-en-v1.5/vocab.txt"
  done_
  progress "models/bge-small-en-v1.5/onnx/model_quantized.onnx (~34 MB)"
  download "${GITHUB_RAW}/models/bge-small-en-v1.5/onnx/model_quantized.onnx" \
    "$ASSETS_DIR/models/bge-small-en-v1.5/onnx/model_quantized.onnx"
  done_

  # Plugins (OpenCode hooks)
  mkdir -p "$ASSETS_DIR/plugins"
  progress "plugins/ck-guards.ts"
  download "${GITHUB_RAW}/plugins/ck-guards.ts" "$ASSETS_DIR/plugins/ck-guards.ts"
  done_

  # Rules
  mkdir -p "$ASSETS_DIR/rules"
  progress "rules/ck-code-search-protocol.md"
  download "${GITHUB_RAW}/rules/ck-code-search-protocol.md" \
    "$ASSETS_DIR/rules/ck-code-search-protocol.md"
  done_

  info ""
fi

# ── Run deployment ─────────────────────────────────────────────────────────────
# Pass --all so deploy.sh doesn't re-detect (we already know what to deploy).
# We construct the correct flags ourselves.
_deploy_flags=""
[ "$HAS_CLAUDE" = false ] && [ "$HAS_CODEX" = false ] && [ "$HAS_OPENCODE" = false ] && exit 1

# Since we already detected, temporarily create the directories so deploy.sh picks them up.
# (They already exist — this block is just in case install.sh is called with --all logic later.)

# Directly invoke the unified deploy from the assets dir.
# We pass TARGET as the first argument; detection inside deploy.sh will see the same dirs.
bash "$ASSETS_DIR/scripts/deploy.sh" "$TARGET"
