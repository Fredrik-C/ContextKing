# install-global.ps1 — Context King global user installer (Windows/PowerShell).
#
# Installs ck as a global user-level tool so it is available in every project
# without requiring a per-repo deploy step. Per-repo initialization is done with
# `ck init` after the global install.
#
# Usage:
#   iex (iwr 'https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.ps1').Content
#   .\scripts\install-global.ps1
#   .\scripts\install-global.ps1 -CkHome "$env:USERPROFILE\.ck" -NoClaude
#
# Parameters:
#   -CkHome <path>       Installation root (default: $env:USERPROFILE\.ck)
#   -ClaudeHome <path>   Claude Code config dir (default: $env:USERPROFILE\.claude)
#   -CodexHome <path>    Codex config dir (default: $env:USERPROFILE\.codex)
#   -OpenCodeHome <path> OpenCode config dir (default: $env:APPDATA\opencode)
#   Legacy plugin path (~\.opencode\plugin) is also synced.
#   -AgentsHome <path>   Agents skills dir (default: $env:USERPROFILE\.agents)
#   -NoPath              Skip PATH modification
#   -NoClaude            Skip Claude Code registration
#   -NoCodex             Skip Codex registration
#   -NoOpenCode          Skip OpenCode registration
#   -NoAgents            Skip Agents registration

param(
  [string]$CkHome      = "$env:USERPROFILE\.ck",
  [string]$ClaudeHome  = "$env:USERPROFILE\.claude",
  [string]$CodexHome   = "$env:USERPROFILE\.codex",
  [string]$OpenCodeHome = "$env:APPDATA\opencode",
  [string]$AgentsHome  = "$env:USERPROFILE\.agents",
  [switch]$NoPath,
  [switch]$NoClaude,
  [switch]$NoCodex,
  [switch]$NoOpenCode,
  [switch]$NoAgents
)

$ErrorActionPreference = "Stop"

$GithubOwner   = "Fredrik-C"
$GithubRepo    = "ContextKing"
$GithubRelease = "https://github.com/$GithubOwner/$GithubRepo/releases/latest/download"

$CkBinDir   = "$CkHome\bin"
$CkBin      = "$CkBinDir\ck.exe"
$CkModelDir = "$CkHome\models"
$LegacyOpenCodeHome = "$env:USERPROFILE\.opencode"

function Write-Ok($msg)   { Write-Host "  OK $msg" }
function Write-Info($msg) { Write-Host $msg }

# ── Detect local repo ──────────────────────────────────────────────────────────
$LocalRepo = ""
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoDir   = Split-Path -Parent $ScriptDir
if (Test-Path "$RepoDir\skills\ck\ck.cmd") { $LocalRepo = $RepoDir }
if (-not $LocalRepo -and (Test-Path ".\scripts\install-global.ps1") -and (Test-Path ".\skills\ck\ck.cmd")) {
  $LocalRepo = (Get-Location).Path
}

# ── Acquire assets ─────────────────────────────────────────────────────────────
$AssetsDir = ""
$TmpDir    = ""

if ($LocalRepo) {
  Write-Info "Using local assets from: $LocalRepo"
  $AssetsDir = $LocalRepo
} else {
  $Archive    = "context-king-win-x64.zip"
  $ArchiveUrl = "$GithubRelease/$Archive"

  Write-Info "Downloading $Archive from latest release..."
  $TmpDir = Join-Path $env:TEMP "ck-install-$(Get-Random)"
  New-Item -ItemType Directory -Path $TmpDir | Out-Null

  Invoke-WebRequest -Uri $ArchiveUrl -OutFile "$TmpDir\$Archive" -UseBasicParsing
  Expand-Archive "$TmpDir\$Archive" -DestinationPath $TmpDir

  $AssetsDir = "$TmpDir\context-king"
  if (-not (Test-Path $AssetsDir)) { throw "Archive did not contain expected context-king\ directory" }
}

Write-Info ""
Write-Info "Installing Context King globally"
Write-Info "  Binary : $CkBin"
Write-Info "  Models : $CkModelDir"
Write-Info ""

# ── Install binary ─────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Path $CkBinDir -Force | Out-Null

