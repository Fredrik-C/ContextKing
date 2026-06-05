#!/usr/bin/env bash
# uninstall-global.sh — Context King global user uninstaller.
#
# This script targets macOS/Linux shell environments. For Windows, use
# scripts/uninstall-global.ps1 (PowerShell).
#
# Reverses scripts/install-global.sh: removes the ck binary, embedding model,
# skills, hooks, rules/protocol files, and the CK entries it registered in each
# client's config. Per-repo files created by `ck init` (.ck.json, .ck-knowledge/,
# .ck-index/) are NOT touched — they may be git-tracked; remove them manually.
#
# ── One-liner uninstall ─────────────────────────────────────────────────────────
#   curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/uninstall-global.sh | bash
#
# ── Download and run (to pass flags) ────────────────────────────────────────────
#   curl -fsSL .../uninstall-global.sh -o uninstall-global.sh && bash uninstall-global.sh --dry-run
#
# ── Run from a cloned repo ──────────────────────────────────────────────────────
#   bash scripts/uninstall-global.sh
#
# ── Preview without changing anything ───────────────────────────────────────────
#   bash scripts/uninstall-global.sh --dry-run
#
# ── Options ───────────────────────────────────────────────────────────────────
#   --ck-home <path>    Override installation root (default: ~/.ck)
#   --claude-home <p>   Override Claude Code config dir (default: ~/.claude)
#   --codex-home <p>    Override Codex config dir (default: ~/.codex)
#   --opencode-home <p> Override OpenCode config dir (default: ~/.config/opencode)
#   --agents-home <p>   Override Agents skills dir (default: ~/.agents)
#   --dry-run           Print every action without performing it
#   --no-path           Skip PATH cleanup in shell config files
#   --no-claude         Skip Claude Code cleanup
#   --no-codex          Skip Codex cleanup
#   --no-opencode       Skip OpenCode cleanup
#   --no-agents         Skip Agents cleanup
#
# What this script removes:
#   ~/.ck/                               binary + embedding model (whole tree)
#   ~/.claude/skills/ck*/                Claude Code skills
#   ~/.claude/hooks/ck-*.sh|.ps1         Claude Code hooks
#   ~/.claude/rules/ck-code-search-protocol.md
#   ~/.claude/models/bge-small-en-v1.5/  copied embedding model
#   ~/.claude/settings.json              CK hook + permission entries (entries only)
#   ~/.codex/skills/ck*/                 Codex skills
#   ~/.codex/hooks/ck-*.sh               Codex hooks
#   ~/.codex/hooks.json                  CK hook entries (entries only)
#   ~/.codex/ck-code-search-protocol.md  Codex protocol reference
#   ~/.codex/AGENTS.md                   CK navigation section (section only)
#   ~/.codex/config.toml                 project_doc_fallback_filenames line
#   ~/.config/opencode/skills/ck*/       OpenCode skills
#   ~/.config/opencode/plugins/ck-guards.ts
#   ~/.opencode/plugin/ck-guards.ts      Legacy OpenCode plugin path
#   ~/.config/opencode/agents/explore.md
#   ~/.config/opencode/ck-code-search-protocol.md
#   ~/.config/opencode/opencode.json(c)  CK plugin + instructions entries (entries only)
#   ~/.agents/skills/ck*/                Generic Agent Skills

set -euo pipefail

# ── Defaults ──────────────────────────────────────────────────────────────────
CK_HOME="${CK_HOME:-$HOME/.ck}"
CLAUDE_HOME="${CLAUDE_HOME:-$HOME/.claude}"
CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
OPENCODE_HOME="${OPENCODE_HOME:-$HOME/.config/opencode}"
LEGACY_OPENCODE_HOME="${LEGACY_OPENCODE_HOME:-$HOME/.opencode}"
AGENTS_HOME="${AGENTS_HOME:-$HOME/.agents}"
DRY_RUN=false
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
    --dry-run)      DRY_RUN=true;      shift ;;
    --no-path)      MODIFY_PATH=false; shift ;;
    --no-claude)    DO_CLAUDE=false;   shift ;;
    --no-codex)     DO_CODEX=false;    shift ;;
    --no-opencode)  DO_OPENCODE=false; shift ;;
    --no-agents)    DO_AGENTS=false;   shift ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//' | head -40
      exit 0 ;;
    *) echo "Unknown flag: $1" >&2; exit 1 ;;
  esac
