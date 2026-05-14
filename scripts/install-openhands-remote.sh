#!/usr/bin/env bash
set -euo pipefail

# Minimal remote installer for OpenHands runtimes.
# Installs:
# - ~/.ck/bin/ck
# - ~/.ck/models/bge-small-en-v1.5
# - ~/.agents/skills/ck*
#
# Skips CLI-specific integrations for Claude/Codex/OpenCode.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-openhands-remote.sh | bash
#
# Optional environment variables:
#   CK_VERSION=latest|v1.8.2    (default: latest)
#   CK_GITHUB_OWNER=Fredrik-C
#   CK_GITHUB_REPO=ContextKing
#   CK_HOME=/custom/.ck
#   CK_AGENTS_HOME=/custom/.agents
#   CK_SKIP_AGENTS=1            (skip ~/.agents/skills install)
#
# Any additional arguments are forwarded to install-global.sh.

OWNER="${CK_GITHUB_OWNER:-Fredrik-C}"
REPO="${CK_GITHUB_REPO:-ContextKing}"
VERSION="${CK_VERSION:-latest}"

if [ "$VERSION" = "latest" ]; then
  INSTALLER_URL="https://github.com/${OWNER}/${REPO}/releases/latest/download/install-global.sh"
else
  INSTALLER_URL="https://github.com/${OWNER}/${REPO}/releases/download/${VERSION}/install-global.sh"
fi

download() {
  local url="$1"
  local dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL -o "$dest" "$url"
    return
  fi
  if command -v wget >/dev/null 2>&1; then
    wget -q -O "$dest" "$url"
    return
  fi
  echo "Error: curl or wget is required." >&2
  exit 1
}

TMP_INSTALLER="$(mktemp)"
trap 'rm -f "$TMP_INSTALLER"' EXIT

download "$INSTALLER_URL" "$TMP_INSTALLER"
chmod +x "$TMP_INSTALLER"

INSTALL_ARGS=(
  --no-claude
  --no-codex
  --no-opencode
  --no-path
)

if [ -n "${CK_HOME:-}" ]; then
  INSTALL_ARGS+=(--ck-home "$CK_HOME")
fi

if [ -n "${CK_AGENTS_HOME:-}" ]; then
  INSTALL_ARGS+=(--agents-home "$CK_AGENTS_HOME")
fi

if [ "${CK_SKIP_AGENTS:-0}" = "1" ]; then
  INSTALL_ARGS+=(--no-agents)
fi

bash "$TMP_INSTALLER" "${INSTALL_ARGS[@]}" "$@"

EFFECTIVE_CK_HOME="${CK_HOME:-$HOME/.ck}"
export PATH="${EFFECTIVE_CK_HOME}/bin:${PATH}"

if ! command -v ck >/dev/null 2>&1; then
  echo "Error: ck was installed but is not on PATH. Expected: ${EFFECTIVE_CK_HOME}/bin/ck" >&2
  exit 1
fi

echo "Context King installed for OpenHands: $(ck --version 2>/dev/null || echo "ck (version unavailable)")"
echo "Add this to your runtime shell environment if needed:"
echo "  export PATH=\"${EFFECTIVE_CK_HOME}/bin:\$PATH\""
