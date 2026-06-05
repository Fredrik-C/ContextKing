# uninstall-global.ps1 — Context King global user uninstaller (Windows/PowerShell).
#
# Reverses scripts/install-global.ps1: removes the ck binary, embedding model,
# skills, hooks, rules/protocol files, and the CK entries it registered in each
# client's config. Per-repo files created by `ck init` (.ck.json, .ck-knowledge\,
# .ck-index\) are NOT touched — they may be git-tracked; remove them manually.
#
# Usage:
#   iex (iwr 'https://github.com/Fredrik-C/ContextKing/releases/latest/download/uninstall-global.ps1').Content
#   .\scripts\uninstall-global.ps1
#   .\scripts\uninstall-global.ps1 -DryRun
#   .\scripts\uninstall-global.ps1 -CkHome "$env:USERPROFILE\.ck" -NoClaude
#
# Parameters:
#   -CkHome <path>       Installation root (default: $env:USERPROFILE\.ck)
#   -ClaudeHome <path>   Claude Code config dir (default: $env:USERPROFILE\.claude)
#   -CodexHome <path>    Codex config dir (default: $env:USERPROFILE\.codex)
#   -OpenCodeHome <path> OpenCode config dir (default: $env:APPDATA\opencode)
#   -AgentsHome <path>   Agents skills dir (default: $env:USERPROFILE\.agents)
#   -DryRun              Print every action without performing it
#   -NoPath              Skip PATH cleanup
#   -NoClaude            Skip Claude Code cleanup
#   -NoCodex             Skip Codex cleanup
#   -NoOpenCode          Skip OpenCode cleanup
#   -NoAgents            Skip Agents cleanup

param(
  [string]$CkHome      = "$env:USERPROFILE\.ck",
  [string]$ClaudeHome  = "$env:USERPROFILE\.claude",
  [string]$CodexHome   = "$env:USERPROFILE\.codex",
  [string]$OpenCodeHome = "$env:APPDATA\opencode",
  [string]$AgentsHome  = "$env:USERPROFILE\.agents",
  [switch]$DryRun,
  [switch]$NoPath,
  [switch]$NoClaude,
  [switch]$NoCodex,
  [switch]$NoOpenCode,
  [switch]$NoAgents
)

$ErrorActionPreference = "Stop"

$CkBinDir = "$CkHome\bin"
$LegacyOpenCodeHome = "$env:USERPROFILE\.opencode"

function Write-Ok($msg)   { Write-Host "  OK $msg" }
function Write-Info($msg) { Write-Host $msg }

# Remove a single path (file or directory). No-op if absent.
function Remove-Path($p) {
  if (-not (Test-Path $p)) { return }
  if ($DryRun) {
    Write-Host "  [dry-run] remove $p"
  } else {
    Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "Removed $p"
  }
}