done

CK_BIN_DIR="$CK_HOME/bin"

# ── Helpers ────────────────────────────────────────────────────────────────────
info() { echo "$*"; }
ok()   { echo "  ✓ $*"; }

# Remove a single path (file or directory). No-op if absent.
rm_path() {
  local target="$1"
  [ -e "$target" ] || return 0
  if [ "$DRY_RUN" = true ]; then
    echo "  [dry-run] rm -rf $target"
  else
    rm -rf "$target"
    ok "Removed $target"
  fi
}

purge_ck_skills() {
  local root="$1"
  [ -d "$root" ] || return 0
  if [ "$DRY_RUN" = true ]; then
    [ -d "$root/ck" ] && echo "  [dry-run] rm -rf $root/ck"
    for d in "$root"/ck-*; do [ -d "$d" ] && echo "  [dry-run] rm -rf $d"; done
    return 0
  fi
  rm -rf "$root/ck" 2>/dev/null || true
  for d in "$root"/ck-*; do [ -d "$d" ] && rm -rf "$d"; done 2>/dev/null || true
}

purge_ck_hooks() {
  local root="$1"
  [ -d "$root" ] || return 0
  if [ "$DRY_RUN" = true ]; then
    ls "$root"/ck-*.sh "$root"/ck-*.ps1 "$root"/agent-usage-guard.sh "$root"/agent-usage-guard.ps1 2>/dev/null \
      | while IFS= read -r f; do echo "  [dry-run] rm -f $f"; done
    return 0
  fi
  rm -f "$root"/ck-*.sh "$root"/ck-*.ps1 "$root"/agent-usage-guard.sh "$root"/agent-usage-guard.ps1 2>/dev/null || true
}

info "Uninstalling Context King (global)"
if [ "$DRY_RUN" = true ]; then info "  (dry run — no changes will be made)"; fi
info ""

# ── Remove binary + models (whole ~/.ck tree) ────────────────────────────────
info "── Binary & models (~/.ck) ───────────────────────────────────────────────"
rm_path "$CK_HOME"
info ""

# ── Remove ~/.ck/bin from PATH in shell config files ──────────────────────────
if [ "$MODIFY_PATH" = true ]; then
  info "── PATH cleanup ──────────────────────────────────────────────────────────"
  for rc in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"; do
    if [ -f "$rc" ] && grep -qF "$CK_BIN_DIR" "$rc" 2>/dev/null; then
      if [ "$DRY_RUN" = true ]; then
        echo "  [dry-run] strip Context King PATH lines from $rc"
      else
        tmp="$(mktemp)"
        # Drop the "# Context King" marker line and any line referencing CK_BIN_DIR.
        grep -vF "$CK_BIN_DIR" "$rc" | grep -vxF "# Context King" > "$tmp" || true
        mv "$tmp" "$rc"
        ok "Removed $CK_BIN_DIR from PATH in $rc"
      fi
    fi
  done
  info ""
fi

