#!/usr/bin/env bash
# install-global.sh — Context King global user installer.
#
# This script targets macOS/Linux shell environments. For Windows, use
# scripts/install-global.ps1 (PowerShell).
#
# Installs ck as a global user-level tool so it is available in every project
# without requiring a per-repo deploy step. Per-repo initialization is done with
# `ck init` after the global install.
#
# ── One-liner install ──────────────────────────────────────────────────────────
#   curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.sh | bash
#
# ── Download and run ──────────────────────────────────────────────────────────
#   curl -fsSL .../install-global.sh -o install-global.sh && bash install-global.sh
#
# ── Run from a cloned repo (uses local assets, no download needed) ────────────
#   bash scripts/install-global.sh
#
# ── Options ───────────────────────────────────────────────────────────────────
#   --ck-home <path>    Override installation root (default: ~/.ck)
#   --claude-home <p>   Override Claude Code config dir (default: ~/.claude)
#   --codex-home <p>    Override Codex config dir (default: ~/.codex)
#   --opencode-home <p> Override OpenCode config dir (default: ~/.config/opencode)
#   --agents-home <p>   Override Agents skills dir (default: ~/.agents)
#   --no-path           Skip PATH modification in shell config files
#   --no-claude         Skip Claude Code registration
#   --no-codex          Skip Codex registration
#   --no-opencode       Skip OpenCode registration
#   --no-agents         Skip Agents registration
#
# What this script installs:
#   ~/.ck/bin/ck                         platform binary (symlinked from assets)
#   ~/.ck/models/bge-small-en-v1.5/      embedding model
#   ~/.claude/skills/ck*/                Claude Code skills (global)
#   ~/.claude/hooks/ck-*.sh              Claude Code hooks (global)
#   ~/.claude/rules/ck-code-search-protocol.md
#   ~/.claude/settings.json              hook + permission registrations (merged)
#   ~/.codex/skills/ck*/                 Codex skills (global)
#   ~/.codex/hooks/ck-*.sh               Codex hooks (global)
#   ~/.codex/hooks.json                  Codex hook registrations (merged)
#   ~/.codex/ck-code-search-protocol.md  Codex protocol reference
#   ~/.config/opencode/skills/ck*/       OpenCode skills (global)
#   ~/.config/opencode/plugins/ck-guards.ts
#   ~/.opencode/plugin/ck-guards.ts      Legacy OpenCode plugin path (synced)
#   ~/.agents/skills/ck*/                Generic Agent Skills (global)

set -euo pipefail

GITHUB_OWNER="Fredrik-C"
GITHUB_REPO="ContextKing"
GITHUB_RELEASE="https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}/releases/latest/download"

# ── Defaults ──────────────────────────────────────────────────────────────────
CK_HOME="${CK_HOME:-$HOME/.ck}"
CLAUDE_HOME="${CLAUDE_HOME:-$HOME/.claude}"
CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
OPENCODE_HOME="${OPENCODE_HOME:-$HOME/.config/opencode}"
LEGACY_OPENCODE_HOME="${LEGACY_OPENCODE_HOME:-$HOME/.opencode}"
AGENTS_HOME="${AGENTS_HOME:-$HOME/.agents}"
MODIFY_PATH=true
DO_CLAUDE=true
DO_CODEX=true
DO_OPENCODE=true
DO_AGENTS=true

while [ "$#" -gt 0 ]; do
  case "$1" in
    --ck-home)      CK_HOME="$2";      shift 2 ;;
    --claude-home)  CLAUDE_HOME="$2";  shift 2 ;;
    --codex-home)   CODEX_HOME="$2";   shift 2 ;;
    --opencode-home) OPENCODE_HOME="$2"; shift 2 ;;
    --agents-home)  AGENTS_HOME="$2";  shift 2 ;;
    --no-path)      MODIFY_PATH=false; shift ;;
    --no-claude)    DO_CLAUDE=false;   shift ;;
    --no-codex)     DO_CODEX=false;    shift ;;
    --no-opencode)  DO_OPENCODE=false; shift ;;
    --no-agents)    DO_AGENTS=false;   shift ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//' | head -30
      exit 0 ;;
    *) echo "Unknown flag: $1" >&2; exit 1 ;;
  esac
done

CK_BIN_DIR="$CK_HOME/bin"
CK_MODEL_DIR="$CK_HOME/models"
CK_BIN="$CK_BIN_DIR/ck"

