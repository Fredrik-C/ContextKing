#!/usr/bin/env bash
set -euo pipefail

# Regression test: when opencode.jsonc has comments, installer keeps comments
# while adding CK plugin/instruction entries.

tmp_home="$(mktemp -d)"
cleanup() { rm -rf "$tmp_home"; }
trap cleanup EXIT

mkdir -p "$tmp_home/opencode"
cat > "$tmp_home/opencode/opencode.jsonc" <<'JSONC'
{
  // top-level comment must survive
  "plugin": [
    "existing-plugin", // inline comment must survive
  ],
  "instructions": [],
  "permission": {
    "bash": {
      "*": "ask"
    }
  }
}
JSONC

bash scripts/install-global.sh \
  --ck-home "$tmp_home/ck" \
  --claude-home "$tmp_home/claude" \
  --codex-home "$tmp_home/codex" \
  --opencode-home "$tmp_home/opencode" \
  --agents-home "$tmp_home/agents" \
  --no-path --no-claude --no-codex --no-agents >/dev/null 2>&1

cfg="$tmp_home/opencode/opencode.jsonc"

rg -q "top-level comment must survive" "$cfg"
rg -q "inline comment must survive" "$cfg"
rg -q "\./plugins/ck-guards.ts" "$cfg"
rg -q "\./ck-code-search-protocol.md" "$cfg"

echo "PASS: installer preserves JSONC comments while adding CK entries"