$SrcBin = "$AssetsDir\skills\ck\ck-win-x64.exe"
if (-not (Test-Path $SrcBin)) { $SrcBin = "$AssetsDir\skills\ck\ck.cmd" }
Copy-Item $SrcBin $CkBin -Force
Write-Ok "Binary installed: $CkBin"

# ── Install models ─────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Path $CkModelDir -Force | Out-Null
$ModelSrc = "$AssetsDir\models\bge-small-en-v1.5"
if (Test-Path $ModelSrc) {
  $ModelDest = "$CkModelDir\bge-small-en-v1.5"
  if (Test-Path $ModelDest) { Remove-Item $ModelDest -Recurse -Force }
  Copy-Item $ModelSrc $ModelDest -Recurse
  Write-Ok "Model installed: $ModelDest"
} else {
  Write-Warning "Model not found in assets — embedding commands (find-files, get-keyword-map, recall --query) will not work."
}

# ── Add to PATH ────────────────────────────────────────────────────────────────
if (-not $NoPath) {
  $UserPath = [Environment]::GetEnvironmentVariable("PATH", "User")
  if ($UserPath -notlike "*$CkBinDir*") {
    [Environment]::SetEnvironmentVariable("PATH", "$UserPath;$CkBinDir", "User")
    Write-Ok "Added $CkBinDir to user PATH"
  }
  $env:PATH = "$env:PATH;$CkBinDir"
}

# ── Helper: rewrite skill paths to absolute binary path ───────────────────────
# Uses the absolute path so subshells find ck without relying on the user's PATH.
function Rewrite-SkillsGlobal($SkillsRoot, $CkCmd = 'ck') {
  Get-ChildItem $SkillsRoot -Filter "SKILL.md" -Recurse | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $content = $content -replace '\.claude[/\\]skills[/\\]ck[/\\]ck\.cmd ', "$CkCmd "
    $content = $content -replace '\.claude[/\\]skills[/\\]ck[/\\]ck ', "$CkCmd "
    $content = $content -replace '\.claude[/\\]skills[/\\]ck[/\\]ck$', $CkCmd
    Set-Content $_.FullName $content -NoNewline
  }
}

