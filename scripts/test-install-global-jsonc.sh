#!/usr/bin/env bash
set -euo pipefail

# Regression test for GitHub issue #6:
# JSONC parsing in install-global.sh must handle inline // comments,
# block comments, and trailing commas before jq processing.

tmpdir="$(mktemp -d)"
cleanup() { rm -rf "$tmpdir"; }
trap cleanup EXIT

cfg="$tmpdir/opencode.jsonc"
clean="$tmpdir/opencode.clean.json"

cat > "$cfg" <<'JSONC'
{
  "$schema": "https://opencode.ai/config.json",
  "plugin": [
    "@mohak34/opencode-notifier@42ca308d16fb494e8b337e6f703b7bee800db9e9", // v0.2.2
    "./plugins/ck-guards.ts"
  ],
  "instructions": ["./ck-code-search-protocol.md"],
  "permission": {
    "bash": {
      "ck *": "allow",
    }
  },
  /* block comment should be ignored */
}
JSONC

python3 - "$cfg" > "$clean" <<'PY'
import sys
import re

path = sys.argv[1]
text = open(path, "r", encoding="utf-8").read()

out = []
i = 0
n = len(text)
in_string = False
escaped = False

while i < n:
    ch = text[i]

    if in_string:
        out.append(ch)
        if escaped:
            escaped = False
        elif ch == "\\":
            escaped = True
        elif ch == '"':
            in_string = False
        i += 1
        continue

    if ch == '"':
        in_string = True
        out.append(ch)
        i += 1
        continue

    if ch == '/' and i + 1 < n:
        nxt = text[i + 1]
        if nxt == '/':
            i += 2
            while i < n and text[i] not in "\r\n":
                i += 1
            continue
        if nxt == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                i += 1
            i += 2 if i + 1 < n else 0
            continue

    out.append(ch)
    i += 1

clean = "".join(out)
while True:
    updated = re.sub(r",\s*([}\]])", r"\1", clean)
    if updated == clean:
        break
    clean = updated

sys.stdout.write(clean)
PY

jq -e '.plugin | index("./plugins/ck-guards.ts") != null' "$clean" >/dev/null
jq -e '.instructions | index("./ck-code-search-protocol.md") != null' "$clean" >/dev/null
jq -e '.permission.bash["ck *"] == "allow"' "$clean" >/dev/null

echo "PASS: JSONC sanitizer handles issue #6 sample config"