function Remove-CkSkills($SkillsRoot) {
  if (-not (Test-Path $SkillsRoot)) { return }
  if ($DryRun) {
    if (Test-Path "$SkillsRoot\ck") { Write-Host "  [dry-run] remove $SkillsRoot\ck" }
    Get-ChildItem $SkillsRoot -Directory -Filter "ck-*" -ErrorAction SilentlyContinue |
      ForEach-Object { Write-Host "  [dry-run] remove $($_.FullName)" }
    return
  }
  Remove-Item "$SkillsRoot\ck" -Recurse -Force -ErrorAction SilentlyContinue
  Get-ChildItem $SkillsRoot -Directory -Filter "ck-*" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Remove-CkHooks($HooksRoot) {
  if (-not (Test-Path $HooksRoot)) { return }
  $patterns = @("ck-*.ps1", "ck-*.sh", "agent-usage-guard.ps1", "agent-usage-guard.sh")
  foreach ($pat in $patterns) {
    Get-ChildItem $HooksRoot -File -Filter $pat -ErrorAction SilentlyContinue | ForEach-Object {
      if ($DryRun) { Write-Host "  [dry-run] remove $($_.FullName)" }
      else { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }
  }
}

Write-Info "Uninstalling Context King (global)"
if ($DryRun) { Write-Info "  (dry run — no changes will be made)" }
Write-Info ""

# ── Remove binary + models (whole .ck tree) ──────────────────────────────────
Write-Info "── Binary & models ($CkHome\) ────────────────────────────────────────"
Remove-Path $CkHome
Write-Info ""

# ── Remove .ck\bin from user PATH ────────────────────────────────────────────
if (-not $NoPath) {
  Write-Info "── PATH cleanup ──────────────────────────────────────────────────────"
  $UserPath = [Environment]::GetEnvironmentVariable("PATH", "User")
  if ($UserPath -and ($UserPath -like "*$CkBinDir*")) {
    $newParts = @($UserPath -split ';' | Where-Object { $_ -and ($_ -ne $CkBinDir) })
    $newPath = $newParts -join ';'
    if ($DryRun) {
      Write-Host "  [dry-run] remove $CkBinDir from user PATH"
    } else {
      [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
      Write-Ok "Removed $CkBinDir from user PATH"
    }
  }
  Write-Info ""
}

# ── Claude Code ────────────────────────────────────────────────────────────────
if (-not $NoClaude) {
  Write-Info "── Claude Code ($ClaudeHome\) ──────────────────────────────────────────"
  Remove-CkSkills "$ClaudeHome\skills"
  Remove-CkHooks  "$ClaudeHome\hooks"
  Remove-Path "$ClaudeHome\rules\ck-code-search-protocol.md"
  Remove-Path "$ClaudeHome\models\bge-small-en-v1.5"
  if (-not $DryRun) { Write-Ok "Skills and hooks removed" }

  $Settings = "$ClaudeHome\settings.json"
  if (Test-Path $Settings) {
    if ($DryRun) {
      Write-Host "  [dry-run] remove CK hook + permission entries from $Settings"
    } else {
      try {
        $cfg = Get-Content -LiteralPath $Settings -Raw | ConvertFrom-Json -AsHashtable
        if (-not $cfg) { $cfg = @{} }

        if ($cfg.ContainsKey('permissions') -and ($cfg['permissions'] -is [hashtable])) {
          $permissions = $cfg['permissions']
          if ($permissions.ContainsKey('allowedTools')) {
            $permissions['allowedTools'] = @($permissions['allowedTools'] | Where-Object {
              $_ -and ($_ -notmatch 'ck/ck|ck\.cmd|"ck |Bash\(ck ')
            })
          }
          $cfg['permissions'] = $permissions
        }

        if ($cfg.ContainsKey('hooks') -and ($cfg['hooks'] -is [hashtable])) {
          $hooks = $cfg['hooks']
          $eventPatterns = @{
            PreToolUse    = 'ck-bash-guard|ck-read-guard|ck-search-guard'
            SubagentStart = 'agent-usage-guard'
            PostToolUse   = 'ck-scope-hint'
            SessionStart  = 'ck-update-check'
            Stop          = 'ck-postsession'
          }
          foreach ($event in @($hooks.Keys)) {
            if (-not $eventPatterns.ContainsKey($event)) { continue }
            $pat = $eventPatterns[$event]
            $cleaned = @()
            foreach ($entry in @($hooks[$event])) {
              $entryHooks = @($entry['hooks'] | Where-Object { -not ([string]($_['command']) -match $pat) })
              if ($entryHooks.Count -gt 0) {
                $entry['hooks'] = $entryHooks
                $cleaned += $entry
              }
            }
            $hooks[$event] = $cleaned
          }
          $cfg['hooks'] = $hooks
        }

        $cfg | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Settings -Encoding UTF8
        Write-Ok "Removed CK hook and permission entries from $Settings"
      } catch {
        Write-Warning "Could not update $Settings automatically. Remove the Bash(ck *) permission and ck-* hook entries manually."
      }
    }
  }
  Write-Info ""
}

# ── Codex ──────────────────────────────────────────────────────────────────────
if (-not $NoCodex) {
  Write-Info "── Codex ($CodexHome\) ─────────────────────────────────────────────────"
  Remove-CkSkills "$CodexHome\skills"
  Remove-CkHooks  "$CodexHome\hooks"
  Remove-Path "$CodexHome\ck-code-search-protocol.md"
  if (-not $DryRun) { Write-Ok "Skills, hooks and protocol removed" }

  # Remove the CK navigation section appended to AGENTS.md (sentinel → EOF).
  $CodexAgentsMd = "$CodexHome\AGENTS.md"
  if (Test-Path $CodexAgentsMd) {
    $agentsContent = Get-Content $CodexAgentsMd -Raw -ErrorAction SilentlyContinue
    if ($agentsContent -match [regex]::Escape('## CODE NAVIGATION (Context King)')) {
      if ($DryRun) {
        Write-Host "  [dry-run] remove CK navigation section from $CodexAgentsMd"
      } else {
        $idx = $agentsContent.IndexOf('## CODE NAVIGATION (Context King)')
        $kept = $agentsContent.Substring(0, $idx).TrimEnd("`r", "`n", " ", "`t")
        Set-Content -LiteralPath $CodexAgentsMd -Value $kept -Encoding UTF8 -NoNewline
        Write-Ok "Removed CK navigation section from $CodexAgentsMd"
      }
    }
  }

  # Remove the project_doc_fallback_filenames line from config.toml.
  $CodexConfigToml = "$CodexHome\config.toml"
  if (Test-Path $CodexConfigToml) {
    $tomlContent = Get-Content $CodexConfigToml -Raw -ErrorAction SilentlyContinue
    if ($tomlContent -match 'project_doc_fallback_filenames') {
      if ($DryRun) {
        Write-Host "  [dry-run] remove project_doc_fallback_filenames from $CodexConfigToml"
      } else {
        $kept = @(Get-Content $CodexConfigToml | Where-Object { $_ -notmatch 'project_doc_fallback_filenames' })
        Set-Content -LiteralPath $CodexConfigToml -Value $kept -Encoding UTF8
        Write-Ok "Removed project_doc_fallback_filenames from $CodexConfigToml"
      }
    }
  }

  $CodexHooksJson = "$CodexHome\hooks.json"
  if (Test-Path $CodexHooksJson) {
    if ($DryRun) {
      Write-Host "  [dry-run] remove CK hook entries from $CodexHooksJson"
    } else {
      try {
        $cfg = Get-Content -LiteralPath $CodexHooksJson -Raw | ConvertFrom-Json -AsHashtable
        if ($cfg -and $cfg.ContainsKey('hooks') -and ($cfg['hooks'] -is [hashtable])) {
          $hooks = $cfg['hooks']
          $eventPatterns = @{
            PreToolUse   = 'ck-bash-guard|ck-read-guard|ck-search-guard'
            PostToolUse  = 'ck-scope-hint'
            SessionStart = 'ck-update-check'
            Stop         = 'ck-postsession'
          }
          foreach ($event in @($hooks.Keys)) {
            if (-not $eventPatterns.ContainsKey($event)) { continue }
            $pat = $eventPatterns[$event]
            $cleaned = @()
            foreach ($entry in @($hooks[$event])) {
              $entryHooks = @($entry['hooks'] | Where-Object { -not ([string]($_['command']) -match $pat) })
              if ($entryHooks.Count -gt 0) {
                $entry['hooks'] = $entryHooks
                $cleaned += $entry
              }
            }
            $hooks[$event] = $cleaned
          }
          $cfg['hooks'] = $hooks
          $cfg | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $CodexHooksJson -Encoding UTF8
          Write-Ok "Removed CK hook entries from $CodexHooksJson"
        }
      } catch {
        Write-Warning "Could not update $CodexHooksJson automatically. Remove the ck-* hook entries manually."
      }
    }
  }
  Write-Info ""
}

# ── OpenCode ───────────────────────────────────────────────────────────────────
if (-not $NoOpenCode) {
  Write-Info "── OpenCode ($OpenCodeHome\) ──────────────────────────────────────────"
  Remove-CkSkills "$OpenCodeHome\skills"
  Remove-Path "$OpenCodeHome\plugins\ck-guards.ts"
  Remove-Path "$LegacyOpenCodeHome\plugin\ck-guards.ts"
  Remove-Path "$OpenCodeHome\agents\explore.md"
  Remove-Path "$OpenCodeHome\ck-code-search-protocol.md"
  if (-not $DryRun) { Write-Ok "Skills, plugin, agent and protocol removed" }

  # Determine which OpenCode config file the installer wrote to.
  $OpenCodeCfg = ""
  if (Test-Path "$OpenCodeHome\opencode.jsonc") {
    $OpenCodeCfg = "$OpenCodeHome\opencode.jsonc"
  } elseif (Test-Path "$OpenCodeHome\opencode.json") {
    $OpenCodeCfg = "$OpenCodeHome\opencode.json"
  }

  if ($OpenCodeCfg) {
    if ($DryRun) {
      Write-Host "  [dry-run] remove CK plugin + instructions entries from $OpenCodeCfg"
    } else {
      try {
        $rawCfg = Get-Content -LiteralPath $OpenCodeCfg -Raw
        if ($OpenCodeCfg.ToLower().EndsWith('.jsonc')) {
          # Keep JSONC comments by patching only target arrays in-place.
          function Remove-JsoncArrayEntry {
            param([string]$Text, [string]$Key, [string]$Value)
            $pattern = '(?s)("' + [regex]::Escape($Key) + '"\s*:\s*\[)(.*?)(\])'
            $m = [regex]::Match($Text, $pattern)
            if (-not $m.Success) { return $Text }
            $body = $m.Groups[2].Value
            $quoted = [regex]::Escape($Value)
            $newBody = [regex]::Replace($body, '\s*"' + $quoted + '"\s*,?', '', 1)
            $newBody = [regex]::Replace($newBody, ',(\s*)$', '$1')
            $newBody = [regex]::Replace($newBody, '^(\s*),', '$1')
            return $Text.Substring(0, $m.Index) + $m.Groups[1].Value + $newBody + $m.Groups[3].Value + $Text.Substring($m.Index + $m.Length)
          }
          $rawCfg = Remove-JsoncArrayEntry -Text $rawCfg -Key 'plugin' -Value './plugins/ck-guards.ts'
          $rawCfg = Remove-JsoncArrayEntry -Text $rawCfg -Key 'instructions' -Value './ck-code-search-protocol.md'
          Set-Content -LiteralPath $OpenCodeCfg -Value $rawCfg -Encoding UTF8 -NoNewline
        } else {
          $cfg = $rawCfg | ConvertFrom-Json -AsHashtable
          if ($cfg.ContainsKey('plugin')) {
            $cfg['plugin'] = @($cfg['plugin'] | Where-Object { $_ -ne './plugins/ck-guards.ts' })
          }
          if ($cfg.ContainsKey('instructions')) {
            $cfg['instructions'] = @($cfg['instructions'] | Where-Object { $_ -ne './ck-code-search-protocol.md' })
          }
          $cfg | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OpenCodeCfg -Encoding UTF8
        }
        Write-Ok "Removed CK plugin and instructions entries from $OpenCodeCfg"
      } catch {
        Write-Warning "Could not update $OpenCodeCfg automatically. Remove './plugins/ck-guards.ts' from plugin[] and './ck-code-search-protocol.md' from instructions[] manually."
      }
    }
  }
  Write-Info ""
}

# ── Agent Skills ───────────────────────────────────────────────────────────────
if (-not $NoAgents) {
  Write-Info "── Agent Skills ($AgentsHome\skills\) ────────────────────────────────"
  Remove-CkSkills "$AgentsHome\skills"
  if (-not $DryRun) { Write-Ok "Skills removed" }
  Write-Info ""
}

# ── Summary ────────────────────────────────────────────────────────────────────
if ($DryRun) {
  Write-Info "Dry run complete — no changes were made."
} else {
  Write-Info "Context King uninstalled globally."
}
Write-Info ""
Write-Info "Per-repo files created by 'ck init' are NOT removed (they may be git-tracked)."
Write-Info "To remove them from a repository:"
Write-Info "  Remove-Item -Recurse -Force .ck-index, .ck-knowledge, .ck.json"
Write-Info "Also remove .ck-index\ from that repo's .gitignore if you added it."