# ── Helpers ────────────────────────────────────────────────────────────────────
die()  { echo "Error: $*" >&2; exit 1; }
info() { echo "$*"; }
ok()   { echo "  ✓ $*"; }

download() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL -o "$dest" "$url" || die "Failed to download: $url"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$dest" "$url" || die "Failed to download: $url"
  else
    die "Neither curl nor wget found."
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
        aarch64|arm64) echo "linux-arm64" ;;
        *)            die "Unsupported Linux architecture: $arch" ;;
      esac ;;
    CYGWIN*|MINGW*|MSYS*) echo "win-x64" ;;
    *) die "Unsupported OS: $os. On Windows, use install-global.ps1 instead." ;;
  esac
}

# Rewrite skill command paths from `.claude/skills/ck/ck` → absolute path.
# Uses the absolute binary path so Claude Code subshells find ck without needing
# the user's interactive PATH (which is not inherited by Claude Code's bash subprocess).
rewrite_skills_global() {
  local skills_root="$1"
  local ck_cmd="${2:-ck}"
  while IFS= read -r -d '' skill_file; do
    tmp="$(mktemp)"
    sed \
      -e "s|\.claude/skills/ck/ck |${ck_cmd} |g" \
      -e "s|\.claude\\\\skills\\\\ck\\\\ck\.cmd |${ck_cmd} |g" \
      -e "s|\.claude/skills/ck/ck\$|${ck_cmd}|g" \
      "$skill_file" > "$tmp"
    mv "$tmp" "$skill_file"
  done < <(find "$skills_root" -type f -name 'SKILL.md' -print0)
}

# Rewrite skill paths for a specific prefix (e.g. `.opencode/skills/ck/ck`).
rewrite_skills_prefix() {
  local skills_root="$1"
  local new_prefix="$2"
  while IFS= read -r -d '' skill_file; do
    tmp="$(mktemp)"
    sed \
      -e "s|\.claude/skills/ck/ck |${new_prefix} |g" \
      -e "s|\.claude/skills/ck/ck\$|${new_prefix}|g" \
      "$skill_file" > "$tmp"
    mv "$tmp" "$skill_file"
  done < <(find "$skills_root" -type f -name 'SKILL.md' -print0)
}

# Ensure embedded per-client skill binary points to the same binary as CK_BIN.
# Prevents stale copies in ~/.config/opencode/skills/ck/ck etc.
sync_embedded_ck_binary() {
  local home_root="$1"
  local embedded="$home_root/skills/ck/ck"
  [ -f "$CK_BIN" ] || return 0
  [ -d "$home_root/skills/ck" ] || return 0
  cp "$CK_BIN" "$embedded"
  chmod +x "$embedded" 2>/dev/null || true
}

purge_ck_skills() {
  local root="$1"
  [ -d "$root" ] || return 0
  rm -rf "$root/ck" 2>/dev/null || true
  for d in "$root"/ck-*; do [ -d "$d" ] && rm -rf "$d"; done 2>/dev/null || true
}

purge_ck_hooks() {
  local root="$1"
  [ -d "$root" ] || return 0
  rm -f "$root"/ck-*.sh "$root"/ck-*.ps1 "$root"/agent-usage-guard.sh "$root"/agent-usage-guard.ps1 2>/dev/null || true
}

# ── Detect whether running from a local clone ─────────────────────────────────
LOCAL_REPO=""
_self="${BASH_SOURCE[0]:-}"
if [ -n "$_self" ] && [ -f "$(dirname "$_self")/install-global.sh" ]; then
  _scripts_dir="$(cd "$(dirname "$_self")" && pwd)"
  _repo_dir="$(dirname "$_scripts_dir")"
  if [ -f "$_repo_dir/skills/ck/ck" ]; then
    LOCAL_REPO="$_repo_dir"
  fi
fi

# ── Acquire platform archive ───────────────────────────────────────────────────
ASSETS_DIR=""
TMPDIR_CK=""

if [ -n "$LOCAL_REPO" ]; then
  info "Using local assets from: $LOCAL_REPO"
  ASSETS_DIR="$LOCAL_REPO"