# ── Claude Code (~/.claude/) ──────────────────────────────────────────────────
if [ "$DO_CLAUDE" = true ]; then
  info "── Claude Code (~/.claude/) ──────────────────────────────────────────────"
  purge_ck_skills "$CLAUDE_HOME/skills"
  purge_ck_hooks  "$CLAUDE_HOME/hooks"
  rm_path "$CLAUDE_HOME/rules/ck-code-search-protocol.md"
  rm_path "$CLAUDE_HOME/models/bge-small-en-v1.5"
  [ "$DRY_RUN" = true ] || ok "Skills and hooks removed"

  SETTINGS="$CLAUDE_HOME/settings.json"
  if [ -f "$SETTINGS" ]; then
    if command -v jq >/dev/null 2>&1; then
      if [ "$DRY_RUN" = true ]; then
        echo "  [dry-run] remove CK hook + permission entries from $SETTINGS"
      else
        jq '
          .permissions.allowedTools = [(.permissions.allowedTools // [])[] | select(
            test("ck/ck|ck\\.cmd|\"ck |Bash\\(ck ") | not
          )] |
          .hooks.PreToolUse    = [(.hooks.PreToolUse    // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-bash-guard|ck-read-guard|ck-search-guard") | not)] | select((.hooks | length) > 0)] |
          .hooks.SubagentStart = [(.hooks.SubagentStart // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("agent-usage-guard")            | not)] | select((.hooks | length) > 0)] |
          .hooks.PostToolUse   = [(.hooks.PostToolUse   // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-scope-hint")                | not)] | select((.hooks | length) > 0)] |
          .hooks.SessionStart  = [(.hooks.SessionStart  // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-update-check")              | not)] | select((.hooks | length) > 0)] |
          .hooks.Stop          = [(.hooks.Stop          // [])[] | .hooks = [(.hooks // [])[]? | select((.command // "") | test("ck-postsession")               | not)] | select((.hooks | length) > 0)]
        ' "$SETTINGS" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
        ok "Removed CK hook and permission entries from $SETTINGS"
      fi
    else
      echo "  WARNING: jq not found — CK entries in $SETTINGS not removed."
      echo "  Manually delete the Bash(ck *) permission and the ck-*/agent-usage-guard hook entries."
    fi
  fi
  info ""
fi

# ── Codex (~/.codex/) ─────────────────────────────────────────────────────────
if [ "$DO_CODEX" = true ]; then
  info "── Codex (~/.codex/) ─────────────────────────────────────────────────────"
  purge_ck_skills "$CODEX_HOME/skills"
  purge_ck_hooks  "$CODEX_HOME/hooks"
  rm_path "$CODEX_HOME/ck-code-search-protocol.md"
  [ "$DRY_RUN" = true ] || ok "Skills, hooks and protocol removed"

  # Remove the CK navigation section appended to AGENTS.md (sentinel → EOF),
  # then trim any trailing blank lines left behind.
  AGENTS_MD="$CODEX_HOME/AGENTS.md"
  if [ -f "$AGENTS_MD" ] && grep -qF '## CODE NAVIGATION (Context King)' "$AGENTS_MD" 2>/dev/null; then
    if [ "$DRY_RUN" = true ]; then
      echo "  [dry-run] remove CK navigation section from $AGENTS_MD"
    else
      # Delete from the sentinel to EOF; command substitution strips the
      # trailing blank lines the section left behind (portable, no BSD-sed quirks).
      kept="$(sed '/## CODE NAVIGATION (Context King)/,$d' "$AGENTS_MD")"
      if [ -n "$kept" ]; then
        printf '%s\n' "$kept" > "$AGENTS_MD"
      else
        : > "$AGENTS_MD"
      fi
      ok "Removed CK navigation section from $AGENTS_MD"
    fi
  fi

  # Remove the project_doc_fallback_filenames line from config.toml. Match the
  # exact literal the installer writes (symmetric with the install side) so we
  # never touch a user-owned value the installer deliberately skipped, and never
  # half-delete a multi-line TOML array.
  CODEX_CONFIG="$CODEX_HOME/config.toml"
  CODEX_FALLBACK_LINE='project_doc_fallback_filenames = ["ck-code-search-protocol.md"]'
  if [ -f "$CODEX_CONFIG" ] && grep -qxF "$CODEX_FALLBACK_LINE" "$CODEX_CONFIG" 2>/dev/null; then
    if [ "$DRY_RUN" = true ]; then
      echo "  [dry-run] remove project_doc_fallback_filenames from $CODEX_CONFIG"
    else
      tmp="$(mktemp)"
      grep -vxF "$CODEX_FALLBACK_LINE" "$CODEX_CONFIG" > "$tmp" || true
      mv "$tmp" "$CODEX_CONFIG"
      ok "Removed project_doc_fallback_filenames from $CODEX_CONFIG"
    fi
  fi

  HOOKS_JSON="$CODEX_HOME/hooks.json"
  if [ -f "$HOOKS_JSON" ]; then
    if command -v jq >/dev/null 2>&1; then
      if [ "$DRY_RUN" = true ]; then
        echo "  [dry-run] remove CK hook entries from $HOOKS_JSON"
      else
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
        ok "Removed CK hook entries from $HOOKS_JSON"
      fi
    else
      echo "  WARNING: jq not found — CK entries in $HOOKS_JSON not removed."
      echo "  Manually delete the ck-* hook entries (PreToolUse/PostToolUse/SessionStart/Stop)."
    fi
  fi
  info ""
fi

# ── OpenCode (~/.config/opencode/) ────────────────────────────────────────────
if [ "$DO_OPENCODE" = true ]; then
  info "── OpenCode (~/.config/opencode/) ────────────────────────────────────────"
  purge_ck_skills "$OPENCODE_HOME/skills"
  rm_path "$OPENCODE_HOME/plugins/ck-guards.ts"
  rm_path "$LEGACY_OPENCODE_HOME/plugin/ck-guards.ts"
  rm_path "$OPENCODE_HOME/agents/explore.md"
  rm_path "$OPENCODE_HOME/ck-code-search-protocol.md"
  [ "$DRY_RUN" = true ] || ok "Skills, plugin, agent and protocol removed"

  # Determine which OpenCode config file the installer wrote to.
  OPENCODE_CFG=""
  if [ -f "$OPENCODE_HOME/opencode.jsonc" ]; then
    OPENCODE_CFG="$OPENCODE_HOME/opencode.jsonc"
  elif [ -f "$OPENCODE_HOME/opencode.json" ]; then
    OPENCODE_CFG="$OPENCODE_HOME/opencode.json"
  fi

  if [ -n "$OPENCODE_CFG" ]; then
    if [ "$DRY_RUN" = true ]; then
      echo "  [dry-run] remove CK plugin + instructions entries from $OPENCODE_CFG"
    elif [[ "$OPENCODE_CFG" == *.jsonc ]]; then
      # JSONC path: edit in-place so comments are preserved.
      python3 - "$OPENCODE_CFG" <<'PY'
import re
import sys

path = sys.argv[1]
text = open(path, "r", encoding="utf-8").read()

def remove_array_entry(src: str, key: str, value: str) -> str:
    # Match the first `"key": [ ... ]` block.
    pattern = rf'("{re.escape(key)}"\s*:\s*\[)(.*?)(\])'
    m = re.search(pattern, src, flags=re.S)
    if not m:
        return src
    head, body, close = m.group(1), m.group(2), m.group(3)
    # Drop the element `"value"` plus a trailing or leading comma.
    quoted = re.escape(value)
    new_body = re.sub(rf'\s*"{quoted}"\s*,?', '', body, count=1)
    # If that left a dangling leading comma (we removed a middle/last item),
    # clean up `, ]` and `[ ,` artifacts.
    new_body = re.sub(r',(\s*)$', r'\1', new_body)
    new_body = re.sub(r'^(\s*),', r'\1', new_body)
    return src[:m.start()] + head + new_body + close + src[m.end():]

updated = remove_array_entry(text, "plugin", "./plugins/ck-guards.ts")
updated = remove_array_entry(updated, "instructions", "./ck-code-search-protocol.md")
if updated != text:
    open(path, "w", encoding="utf-8").write(updated)
PY
      ok "Removed CK plugin and instructions entries from $OPENCODE_CFG"
    elif command -v jq >/dev/null 2>&1; then
      jq '
        .plugin = [(.plugin // [])[] | select(. != "./plugins/ck-guards.ts")] |
        .instructions = [(.instructions // [])[] | select(. != "./ck-code-search-protocol.md")]
      ' "$OPENCODE_CFG" > "$OPENCODE_CFG.tmp" && mv "$OPENCODE_CFG.tmp" "$OPENCODE_CFG"
      ok "Removed CK plugin and instructions entries from $OPENCODE_CFG"
    else
      echo "  WARNING: neither python3 nor jq found — CK entries in $OPENCODE_CFG not removed."
      echo '  Manually delete "./plugins/ck-guards.ts" from "plugin" and "./ck-code-search-protocol.md" from "instructions".'
    fi
  fi
  info ""
fi

# ── Generic Agent Skills (~/.agents/skills/) ──────────────────────────────────
if [ "$DO_AGENTS" = true ]; then
  info "── Agent Skills (~/.agents/skills/) ──────────────────────────────────────"
  purge_ck_skills "$AGENTS_HOME/skills"
  [ "$DRY_RUN" = true ] || ok "Skills removed"
  info ""
fi

# ── Summary ────────────────────────────────────────────────────────────────────
if [ "$DRY_RUN" = true ]; then
  info "Dry run complete — no changes were made."
else
  info "Context King uninstalled globally."
fi
info ""
info "Per-repo files created by 'ck init' are NOT removed (they may be git-tracked)."
info "To remove them from a repository:"
info "  rm -rf .ck-index .ck-knowledge .ck.json"
info "Also remove .ck-index/ from that repo's .gitignore if you added it."
