#!/usr/bin/env bash
set -euo pipefail

# Regression test: if opencode.jsonc exists, installer must not create opencode.json.

tmp_home="$(mktemp -d)"
cleanup() { rm -rf "$tmp_home"; }
trap cleanup EXIT

mkdir -p "$tmp_home/opencode"
cat > "$tmp_home/opencode/opencode.jsonc" <<'JSONC'
{
  "plugin": ["./existing.ts"]
}
JSONC

bash scripts/install-global.sh \
  --ck-home "$tmp_home/ck" \
  --claude-home "$tmp_home/claude" \
  --codex-home "$tmp_home/codex" \
  --opencode-home "$tmp_home/opencode" \
  --agents-home "$tmp_home/agents" \
  --no-path --no-claude --no-codex --no-agents >/dev/null 2>&1

if [ -f "$tmp_home/opencode/opencode.json" ]; then
  echo "FAIL: opencode.json was created even though opencode.jsonc exists" >&2
  exit 1
fi

if [ ! -f "$tmp_home/opencode/opencode.jsonc" ]; then
  echo "FAIL: opencode.jsonc is missing after install" >&2
  exit 1
fi

echo "PASS: installer preserves jsonc-only config selection"