else
  PLATFORM="$(detect_platform)"
  ARCHIVE="context-king-${PLATFORM}.tar.gz"

  info "Downloading $ARCHIVE from latest release..."
  TMPDIR_CK="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$TMPDIR_CK'" EXIT

  download "${GITHUB_RELEASE}/${ARCHIVE}" "$TMPDIR_CK/$ARCHIVE"
  tar -xzf "$TMPDIR_CK/$ARCHIVE" -C "$TMPDIR_CK"

  ASSETS_DIR="$TMPDIR_CK/context-king"
  [ -d "$ASSETS_DIR" ] || die "Archive did not contain expected context-king/ directory"
fi

info ""
info "Installing Context King globally"
info "  Binary : $CK_BIN"
info "  Models : $CK_MODEL_DIR"
info ""

# ── Install binary ─────────────────────────────────────────────────────────────
mkdir -p "$CK_BIN_DIR"

PLATFORM_BIN=""
_os="$(uname -s)"
_arch="$(uname -m)"
case "$_os" in
  Darwin)
    case "$_arch" in
      arm64)  PLATFORM_BIN="$ASSETS_DIR/skills/ck/ck-osx-arm64" ;;
      x86_64) PLATFORM_BIN="$ASSETS_DIR/skills/ck/ck-osx-x64" ;;
    esac ;;
  Linux)
    case "$_arch" in
      x86_64|amd64)    PLATFORM_BIN="$ASSETS_DIR/skills/ck/ck-linux-x64" ;;
      aarch64|arm64)   PLATFORM_BIN="$ASSETS_DIR/skills/ck/ck-linux-arm64" ;;
    esac ;;
esac

if [ -n "$PLATFORM_BIN" ] && [ -f "$PLATFORM_BIN" ]; then
  cp "$PLATFORM_BIN" "$CK_BIN"
else
  # Fall back to the generic wrapper (ck) which may already be the right binary
  cp "$ASSETS_DIR/skills/ck/ck" "$CK_BIN"
fi
chmod +x "$CK_BIN"
ok "Binary installed: $CK_BIN"

# ── Install models ─────────────────────────────────────────────────────────────
mkdir -p "$CK_MODEL_DIR"
if [ -d "$ASSETS_DIR/models/bge-small-en-v1.5" ]; then
  rm -rf "$CK_MODEL_DIR/bge-small-en-v1.5"
  cp -r "$ASSETS_DIR/models/bge-small-en-v1.5" "$CK_MODEL_DIR/"
  ok "Model installed: $CK_MODEL_DIR/bge-small-en-v1.5"
else
  echo "  WARNING: model not found in assets — embedding commands (find-files, get-keyword-map, recall --query) will not work."
  echo "  Set CK_MODEL_DIR env var or run from a local clone that has models/bge-small-en-v1.5/."
fi

# ── Add ~/.ck/bin to PATH ──────────────────────────────────────────────────────
if [ "$MODIFY_PATH" = true ]; then
  PATH_LINE="export PATH=\"\$PATH:$CK_BIN_DIR\""
  for rc in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"; do
    if [ -f "$rc" ] && ! grep -qF "$CK_BIN_DIR" "$rc" 2>/dev/null; then
      printf '\n# Context King\n%s\n' "$PATH_LINE" >> "$rc"
      ok "Added $CK_BIN_DIR to PATH in $rc"
    fi
  done
  export PATH="$PATH:$CK_BIN_DIR"
fi

# ── Verify binary works ────────────────────────────────────────────────────────
INSTALLED_VERSION=""
if INSTALLED_VERSION=$("$CK_BIN" --version 2>/dev/null | sed 's/^ck //'); then
  ok "Binary version: $INSTALLED_VERSION"
else
  echo "  WARNING: binary installed but --version check failed."
fi

echo ""

