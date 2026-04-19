#!/usr/bin/env bash
# deploy-opencode.sh — installs Context King assets into a target repo's .opencode/ directory.
#
# Usage:
#   bash scripts/deploy-opencode.sh <target-repo-root>
#
# After deploy, the target repo's .opencode/ will contain:
#   models/bge-small-en-v1.5/   — embedding model
#   skills/ck/                  — ck binaries + platform wrapper
#   skills/ck-find-scope/       — SKILL.md (opencode paths)
#   skills/ck-signatures/       — SKILL.md (opencode paths)
#   skills/ck-get-method-source/ — SKILL.md (opencode paths)
#   skills/ck-index/            — SKILL.md (opencode paths)
#   AGENTS.md                   — code search protocol instructions
#   config.json                 — tool allowlist (created or merged)

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:?Usage: deploy-opencode.sh <target-repo-root>}"

normalize_path() {
  local input="$1"
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -u "$input" 2>/dev/null || printf '%s' "$input"
  elif [[ "$input" =~ ^([A-Za-z]):[\\/]?(.*)$ ]]; then
    local drive="${BASH_REMATCH[1],,}"
    local rest="${BASH_REMATCH[2]}"
    rest="${rest//\\//}"
    printf '/mnt/%s/%s' "$drive" "$rest"
  else
    printf '%s' "$input"
  fi
}

TARGET="$(normalize_path "$TARGET")"
DOT_OPENCODE="$TARGET/.opencode"

echo "Deploying Context King to OpenCode: $DOT_OPENCODE"
mkdir -p "$DOT_OPENCODE"

# ── 1. Copy models ─────────────────────────────────────────────────────────────
echo "  Copying models..."
mkdir -p "$DOT_OPENCODE/models"
cp -r "$REPO_DIR/models/." "$DOT_OPENCODE/models/"

# ── 2. Copy skills (with path rewriting) ────────────────────────────────────────
echo "  Copying skills..."
mkdir -p "$DOT_OPENCODE/skills"
cp -r "$REPO_DIR/skills/." "$DOT_OPENCODE/skills/"

# Rewrite .claude/ paths → .opencode/ in SKILL.md files
while IFS= read -r -d '' skill_file; do
  tmp_file="$(mktemp)"
  sed \
    -e 's|\.claude/skills/ck/|.opencode/skills/ck/|g' \
    -e 's|\.claude\\skills\\ck\\ck\.cmd|.opencode\\skills\\ck\\ck.cmd|g' \
    "$skill_file" > "$tmp_file"
  mv "$tmp_file" "$skill_file"
done < <(find "$DOT_OPENCODE/skills" -type f -name 'SKILL.md' -print0)

# Ensure binaries are executable
chmod +x \
  "$DOT_OPENCODE/skills/ck/ck" \
  "$DOT_OPENCODE/skills/ck/ck-osx-arm64" \
  "$DOT_OPENCODE/skills/ck/ck-osx-x64" \
  "$DOT_OPENCODE/skills/ck/ck-linux-x64" \
  2>/dev/null || true

# ── 3. Write protocol file + AGENTS.md inline workflow ─────────────────────────
# Full protocol goes to a dedicated file; AGENTS.md gets the core 4-step
# workflow inline so agents see it without needing to follow a pointer.
PROTOCOL_FILE="$DOT_OPENCODE/ck-code-search-protocol.md"
cp "$REPO_DIR/rules/ck-code-search-protocol.md" "$PROTOCOL_FILE"
# Rewrite .claude/ binary paths → .opencode/
sed -i.bak \
  -e 's|\.claude/skills/ck/ck |.opencode/skills/ck/ck |g' \
  -e 's|\.claude\\skills\\ck\\ck\.cmd|.opencode\\skills\\ck\\ck.cmd|g' \
  "$PROTOCOL_FILE" && rm -f "$PROTOCOL_FILE.bak"
echo "  Wrote CK code search protocol to .opencode/ck-code-search-protocol.md"

