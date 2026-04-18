#!/usr/bin/env bash
# ck-update-check.sh — SessionStart hook.
# Checks if a newer Context King release is available on GitHub.
# Non-blocking: shows a notice to the user, never fails the session.
# Caches the result for 24 hours to avoid repeated API calls.

GITHUB_OWNER="Fredrik-C"
GITHUB_REPO="ContextKing"
CACHE_HOURS=24

# Find the ck binary relative to this hook
HOOK_DIR="$(cd "$(dirname "$0")" && pwd)"
CK_BIN="$HOOK_DIR/../skills/ck/ck"

# Bail silently if ck binary not found
[ -x "$CK_BIN" ] || exit 0

# Get installed version (e.g. "ck 1.3.1" → "1.3.1")
INSTALLED=$("$CK_BIN" --version 2>/dev/null | sed 's/^ck //')
[ -z "$INSTALLED" ] && exit 0

# Cache file — stored next to the index so it's gitignored
REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || echo "")
[ -z "$REPO_ROOT" ] && exit 0
CACHE_DIR="$REPO_ROOT/.ck-index"
mkdir -p "$CACHE_DIR"
CACHE_FILE="$CACHE_DIR/update-check"

# Check cache — skip if checked recently
if [ -f "$CACHE_FILE" ]; then
  if command -v stat >/dev/null 2>&1; then
    # macOS stat
    if stat -f %m "$CACHE_FILE" >/dev/null 2>&1; then
      CACHE_AGE=$(( $(date +%s) - $(stat -f %m "$CACHE_FILE") ))
    else
      # Linux stat
      CACHE_AGE=$(( $(date +%s) - $(stat -c %Y "$CACHE_FILE") ))
    fi
    CACHE_MAX=$(( CACHE_HOURS * 3600 ))
    if [ "$CACHE_AGE" -lt "$CACHE_MAX" ]; then
      # Cache is fresh — read cached result
      CACHED_LATEST=$(cat "$CACHE_FILE")
      if [ "$CACHED_LATEST" != "$INSTALLED" ] && [ -n "$CACHED_LATEST" ]; then
        echo "[Context King] Update available: v${CACHED_LATEST} (installed: v${INSTALLED}). Run: curl -fsSL https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/main/scripts/install.sh | bash"
      fi
      exit 0
    fi
  fi
fi

# Query GitHub API for latest release (with 5s timeout, non-blocking)
LATEST=""
if command -v curl >/dev/null 2>&1; then
  LATEST=$(curl -fsSL --connect-timeout 5 --max-time 10 \
    "https://api.github.com/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases/latest" 2>/dev/null \
    | grep '"tag_name"' | head -1 | sed 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/')
fi

# If curl failed or no result, try wget
if [ -z "$LATEST" ] && command -v wget >/dev/null 2>&1; then
  LATEST=$(wget -q --timeout=10 -O - \
    "https://api.github.com/repos/${GITHUB_OWNER}/${GITHUB_REPO}/releases/latest" 2>/dev/null \
    | grep '"tag_name"' | head -1 | sed 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/')
fi

# Cache the result regardless
[ -n "$LATEST" ] && echo "$LATEST" > "$CACHE_FILE"

# Compare and notify
if [ -n "$LATEST" ] && [ "$LATEST" != "$INSTALLED" ]; then
  echo "[Context King] Update available: v${LATEST} (installed: v${INSTALLED}). Run: curl -fsSL https://raw.githubusercontent.com/${GITHUB_OWNER}/${GITHUB_REPO}/main/scripts/install.sh | bash"
fi

exit 0