# ── Claude Code (global ~/.claude/) ───────────────────────────────────────────
if [ "$DO_CLAUDE" = true ]; then
  info "── Claude Code (~/.claude/) ───────────────────────────────────────────────"
  mkdir -p "$CLAUDE_HOME/skills" "$CLAUDE_HOME/hooks" "$CLAUDE_HOME/rules" "$CLAUDE_HOME/models"

  # Purge stale CK assets
  purge_ck_skills "$CLAUDE_HOME/skills"
  purge_ck_hooks  "$CLAUDE_HOME/hooks"
  rm -f "$CLAUDE_HOME/rules/ck-code-search-protocol.md" 2>/dev/null || true

  # Copy models (also make them available at ~/.claude/models/ for hook/skill locality)
  if [ -d "$CK_MODEL_DIR/bge-small-en-v1.5" ]; then
    rm -rf "$CLAUDE_HOME/models/bge-small-en-v1.5"
    cp -r "$CK_MODEL_DIR/bge-small-en-v1.5" "$CLAUDE_HOME/models/"
    ok "Model copied to $CLAUDE_HOME/models/"
  fi

  # Copy skills and rewrite paths → absolute binary path
  cp -r "$ASSETS_DIR/skills/." "$CLAUDE_HOME/skills/"
  chmod +x "$CLAUDE_HOME/skills/ck/ck" 2>/dev/null || true
  rewrite_skills_global "$CLAUDE_HOME/skills" "$CK_BIN"
  sync_embedded_ck_binary "$CLAUDE_HOME"
  ok "Skills installed ($(ls "$CLAUDE_HOME/skills/" | grep -c 'ck' || true) dirs)"

  # Copy hooks
  for f in \
    agent-usage-guard.sh agent-usage-guard.ps1 \
    ck-bash-guard.sh ck-bash-guard.ps1 \
    ck-read-guard.sh ck-read-guard.ps1 \
    ck-search-guard.sh ck-search-guard.ps1 \
    ck-scope-hint.sh ck-scope-hint.ps1 \
    ck-update-check.sh ck-update-check.ps1 \
    ck-postsession.sh ck-postsession.ps1; do
    [ -f "$ASSETS_DIR/hooks/$f" ] && cp "$ASSETS_DIR/hooks/$f" "$CLAUDE_HOME/hooks/$f"
  done
  chmod +x "$CLAUDE_HOME/hooks/"ck-*.sh "$CLAUDE_HOME/hooks/agent-usage-guard.sh" 2>/dev/null || true
  ok "Hooks installed"

  # Copy rule and rewrite binary path placeholder to actual install path
  cp "$ASSETS_DIR/rules/ck-code-search-protocol.md" "$CLAUDE_HOME/rules/"
  sed -i.bak "s|~/.ck/bin/ck|${CK_BIN}|g" "$CLAUDE_HOME/rules/ck-code-search-protocol.md"
  rm -f "$CLAUDE_HOME/rules/ck-code-search-protocol.md.bak"
  ok "Rule installed"

  # Update ~/.claude/settings.json — remove then re-add all CK entries
  SETTINGS="$CLAUDE_HOME/settings.json"
  [ -f "$SETTINGS" ] || echo '{}' > "$SETTINGS"

  if command -v jq >/dev/null 2>&1; then
    # Purge existing CK entries (same patterns as deploy.sh)
    jq '
      .permissions.allowedTools = [(.permissions.allowedTools // [])[] | select(
        test("ck/ck|ck\\.cmd|\"ck ") | not
      )] |
      .hooks.PreToolUse    = [(.hooks.PreToolUse    // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-bash-guard|ck-read-guard|ck-search-guard") | not)] | select((.hooks | length) > 0)] |
      .hooks.SubagentStart = [(.hooks.SubagentStart // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("agent-usage-guard")            | not)] | select((.hooks | length) > 0)] |
      .hooks.PostToolUse   = [(.hooks.PostToolUse   // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-scope-hint")                | not)] | select((.hooks | length) > 0)] |
      .hooks.SessionStart  = [(.hooks.SessionStart  // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-update-check")              | not)] | select((.hooks | length) > 0)] |
      .hooks.Stop          = [(.hooks.Stop          // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-postsession")               | not)] | select((.hooks | length) > 0)]
    ' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    # Add allowedTools for bare `ck` command
    jq '.permissions.allowedTools = ((.permissions.allowedTools // []) + ["Bash(ck *)"])' \
      "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    # Register hooks using absolute paths (global hooks must be absolute)
    HOOK_BASE="$CLAUDE_HOME/hooks"

    jq --arg base "$HOOK_BASE" '.hooks.SubagentStart = ((.hooks.SubagentStart // []) + [{"matcher":"*","hooks":[
      {"type":"command","command":($base + "/agent-usage-guard.sh")}
    ]}])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    jq --arg base "$HOOK_BASE" '.hooks.PreToolUse = ((.hooks.PreToolUse // []) + [
      {"matcher":"Bash","hooks":[
        {"type":"command","command":($base + "/ck-bash-guard.sh")}
      ]},
      {"matcher":"Read","hooks":[
        {"type":"command","command":($base + "/ck-read-guard.sh")}
      ]},
      {"matcher":"Grep","hooks":[
        {"type":"command","command":($base + "/ck-search-guard.sh")}
      ]},
      {"matcher":"Glob","hooks":[
        {"type":"command","command":($base + "/ck-search-guard.sh")}
      ]}
    ])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    jq --arg base "$HOOK_BASE" '.hooks.PostToolUse = ((.hooks.PostToolUse // []) + [{"matcher":"Bash","hooks":[
      {"type":"command","command":($base + "/ck-scope-hint.sh")}
    ]}])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    jq --arg base "$HOOK_BASE" '.hooks.SessionStart = ((.hooks.SessionStart // []) + [{"matcher":"startup","hooks":[
      {"type":"command","command":($base + "/ck-update-check.sh"),"timeout":15}
    ]}])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    jq --arg base "$HOOK_BASE" '.hooks.Stop = ((.hooks.Stop // []) + [{"matcher":"*","hooks":[
      {"type":"command","command":($base + "/ck-postsession.sh")}
    ]}])' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"

    ok "Registered hooks and permissions in $SETTINGS"
  else
    echo "  WARNING: jq not found — hook registration in $SETTINGS skipped."
    echo "  Install jq and re-run, or register hooks manually."
  fi

  echo ""
