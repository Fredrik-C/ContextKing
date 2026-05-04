#!/usr/bin/env bash
# install-local-dev.sh — Local developer installer for Context King.
#
# Purpose:
# - Register/sync hooks + skills for supported CLIs (Claude, Codex, OpenCode, Agents)
# - Force-install a locally built ck binary (default: artifacts/publish/local/ck)
#   so local behavior is used instead of release asset binaries.
#
# Usage:
#   bash scripts/install-local-dev.sh
#   bash scripts/install-local-dev.sh --binary artifacts/publish/local/ck
#   bash scripts/install-local-dev.sh --no-claude --no-codex
#
# Notes:
# - All flags other than --binary are forwarded to scripts/install-global.sh.
# - Run from repository root (or keep default binary path valid from cwd).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GLOBAL_SCRIPT="$ROOT_DIR/scripts/install-global.sh"

[ -f "$GLOBAL_SCRIPT" ] || { echo "Error: missing $GLOBAL_SCRIPT" >&2; exit 1; }

BINARY_PATH="$ROOT_DIR/artifacts/publish/local/ck"
FORWARD_ARGS=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --binary)
      [ "$#" -ge 2 ] || { echo "Error: --binary requires a value" >&2; exit 1; }
      BINARY_PATH="$2"
      shift 2
      ;;
    *)
      FORWARD_ARGS+=("$1")
      shift
      ;;
  esac
done

# Resolve binary path to absolute form.
if [ "${BINARY_PATH#/}" = "$BINARY_PATH" ]; then
  BINARY_PATH="$(cd "$(dirname "$BINARY_PATH")" && pwd)/$(basename "$BINARY_PATH")"
fi

[ -f "$BINARY_PATH" ] || { echo "Error: local binary not found: $BINARY_PATH" >&2; exit 1; }
[ -x "$BINARY_PATH" ] || chmod +x "$BINARY_PATH"

CK_HOME="${CK_HOME:-$HOME/.ck}"
CLAUDE_HOME="${CLAUDE_HOME:-$HOME/.claude}"
CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
OPENCODE_HOME="${OPENCODE_HOME:-$HOME/.config/opencode}"
AGENTS_HOME="${AGENTS_HOME:-$HOME/.agents}"

echo "Installing CK integrations via install-global.sh..."
echo "Note: install-global.sh may install the packaged baseline binary first."
echo "install-local-dev.sh then force-overrides it with your local dev binary."
if [ "${#FORWARD_ARGS[@]}" -gt 0 ]; then
  bash "$GLOBAL_SCRIPT" "${FORWARD_ARGS[@]}"
else
  bash "$GLOBAL_SCRIPT"
fi

BASELINE_VERSION="unknown"
if [ -x "$CK_HOME/bin/ck" ]; then
  BASELINE_VERSION="$("$CK_HOME/bin/ck" --version 2>/dev/null || echo unknown)"
fi

LOCAL_VERSION="$("$BINARY_PATH" --version 2>/dev/null || echo unknown)"

echo "Force-syncing local dev binary:"
echo "  source: $BINARY_PATH"
echo "  target: $CK_HOME/bin/ck"
echo "  local binary version: $LOCAL_VERSION"

mkdir -p "$CK_HOME/bin"
cp "$BINARY_PATH" "$CK_HOME/bin/ck"
chmod +x "$CK_HOME/bin/ck"

# Keep embedded per-client ck copies aligned with the local dev binary.
for embedded in \
  "$CLAUDE_HOME/skills/ck/ck" \
  "$CODEX_HOME/skills/ck/ck" \
  "$OPENCODE_HOME/skills/ck/ck" \
  "$AGENTS_HOME/skills/ck/ck"
do
  if [ -d "$(dirname "$embedded")" ]; then
    cp "$BINARY_PATH" "$embedded"
    chmod +x "$embedded" 2>/dev/null || true
  fi
done

echo "Done."
FINAL_VERSION="$("$CK_HOME/bin/ck" --version 2>/dev/null || echo unknown)"
echo "Version summary:"
echo "  baseline after install-global.sh: $BASELINE_VERSION"
echo "  local dev binary source:          $LOCAL_VERSION"
echo "  final installed:                  $FINAL_VERSION"