function Remove-CkSkills($SkillsRoot) {
  if (-not (Test-Path $SkillsRoot)) { return }
  Remove-Item "$SkillsRoot\ck" -Recurse -Force -ErrorAction SilentlyContinue
  Get-ChildItem $SkillsRoot -Directory -Filter "ck-*" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Sync-EmbeddedCkBinary($HomeRoot, $CanonicalCkBin) {
  $embeddedDir = Join-Path $HomeRoot "skills\ck"
  if (-not (Test-Path $embeddedDir)) { return }
  if (-not (Test-Path $CanonicalCkBin)) { return }

  # Keep per-client wrapper + embedded binary aligned with the canonical install.
  $embeddedExe = Join-Path $embeddedDir "ck-win-x64.exe"
  Copy-Item $CanonicalCkBin $embeddedExe -Force
}

# ── Claude Code ────────────────────────────────────────────────────────────────
if (-not $NoClaude) {
  Write-Info "── Claude Code (~\.claude\) ────────────────────────────────────────────────"
  foreach ($d in @("skills","hooks","rules","models")) {
    New-Item -ItemType Directory -Path "$ClaudeHome\$d" -Force | Out-Null
  }

  Remove-CkSkills "$ClaudeHome\skills"
  # Copy skills, rewrite paths to absolute binary path
  Copy-Item "$AssetsDir\skills\*" "$ClaudeHome\skills\" -Recurse -Force
  Rewrite-SkillsGlobal "$ClaudeHome\skills" $CkBin
  Sync-EmbeddedCkBinary $ClaudeHome $CkBin
  Write-Ok "Skills installed"

  # Copy hooks
  $HookFiles = @(
    "agent-usage-guard.ps1","ck-bash-guard.ps1","ck-read-guard.ps1","ck-search-guard.ps1",
    "ck-scope-hint.ps1","ck-update-check.ps1","ck-postsession.ps1"
  )
  foreach ($f in $HookFiles) {
    if (Test-Path "$AssetsDir\hooks\$f") {
      Copy-Item "$AssetsDir\hooks\$f" "$ClaudeHome\hooks\$f" -Force
    }
  }
  Write-Ok "Hooks installed"

  # Copy rule and rewrite binary path placeholder to actual install path
  Copy-Item "$AssetsDir\rules\ck-code-search-protocol.md" "$ClaudeHome\rules\" -Force
  $rulePath = "$ClaudeHome\rules\ck-code-search-protocol.md"
  (Get-Content $rulePath -Raw) -replace [regex]::Escape('~/.ck/bin/ck'), $CkBin | Set-Content $rulePath -NoNewline
  Write-Ok "Rule installed"

  # Update settings.json
  $Settings = "$ClaudeHome\settings.json"
  if (-not (Test-Path $Settings)) { '{}' | Set-Content $Settings }

  if (Get-Command jq -ErrorAction SilentlyContinue) {
    $HookBase = "$ClaudeHome\hooks" -replace '\\','/'

    jq ".permissions.allowedTools = ((.permissions.allowedTools // []) + [`"Bash(ck *)`"])" `
      $Settings | Set-Content "$Settings.tmp"
    Move-Item "$Settings.tmp" $Settings -Force

    jq --arg base $HookBase ".hooks.PreToolUse = ((.hooks.PreToolUse // []) + [
      {`"matcher`":`"Bash`",`"hooks`":[{`"type`":`"command`",`"command`":(`$base + `"/ck-bash-guard.ps1`")}]},
      {`"matcher`":`"Read`",`"hooks`":[{`"type`":`"command`",`"command`":(`$base + `"/ck-read-guard.ps1`")}]},
      {`"matcher`":`"Grep`",`"hooks`":[{`"type`":`"command`",`"command`":(`$base + `"/ck-search-guard.ps1`")}]},
      {`"matcher`":`"Glob`",`"hooks`":[{`"type`":`"command`",`"command`":(`$base + `"/ck-search-guard.ps1`")}]}
    ])" $Settings | Set-Content "$Settings.tmp"
    Move-Item "$Settings.tmp" $Settings -Force

    $HookJson = "{`"type`":`"command`",`"command`":`"$HookBase/ck-postsession.ps1`"}"
    jq ".hooks.Stop = ((.hooks.Stop // []) + [{`"matcher`":`"*`",`"hooks`":[$HookJson]}])" `
      $Settings | Set-Content "$Settings.tmp"
    Move-Item "$Settings.tmp" $Settings -Force

    Write-Ok "Registered hooks and permissions in $Settings"
  } else {
    Write-Warning "jq not found — hook registration in $Settings skipped. Install jq and re-run."
  }

  # Models
  $ModelDest = "$ClaudeHome\models\bge-small-en-v1.5"
  if ((Test-Path "$CkModelDir\bge-small-en-v1.5") -and -not (Test-Path $ModelDest)) {
    Copy-Item "$CkModelDir\bge-small-en-v1.5" $ModelDest -Recurse
    Write-Ok "Model copied to $ClaudeHome\models\"
  }

  Write-Info ""
}

