#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="${OPENHANDS_PROJECT_DIR:-$PWD}"
cd "$PROJECT_DIR"

CK_BIN="${CK_HOME:-$HOME/.ck}/bin/ck"
export PATH="${CK_HOME:-$HOME/.ck}/bin:${PATH}"

if ! command -v ck >/dev/null 2>&1; then
  echo "[openhands/setup] Installing Context King (minimal OpenHands footprint)..."
  curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-openhands-remote.sh | bash
  export PATH="${CK_HOME:-$HOME/.ck}/bin:${PATH}"
fi

if [ ! -x "$CK_BIN" ]; then
  echo "[openhands/setup] ck binary not found at $CK_BIN" >&2
  exit 1
fi

if [ ! -f ".ck.json" ]; then
  "$CK_BIN" init --quiet
fi

echo "[openhands/setup] Context King ready: $("$CK_BIN" --version 2>/dev/null || echo ck)"