fi

# ── Codex (global ~/.codex/) ──────────────────────────────────────────────────
if [ "$DO_CODEX" = true ]; then
  info "── Codex (~/.codex/) ───────────────────────────────────────────────────"
  mkdir -p "$CODEX_HOME/skills" "$CODEX_HOME/hooks"

  purge_ck_skills "$CODEX_HOME/skills"
  purge_ck_hooks  "$CODEX_HOME/hooks"

  cp -r "$ASSETS_DIR/skills/." "$CODEX_HOME/skills/"
  chmod +x "$CODEX_HOME/skills/ck/ck" 2>/dev/null || true
  rewrite_skills_global "$CODEX_HOME/skills" "$CK_BIN"
  sync_embedded_ck_binary "$CODEX_HOME"
  ok "Skills installed"

  for f in ck-bash-guard.sh ck-read-guard.sh ck-search-guard.sh ck-scope-hint.sh ck-update-check.sh ck-postsession.sh; do
    [ -f "$ASSETS_DIR/hooks/$f" ] && cp "$ASSETS_DIR/hooks/$f" "$CODEX_HOME/hooks/$f"
  done
  chmod +x "$CODEX_HOME/hooks/"ck-*.sh 2>/dev/null || true
  ok "Hooks installed"

  if [ -f "$ASSETS_DIR/rules/ck-code-search-protocol.md" ]; then
    cp "$ASSETS_DIR/rules/ck-code-search-protocol.md" "$CODEX_HOME/ck-code-search-protocol.md"
    sed -i.bak "s|~/.ck/bin/ck|${CK_BIN}|g" "$CODEX_HOME/ck-code-search-protocol.md"
    rm -f "$CODEX_HOME/ck-code-search-protocol.md.bak"
    ok "Protocol installed"
  fi

  # Merge CK navigation section into AGENTS.md (idempotent via sentinel comment).
  AGENTS_MD="$CODEX_HOME/AGENTS.md"
  [ -f "$AGENTS_MD" ] || touch "$AGENTS_MD"
  if ! grep -qF '## CODE NAVIGATION (Context King)' "$AGENTS_MD" 2>/dev/null; then
    cat >> "$AGENTS_MD" <<AGENTS_SECTION

## CODE NAVIGATION (Context King)

This codebase uses Context King (CK) for source navigation. Follow this protocol for ALL C# and TypeScript/TSX source file search.