# ── Codex ──────────────────────────────────────────────────────────────────────
if (-not $NoCodex) {
  Write-Info "── Codex ($CodexHome\) ────────────────────────────────────────────────"
  foreach ($d in @("skills","hooks")) {
    New-Item -ItemType Directory -Path "$CodexHome\$d" -Force | Out-Null
  }

  Remove-CkSkills "$CodexHome\skills"
  Copy-Item "$AssetsDir\skills\*" "$CodexHome\skills\" -Recurse -Force
  Rewrite-SkillsGlobal "$CodexHome\skills" $CkBin
  Sync-EmbeddedCkBinary $CodexHome $CkBin
  Write-Ok "Skills installed"

  $CodexHookFiles = @(
    "ck-bash-guard.ps1","ck-read-guard.ps1","ck-search-guard.ps1","ck-scope-hint.ps1","ck-update-check.ps1","ck-postsession.ps1"
  )
  foreach ($f in $CodexHookFiles) {
    if (Test-Path "$AssetsDir\hooks\$f") {
      Copy-Item "$AssetsDir\hooks\$f" "$CodexHome\hooks\$f" -Force
    }
  }
  Write-Ok "Hooks installed"

  if (Test-Path "$AssetsDir\rules\ck-code-search-protocol.md") {
    Copy-Item "$AssetsDir\rules\ck-code-search-protocol.md" "$CodexHome\ck-code-search-protocol.md" -Force
    $codexProtocolPath = "$CodexHome\ck-code-search-protocol.md"
    (Get-Content $codexProtocolPath -Raw) -replace [regex]::Escape('~/.ck/bin/ck'), $CkBin | Set-Content $codexProtocolPath -NoNewline
    Write-Ok "Protocol installed"
  }

  # Merge CK navigation section into AGENTS.md (idempotent via sentinel comment)
  $CodexAgentsMd = "$CodexHome\AGENTS.md"
  if (-not (Test-Path $CodexAgentsMd)) { '' | Set-Content $CodexAgentsMd }
  $agentsContent = Get-Content $CodexAgentsMd -Raw -ErrorAction SilentlyContinue
  if (-not ($agentsContent -match [regex]::Escape('## CODE NAVIGATION (Context King)'))) {
    $fence = '```'
    $bt = '`'
    $ckSection = @"

## CODE NAVIGATION (Context King)

This codebase uses Context King (CK) for source navigation. Follow this protocol for ALL C# and TypeScript/TSX source file search.

**Binary:** $bt$CkBin$bt (or ${bt}ck${bt} if in PATH)

### Mandatory workflow

$fence
0. ck get-keyword-map --query "<domain concept operation>"   <- FIRST — always before any search
1. ck find-files --query "<refined terms>"                   <- SECOND — establishes folder scope
2. ck expand-folder --pattern "<keyword>" <folder>           <- explore within scoped folders
   ck signatures <folder>/                                   <- when no keyword available
2.5 ck recall --folder <confirmed-folder>                    <- before reading any method body
3. ck find-symbol "<name>" --path <folder>                   <- locate declaration
   ck refs "<name>" --path <folder>                          <- find call-sites
4. ck get-method-source <file> <Member>                      <- read one method (prefer over full file)
   ck get-type-source <file> <TypeName>                      <- read one type/record/interface
   ck get-constructors <file>                                <- constructor(s)
   ck get-usings <file>                                      <- imports only
   ck get-base-types <file>                                  <- inheritance info
   ck read-full-file <file>                                  <- full file when truly needed
5. [edit]
6. ck learn                                                  <- MUST run if CK tools were used
$fence

### Rules
- Steps 0 and 1 are **mandatory** before grep, rg, glob, or find on source files
- When any other search tool (CallGraph, etc.) returns empty results, use CK tools next — **never fall back directly to grep**
- grep/rg/glob are only allowed within folders returned by ${bt}ck find-files${bt}
- **You MUST run ${bt}ck learn${bt} before ending the session if CK tools were used.** Record architectural insights, cross-module constraints, and non-obvious WHYs — not implementation details findable via signatures.

Full protocol reference: ${bt}$CodexHome\ck-code-search-protocol.md${bt}
"@
    Add-Content -Path $CodexAgentsMd -Value $ckSection -NoNewline
    Write-Ok "CK navigation section added to $CodexAgentsMd"
  } else {
    Write-Ok "AGENTS.md already contains CK navigation section (skipped)"
  }

  # Add project_doc_fallback_filenames to config.toml (idempotent)
  $CodexConfigToml = "$CodexHome\config.toml"
  if (Test-Path $CodexConfigToml) {
    $tomlContent = Get-Content $CodexConfigToml -Raw -ErrorAction SilentlyContinue
    if ($tomlContent -notmatch 'project_doc_fallback_filenames') {
      Add-Content -Path $CodexConfigToml -Value "`nproject_doc_fallback_filenames = [`"ck-code-search-protocol.md`"]"
      Write-Ok "Added project_doc_fallback_filenames to $CodexConfigToml"
    } else {
      Write-Ok "config.toml already has project_doc_fallback_filenames (skipped)"
    }
  } else {
    Set-Content -Path $CodexConfigToml -Value "project_doc_fallback_filenames = [`"ck-code-search-protocol.md`"]"
    Write-Ok "Created $CodexConfigToml with project_doc_fallback_filenames"
  }

  $CodexHooksJson = "$CodexHome\hooks.json"
  if (-not (Test-Path $CodexHooksJson)) { '{}' | Set-Content $CodexHooksJson }
  try {
    $cfg = Get-Content -LiteralPath $CodexHooksJson -Raw | ConvertFrom-Json -AsHashtable
    if (-not $cfg.ContainsKey('hooks')) { $cfg['hooks'] = @{} }
    $hooks = $cfg['hooks']
    if (-not $hooks.ContainsKey('PreToolUse')) { $hooks['PreToolUse'] = @() }
    if (-not $hooks.ContainsKey('PostToolUse')) { $hooks['PostToolUse'] = @() }
    if (-not $hooks.ContainsKey('SessionStart')) { $hooks['SessionStart'] = @() }
    if (-not $hooks.ContainsKey('Stop')) { $hooks['Stop'] = @() }

    $preToolUse = @($hooks['PreToolUse'])
    $preCleaned = @()
    foreach ($entry in $preToolUse) {
      $entryHooks = @($entry['hooks'])
      $entryHooks = @($entryHooks | Where-Object { -not ([string]($_['command']) -match 'ck-bash-guard|ck-read-guard|ck-search-guard') })
      if ($entryHooks.Count -gt 0) {
        $entry['hooks'] = $entryHooks
        $preCleaned += $entry
      }
    }

    $preCleaned += @{
      matcher = 'Bash'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-bash-guard.ps1'
      })
    }
    $preCleaned += @{
      matcher = 'Read'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-read-guard.ps1'
      })
    }
    $preCleaned += @{
      matcher = 'Grep'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-search-guard.ps1'
      })
    }
    $preCleaned += @{
      matcher = 'Glob'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-search-guard.ps1'
      })
    }

    $postToolUse = @($hooks['PostToolUse'])
    $postCleaned = @()
    foreach ($entry in $postToolUse) {
      $entryHooks = @($entry['hooks'])
      $entryHooks = @($entryHooks | Where-Object { -not ([string]($_['command']) -match 'ck-scope-hint') })
      if ($entryHooks.Count -gt 0) {
        $entry['hooks'] = $entryHooks
        $postCleaned += $entry
      }
    }
    $postCleaned += @{
      matcher = 'Bash'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-scope-hint.ps1'
      })
    }

    $sessionStart = @($hooks['SessionStart'])
    $sessionCleaned = @()
    foreach ($entry in $sessionStart) {
      $entryHooks = @($entry['hooks'])
      $entryHooks = @($entryHooks | Where-Object { -not ([string]($_['command']) -match 'ck-update-check') })
      if ($entryHooks.Count -gt 0) {
        $entry['hooks'] = $entryHooks
        $sessionCleaned += $entry
      }
    }
    $sessionCleaned += @{
      matcher = 'startup|resume|clear'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-update-check.ps1'
        timeout = 15
      })
    }

    $stopHooks = @($hooks['Stop'])
    $stopCleaned = @()
    foreach ($entry in $stopHooks) {
      $entryHooks = @($entry['hooks'])
      $entryHooks = @($entryHooks | Where-Object { -not ([string]($_['command']) -match 'ck-postsession') })
      if ($entryHooks.Count -gt 0) {
        $entry['hooks'] = $entryHooks
        $stopCleaned += $entry
      }
    }
    $stopCleaned += @{
      matcher = '*'
      hooks = @(@{
        type = 'command'
        command = ($CodexHome -replace '\\','/') + '/hooks/ck-postsession.ps1'
      })
    }

    $hooks['PreToolUse'] = $preCleaned
    $hooks['PostToolUse'] = $postCleaned
    $hooks['SessionStart'] = $sessionCleaned
    $hooks['Stop'] = $stopCleaned
    $cfg['hooks'] = $hooks
    $cfg | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $CodexHooksJson -Encoding UTF8
    Write-Ok "Registered Codex hooks in $CodexHooksJson"
  } catch {
    Write-Warning "Could not update $CodexHooksJson automatically. Add PreToolUse/PostToolUse/SessionStart/Stop CK hooks manually."
  }

  Write-Info ""
}