write_ck_agents_block() {
  cat <<'AGENTSEOF'
## Context King — code search protocol

This repo has Context King installed for fast C# and TypeScript/TSX navigation.
Full reference: `.opencode/ck-code-search-protocol.md`

### Mandatory workflow for .cs / .ts / .tsx files

```
1. SCOPE   → .opencode/skills/ck/ck find-scope --query "domain area concept operation"
2. EXPLORE → .opencode/skills/ck/ck expand-folder --pattern "<keyword>" <folder>
3. READ    → .opencode/skills/ck/ck get-method-source <file> <MemberName>
4. EDIT    → make your changes
```

Use `ck signatures <folder>` at step 2 only when you need all members with no filter.
Do not read source files before running step 1.
AGENTSEOF
}

AGENTS_MD="$DOT_OPENCODE/AGENTS.md"
if grep -q 'expand-folder' "$AGENTS_MD" 2>/dev/null; then
  echo "  .opencode/AGENTS.md already has CK expand-folder workflow — skipping."
elif grep -q 'ck-code-search-protocol' "$AGENTS_MD" 2>/dev/null; then
  # Upgrade: remove old pointer-only CK section, append new inline workflow
  tmp_file="$(mktemp)"
  awk '
    /^## Context King/ { in_ck=1; next }
    in_ck && /^## /    { in_ck=0 }
    !in_ck             { print }
  ' "$AGENTS_MD" > "$tmp_file"
  write_ck_agents_block >> "$tmp_file"
  mv "$tmp_file" "$AGENTS_MD"
  echo "  Upgraded Context King section in .opencode/AGENTS.md (added expand-folder workflow)"
else
  write_ck_agents_block >> "$AGENTS_MD"
  echo "  Added Context King inline workflow to .opencode/AGENTS.md"
fi

# ── 4. Copy plugin (OpenCode hook enforcement) ─────────────────────────────────
echo "  Copying plugin..."
mkdir -p "$DOT_OPENCODE/plugin"
cp "$REPO_DIR/plugins/ck-guards.ts" "$DOT_OPENCODE/plugin/ck-guards.ts"
echo "  Deployed ck-guards.ts to .opencode/plugin/ (auto-loaded by OpenCode)"

# ── 5. Update .opencode/config.json tool allowlist (idempotent) ────────────────
CONFIG_JSON="$DOT_OPENCODE/config.json"
if [ ! -f "$CONFIG_JSON" ]; then
  echo '{}' > "$CONFIG_JSON"
fi

if command -v jq >/dev/null 2>&1; then
  if ! jq -e '.tools.bash.allow // [] | any(test("opencode/skills/ck/ck"))' \
       "$CONFIG_JSON" >/dev/null 2>&1; then
    jq --arg win '.opencode\skills\ck\ck.cmd *' \
      '.tools.bash.allow = ((.tools.bash.allow // []) + [".opencode/skills/ck/ck *", $win])' \
      "$CONFIG_JSON" > "$CONFIG_JSON.tmp" && mv "$CONFIG_JSON.tmp" "$CONFIG_JSON"
    echo "  Added ck to .opencode/config.json tool allowlist"
  else
    echo "  ck already in .opencode/config.json tool allowlist — skipping."
  fi
else
  echo "  WARNING: jq not found. Add the following to $CONFIG_JSON manually:"
  echo '  {"tools":{"bash":{"allow":[".opencode/skills/ck/ck *",".opencode\\skills\\ck\\ck.cmd *"]}}}'
fi

# ── 6. Add .ck-index/ to .gitignore ──────────────────────────────────────────────
GITIGNORE="$TARGET/.gitignore"
if [ -f "$GITIGNORE" ]; then
  if ! grep -qF '.ck-index' "$GITIGNORE"; then
    echo '' >> "$GITIGNORE"
    echo '# Context King index' >> "$GITIGNORE"
    echo '.ck-index/' >> "$GITIGNORE"
    echo "  Added .ck-index/ to .gitignore"
  fi
else
  printf '# Context King index\n.ck-index/\n' > "$GITIGNORE"
  echo "  Created .gitignore with .ck-index/"
fi

echo ""
echo "Done. Context King is deployed for OpenCode."
echo ""
echo "First use: run 'ck find-scope' — the index will be built automatically."
echo "Or pre-build now: cd \"$TARGET\" && .opencode/skills/ck/ck index"