**Binary:** \`${CK_BIN}\` (or \`ck\` if in PATH)

### Mandatory workflow

\`\`\`
0. ck get-keyword-map --query "<domain concept operation>"   ← FIRST — keyword grounding
1. ck find-files --query "<domain concept operation>" --task "<task intent>"  ← SECOND — primary file-level retrieval
2. ck recall --folder <confirmed-folder>                     ← before reading any method body
3. ck find-symbol "<name>" --path <folder-or-file>           ← locate declaration
   ck refs "<name>" --path <folder-or-file>                  ← find call-sites
3.5 Fallback only when file-first results are weak/noisy:
    ck find-files --query "<refined terms>" --task "<task intent>"
    ck expand-folder --pattern "<keyword>" <folder>
    ck signatures <folder>/                                  ← when no keyword available
4. ck get-method-source <file> <Member>                      ← read one method (prefer over full file)
   ck get-type-source <file> <TypeName>                      ← read one type/record/interface
   ck get-constructors <file>                                ← constructor(s)
   ck get-usings <file>                                      ← imports only
   ck get-base-types <file>                                  ← inheritance info
   ck read-full-file <file>                                   ← full file when truly needed
5. [edit]
6. ck learn                                                  ← MUST run if CK tools were used
\`\`\`

### Rules
- Step 0 (`get-keyword-map`) is mandatory first; step 1 (`find-files`) is the default file-level retrieval
- Use `find-files` only as fallback when file-first retrieval is weak/noisy
- When any other search tool (CallGraph, etc.) returns empty results, use CK tools next — **never fall back directly to grep**
- grep/rg/glob are only allowed within explicit `--path` boundaries or folders returned by fallback \`ck find-files\`
- **You MUST run \`ck learn\` before ending the session if CK tools were used.** Record architectural insights, cross-module constraints, and non-obvious WHYs — not implementation details findable via signatures.

Full protocol reference: \`${CODEX_HOME}/ck-code-search-protocol.md\`
AGENTS_SECTION
    ok "CK navigation section added to $AGENTS_MD"
  else
    ok "AGENTS.md already contains CK navigation section (skipped)"
  fi

  # Add project_doc_fallback_filenames to config.toml so per-repo ck-code-search-protocol.md
  # files are auto-loaded alongside AGENTS.md traversal.
  CODEX_CONFIG="$CODEX_HOME/config.toml"
  if [ -f "$CODEX_CONFIG" ] && ! grep -qF 'project_doc_fallback_filenames' "$CODEX_CONFIG" 2>/dev/null; then
    printf '\nproject_doc_fallback_filenames = ["ck-code-search-protocol.md"]\n' >> "$CODEX_CONFIG"
    ok "Added project_doc_fallback_filenames to $CODEX_CONFIG"
  elif [ ! -f "$CODEX_CONFIG" ]; then
    printf 'project_doc_fallback_filenames = ["ck-code-search-protocol.md"]\n' > "$CODEX_CONFIG"
    ok "Created $CODEX_CONFIG with project_doc_fallback_filenames"
  else
    ok "config.toml already has project_doc_fallback_filenames (skipped)"
  fi

  HOOKS_JSON="$CODEX_HOME/hooks.json"
  [ -f "$HOOKS_JSON" ] || echo '{}' > "$HOOKS_JSON"

  if command -v jq >/dev/null 2>&1; then
    # Remove stale CK hook registrations first, then add canonical registrations.
    jq '
      .hooks = (.hooks // {}) |
      .hooks.PreToolUse = [(.hooks.PreToolUse // [])[] |
        .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-bash-guard|ck-read-guard|ck-search-guard") | not)] |
        select((.hooks | length) > 0)
      ] |
      .hooks.PostToolUse = [(.hooks.PostToolUse // [])[] |
        .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-scope-hint") | not)] |
        select((.hooks | length) > 0)
      ] |
      .hooks.SessionStart = [(.hooks.SessionStart // [])[] |
        .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-update-check") | not)] |
        select((.hooks | length) > 0)
      ] |
      .hooks.Stop = [(.hooks.Stop // [])[] |
        .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-postsession") | not)] |
        select((.hooks | length) > 0)
      ]
    ' "$HOOKS_JSON" > "$HOOKS_JSON.tmp" && mv "$HOOKS_JSON.tmp" "$HOOKS_JSON"

    jq --arg base "$CODEX_HOME/hooks" '
      .hooks = (.hooks // {}) |
      .hooks.PreToolUse = ((.hooks.PreToolUse // []) + [
        {"matcher":"Bash","hooks":[{"type":"command","command":($base + "/ck-bash-guard.sh")}]},
        {"matcher":"Read","hooks":[{"type":"command","command":($base + "/ck-read-guard.sh")}]},
        {"matcher":"Grep","hooks":[{"type":"command","command":($base + "/ck-search-guard.sh")}]},
        {"matcher":"Glob","hooks":[{"type":"command","command":($base + "/ck-search-guard.sh")}]}
      ]) |
      .hooks.PostToolUse = ((.hooks.PostToolUse // []) + [
        {"matcher":"Bash","hooks":[{"type":"command","command":($base + "/ck-scope-hint.sh")}]}
      ]) |
      .hooks.SessionStart = ((.hooks.SessionStart // []) + [
        {"matcher":"startup|resume|clear","hooks":[{"type":"command","command":($base + "/ck-update-check.sh"),"timeout":15}]}
      ]) |
      .hooks.Stop = ((.hooks.Stop // []) + [
        {"hooks":[{"type":"command","command":($base + "/ck-postsession.sh")}]}
      ])
    ' "$HOOKS_JSON" > "$HOOKS_JSON.tmp" && mv "$HOOKS_JSON.tmp" "$HOOKS_JSON"
    ok "Registered Codex hooks in $HOOKS_JSON"
  else
    echo "  WARNING: jq not found — Codex hook registration in $HOOKS_JSON skipped."
    echo '  Add this manually: PreToolUse(Bash/Read/Grep/Glob -> CK guards), PostToolUse(Bash -> ck-scope-hint), SessionStart(startup|resume|clear -> ck-update-check), Stop(* -> ck-postsession).'
  fi

  echo ""
fi

# ── OpenCode (global ~/.config/opencode/) ─────────────────────────────────────
if [ "$DO_OPENCODE" = true ]; then
  info "── OpenCode (~/.config/opencode/) ────────────────────────────────────────"
  mkdir -p "$OPENCODE_HOME/skills" "$OPENCODE_HOME/plugins" "$OPENCODE_HOME/agents"

  purge_ck_skills "$OPENCODE_HOME/skills"
  rm -f "$OPENCODE_HOME/plugins/ck-guards.ts" 2>/dev/null || true
  rm -f "$OPENCODE_HOME/agents/explore.md" 2>/dev/null || true

  # Copy skills and rewrite to absolute binary path
  cp -r "$ASSETS_DIR/skills/." "$OPENCODE_HOME/skills/"
  chmod +x "$OPENCODE_HOME/skills/ck/ck" 2>/dev/null || true
  rewrite_skills_global "$OPENCODE_HOME/skills" "$CK_BIN"
  sync_embedded_ck_binary "$OPENCODE_HOME"
  ok "Skills installed"

  # Copy plugin
  if [ -f "$ASSETS_DIR/plugins/ck-guards.ts" ]; then
    cp "$ASSETS_DIR/plugins/ck-guards.ts" "$OPENCODE_HOME/plugins/"
    mkdir -p "$LEGACY_OPENCODE_HOME/plugin"
    cp "$ASSETS_DIR/plugins/ck-guards.ts" "$LEGACY_OPENCODE_HOME/plugin/ck-guards.ts"
    ok "Plugin installed"
  fi

  # Copy explore agent (enforces CK protocol; denies native read/grep/glob)
  if [ -f "$ASSETS_DIR/agents/explore.md" ]; then
    cp "$ASSETS_DIR/agents/explore.md" "$OPENCODE_HOME/agents/"
    ok "Explore agent installed"
  fi

  # Copy CK protocol reference used by OpenCode instructions[].
  if [ -f "$ASSETS_DIR/rules/ck-code-search-protocol.md" ]; then
    cp "$ASSETS_DIR/rules/ck-code-search-protocol.md" "$OPENCODE_HOME/ck-code-search-protocol.md"
    sed -i.bak "s|~/.ck/bin/ck|${CK_BIN}|g" "$OPENCODE_HOME/ck-code-search-protocol.md"
    rm -f "$OPENCODE_HOME/ck-code-search-protocol.md.bak"
    ok "Protocol installed"
  fi

  # Merge into OpenCode config. Prefer opencode.jsonc when present.
  OPENCODE_CFG_JSONC="$OPENCODE_HOME/opencode.jsonc"
  OPENCODE_CFG_JSON="$OPENCODE_HOME/opencode.json"
  if [ -f "$OPENCODE_CFG_JSONC" ]; then
    OPENCODE_CFG="$OPENCODE_CFG_JSONC"
  elif [ -f "$OPENCODE_CFG_JSON" ]; then
    OPENCODE_CFG="$OPENCODE_CFG_JSON"
  else
    # Default to jsonc for new installs. If jsonc already exists, never create json.
    OPENCODE_CFG="$OPENCODE_CFG_JSONC"
    echo '{}' > "$OPENCODE_CFG"
  fi
  if [[ "$OPENCODE_CFG" == *.jsonc ]]; then
    # JSONC path: update in-place so comments are preserved.
    python3 - "$OPENCODE_CFG" <<'PY'
import re
import sys

path = sys.argv[1]
text = open(path, "r", encoding="utf-8").read()

def strip_jsonc(s: str) -> str:
    out = []
    i = 0
    n = len(s)
    in_string = False
    escaped = False
    while i < n:
        ch = s[i]
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
        if ch == "/" and i + 1 < n:
            nxt = s[i + 1]
            if nxt == "/":
                i += 2
                while i < n and s[i] not in "\r\n":
                    i += 1
                continue
            if nxt == "*":
                i += 2
                while i + 1 < n and not (s[i] == "*" and s[i + 1] == "/"):
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
    return clean

def ensure_array_entry(src: str, key: str, value: str) -> str:
    # Match the first `"key": [ ... ]` block.
    pattern = rf'("{re.escape(key)}"\s*:\s*\[)(.*?)(\])'
    m = re.search(pattern, src, flags=re.S)
    if not m:
        # Add missing top-level array property before final `}`.
        tail = re.search(r"}\s*$", src, flags=re.S)
        if not tail:
            return src
        prefix = src[:tail.start()]
        suffix = src[tail.start():]
        has_members = bool(strip_jsonc(prefix[prefix.find("{")+1:]).strip())
        comma = "," if has_members else ""
        block = f'{comma}\n  "{key}": [\n    "{value}"\n  ]\n'
        return prefix + block + suffix

    head, body, close = m.group(1), m.group(2), m.group(3)
    if re.search(rf'"{re.escape(value)}"', body):
        return src

    body_rstrip = body.rstrip()
    indent_match = re.search(r"\n([ \t]*)[^ \t\r\n]", body)
    item_indent = indent_match.group(1) if indent_match else "    "

    if strip_jsonc(body).strip() == "":
        new_body = f"\n{item_indent}\"{value}\"\n  "
    else:
        sep = "," if not body_rstrip.endswith(",") else ""
        new_body = body + f"{sep}\n{item_indent}\"{value}\""

    return src[:m.start()] + head + new_body + close + src[m.end():]

updated = ensure_array_entry(text, "plugin", "./plugins/ck-guards.ts")
updated = ensure_array_entry(updated, "instructions", "./ck-code-search-protocol.md")
if updated != text:
    open(path, "w", encoding="utf-8").write(updated)
PY
    ok "Registered OpenCode plugin and instructions in $OPENCODE_CFG"
  elif command -v jq >/dev/null 2>&1; then
    # Plain JSON path.
    jq '
      if (.tools | type) == "object" and (.tools.bash | type) == "object"
      then .tools.bash = true
      else .
      end |
      .plugin = ((.plugin // []) + ["./plugins/ck-guards.ts"] | unique) |
      .instructions = ((.instructions // []) + ["./ck-code-search-protocol.md"] | unique)
    ' "$OPENCODE_CFG" > "$OPENCODE_CFG.tmp" && mv "$OPENCODE_CFG.tmp" "$OPENCODE_CFG"
    ok "Registered OpenCode plugin and instructions in $OPENCODE_CFG"
  else
    echo "  WARNING: jq not found — OpenCode config merge in $OPENCODE_CFG skipped."
    echo "  Add the following manually:"
    echo '    "plugin": ["./plugins/ck-guards.ts"]'
    echo '    "instructions": ["./ck-code-search-protocol.md"]'
  fi

  echo ""
fi

# ── Generic Agent Skills (~/.agents/skills/) ──────────────────────────────────
if [ "$DO_AGENTS" = true ]; then
  info "── Agent Skills (~/.agents/skills/) ─────────────────────────────────────"
  mkdir -p "$AGENTS_HOME/skills"

  purge_ck_skills "$AGENTS_HOME/skills"

  cp -r "$ASSETS_DIR/skills/." "$AGENTS_HOME/skills/"
  chmod +x "$AGENTS_HOME/skills/ck/ck" 2>/dev/null || true
  rewrite_skills_global "$AGENTS_HOME/skills" "$CK_BIN"
  sync_embedded_ck_binary "$AGENTS_HOME"
  ok "Skills installed"

  echo ""
fi

# ── Summary ────────────────────────────────────────────────────────────────────
info "Context King installed globally."
info ""
info "Next steps:"
info "  1. Start a new shell (or run: export PATH=\"\$PATH:$CK_BIN_DIR\")"
info "  2. In each repository: ck init"
info "     This creates .ck.json (version requirement) and .ck-knowledge/ directory."
info ""
info "  ck --version   →  confirm the installed version"
info "  ck --help      →  show all commands"