# ── OpenCode ───────────────────────────────────────────────────────────────────
if (-not $NoOpenCode) {
  Write-Info "── OpenCode ($OpenCodeHome\) ──────────────────────────────────────────"
  foreach ($d in @("skills","plugins","agents")) {
    New-Item -ItemType Directory -Path "$OpenCodeHome\$d" -Force | Out-Null
  }

  Remove-CkSkills "$OpenCodeHome\skills"
  Copy-Item "$AssetsDir\skills\*" "$OpenCodeHome\skills\" -Recurse -Force
  Rewrite-SkillsGlobal "$OpenCodeHome\skills" $CkBin
  Sync-EmbeddedCkBinary $OpenCodeHome $CkBin
  Write-Ok "Skills installed"

  if (Test-Path "$AssetsDir\plugins\ck-guards.ts") {
    Copy-Item "$AssetsDir\plugins\ck-guards.ts" "$OpenCodeHome\plugins\" -Force
    New-Item -ItemType Directory -Path "$LegacyOpenCodeHome\plugin" -Force | Out-Null
    Copy-Item "$AssetsDir\plugins\ck-guards.ts" "$LegacyOpenCodeHome\plugin\ck-guards.ts" -Force
    Write-Ok "Plugin installed"
  }

  if (Test-Path "$AssetsDir\agents\explore.md") {
    Copy-Item "$AssetsDir\agents\explore.md" "$OpenCodeHome\agents\" -Force
    Write-Ok "Explore agent installed"
  }

  # Copy CK protocol reference used by OpenCode instructions[].
  if (Test-Path "$AssetsDir\rules\ck-code-search-protocol.md") {
    Copy-Item "$AssetsDir\rules\ck-code-search-protocol.md" "$OpenCodeHome\ck-code-search-protocol.md" -Force
    $protocolPath = "$OpenCodeHome\ck-code-search-protocol.md"
    (Get-Content $protocolPath -Raw) -replace [regex]::Escape('~/.ck/bin/ck'), $CkBin | Set-Content $protocolPath -NoNewline
    Write-Ok "Protocol installed"
  }

  # Merge into OpenCode config (idempotent). Prefer opencode.jsonc when present.
  $OpenCodeCfgJsonc = "$OpenCodeHome\opencode.jsonc"
  $OpenCodeCfgJson = "$OpenCodeHome\opencode.json"
  if (Test-Path $OpenCodeCfgJsonc) {
    $OpenCodeCfg = $OpenCodeCfgJsonc
  } elseif (Test-Path $OpenCodeCfgJson) {
    $OpenCodeCfg = $OpenCodeCfgJson
  } else {
    # Default to jsonc for new installs. If jsonc already exists, never create json.
    $OpenCodeCfg = $OpenCodeCfgJsonc
    '{}' | Set-Content $OpenCodeCfg
  }
  try {
    $rawCfg = Get-Content -LiteralPath $OpenCodeCfg -Raw
    if ($OpenCodeCfg.ToLower().EndsWith('.jsonc')) {
      # Keep JSONC comments by patching only target arrays in-place.
      function Ensure-JsoncArrayEntry {
        param(
          [string]$Text,
          [string]$Key,
          [string]$Value
        )

        $pattern = '(?s)("'+ [regex]::Escape($Key) + '"\s*:\s*\[)(.*?)(\])'
        $m = [regex]::Match($Text, $pattern)
        if ($m.Success) {
          if ($m.Groups[2].Value -match ('"' + [regex]::Escape($Value) + '"')) {
            return $Text
          }

          $body = $m.Groups[2].Value
          $itemIndent = '    '
          $indentMatch = [regex]::Match($body, "`n([ `t]*)[^ `t`r`n]")
          if ($indentMatch.Success) { $itemIndent = $indentMatch.Groups[1].Value }

          $plainBody = $body
          $plainBody = [regex]::Replace($plainBody, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
          $plainBody = [regex]::Replace($plainBody, '(?m)//.*$', '')
          $plainBody = $plainBody.Trim()

          if ([string]::IsNullOrWhiteSpace($plainBody)) {
            $newBody = "`n$itemIndent`"$Value`"`n  "
          } else {
            $bodyTrim = $body.TrimEnd()
            $sep = if ($bodyTrim.EndsWith(',')) { '' } else { ',' }
            $newBody = "$body$sep`n$itemIndent`"$Value`""
          }

          return $Text.Substring(0, $m.Index) + $m.Groups[1].Value + $newBody + $m.Groups[3].Value + $Text.Substring($m.Index + $m.Length)
        }

        $end = [regex]::Match($Text, '}\s*$')
        if (-not $end.Success) { return $Text }
        $prefix = $Text.Substring(0, $end.Index)
        $suffix = $Text.Substring($end.Index)
        $inside = $prefix
        $firstBrace = $inside.IndexOf('{')
        if ($firstBrace -ge 0) { $inside = $inside.Substring($firstBrace + 1) }
        $inside = [regex]::Replace($inside, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
        $inside = [regex]::Replace($inside, '(?m)//.*$', '')
        $comma = if ([string]::IsNullOrWhiteSpace($inside.Trim())) { '' } else { ',' }
        $block = "$comma`n  `"$Key`": [`n    `"$Value`"`n  ]`n"
        return "$prefix$block$suffix"
      }

      $rawCfg = Ensure-JsoncArrayEntry -Text $rawCfg -Key 'plugin' -Value './plugins/ck-guards.ts'
      $rawCfg = Ensure-JsoncArrayEntry -Text $rawCfg -Key 'instructions' -Value './ck-code-search-protocol.md'
      Set-Content -LiteralPath $OpenCodeCfg -Value $rawCfg -Encoding UTF8 -NoNewline
    } else {
      $cfg = $rawCfg | ConvertFrom-Json -AsHashtable
      if (-not $cfg.ContainsKey('plugin')) { $cfg['plugin'] = @() }
      if (-not $cfg.ContainsKey('instructions')) { $cfg['instructions'] = @() }
      if ($cfg.ContainsKey('tools') -and $cfg['tools'] -is [hashtable]) {
        $tools = $cfg['tools']
        if ($tools.ContainsKey('bash') -and $tools['bash'] -is [hashtable]) {
          $tools['bash'] = $true
        }
      }

      $plugins = @($cfg['plugin'])
      if ($plugins -notcontains './plugins/ck-guards.ts') { $plugins += './plugins/ck-guards.ts' }
      $cfg['plugin'] = $plugins

      $instructions = @($cfg['instructions'])
      if ($instructions -notcontains './ck-code-search-protocol.md') { $instructions += './ck-code-search-protocol.md' }
      $cfg['instructions'] = $instructions

      $cfg | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OpenCodeCfg -Encoding UTF8
    }
    Write-Ok "Registered OpenCode plugin and instructions in $OpenCodeCfg"
  } catch {
    Write-Warning "Could not update $OpenCodeCfg automatically. Merge plugin/instructions manually."
  }

  Write-Info ""
}

# ── Agent Skills ───────────────────────────────────────────────────────────────
if (-not $NoAgents) {
  Write-Info "── Agent Skills ($AgentsHome\skills\) ────────────────────────────────"
  New-Item -ItemType Directory -Path "$AgentsHome\skills" -Force | Out-Null

  Remove-CkSkills "$AgentsHome\skills"
  Copy-Item "$AssetsDir\skills\*" "$AgentsHome\skills\" -Recurse -Force
  Rewrite-SkillsGlobal "$AgentsHome\skills" $CkBin
  Sync-EmbeddedCkBinary $AgentsHome $CkBin
  Write-Ok "Skills installed"

  Write-Info ""
}

# ── Cleanup ────────────────────────────────────────────────────────────────────
if ($TmpDir -and (Test-Path $TmpDir)) { Remove-Item $TmpDir -Recurse -Force }

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Info "Context King installed globally."
Write-Info ""
Write-Info "Next steps:"
Write-Info "  1. Restart your terminal (PATH update takes effect in new sessions)"
Write-Info "  2. In each repository: ck init"
Write-Info "     This creates .ck.json (version requirement) and .ck-knowledge\ directory."
Write-Info ""
Write-Info "  ck --version   ->  confirm the installed version"
Write-Info "  ck --help      ->  show all commands"
