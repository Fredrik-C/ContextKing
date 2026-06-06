#!/usr/bin/env bash
set -euo pipefail

# Regression test: uninstall-global.sh must only remove the exact
# project_doc_fallback_filenames line the installer writes, and must leave a
# user-owned value (which the installer deliberately skips) untouched.

tmp_home="$(mktemp -d)"
cleanup() { rm -rf "$tmp_home"; }
trap cleanup EXIT

codex_home="$tmp_home/codex"
mkdir -p "$codex_home"
cfg="$codex_home/config.toml"

fail() { echo "FAIL: $1" >&2; exit 1; }

run_uninstall() {
  bash scripts/uninstall-global.sh \
    --codex-home "$codex_home" \
    --no-path --no-claude --no-opencode --no-agents >/dev/null 2>&1
}

# Case 1: CK-installed line is removed.
printf 'model = "gpt-5"\nproject_doc_fallback_filenames = ["ck-code-search-protocol.md"]\n' > "$cfg"
run_uninstall
if grep -qF 'project_doc_fallback_filenames' "$cfg"; then
  fail "CK-installed fallback line was not removed"
fi
grep -qF 'model = "gpt-5"' "$cfg" || fail "unrelated config was lost"

# Case 2: user-owned single-line value is preserved (installer would have skipped it).
printf 'project_doc_fallback_filenames = ["AGENTS.md", "ck-code-search-protocol.md"]\n' > "$cfg"
run_uninstall
grep -qF 'project_doc_fallback_filenames = ["AGENTS.md", "ck-code-search-protocol.md"]' "$cfg" \
  || fail "user-owned single-line value was modified"

# Case 3: user-owned multi-line array is preserved intact (no partial deletion / dangling ]).
cat > "$cfg" <<'TOML'
project_doc_fallback_filenames = [
  "AGENTS.md",
  "ck-code-search-protocol.md",
]
TOML
run_uninstall
expected=$(cat <<'TOML'
project_doc_fallback_filenames = [
  "AGENTS.md",
  "ck-code-search-protocol.md",
]
TOML
)
if [ "$(cat "$cfg")" != "$expected" ]; then
  fail "user-owned multi-line array was corrupted"
fi

echo "PASS: uninstall removes only the CK-written fallback line"
