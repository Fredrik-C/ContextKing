# deploy.ps1 — unified Context King deployer.
#
# Detects which AI CLI tools are configured in the target repo and deploys
# Context King support for each one found:
#   .claude/    → Claude Code
#   .codex/     → Codex CLI
#   .opencode/  → OpenCode
#
# Usage:
#   .\scripts\deploy.ps1 -TargetRepo <target-repo-root>
#   .\scripts\deploy.ps1 -TargetRepo <target-repo-root> -All    # deploy regardless of detection
#
# Requires: PowerShell 7+ (pwsh)

param(
    [Parameter(Mandatory = $true)]
    [string]$TargetRepo,

    [switch]$All
)

$ErrorActionPreference = 'Stop'
$RepoDir   = Split-Path $PSScriptRoot -Parent
$ScriptDir = $PSScriptRoot

if (-not (Test-Path -LiteralPath $TargetRepo -PathType Container)) {
    Write-Error "Target directory does not exist: $TargetRepo"
    exit 1
}

# ── Detect configured CLI tools ────────────────────────────────────────────────
$HasClaude   = $All -or (Test-Path (Join-Path $TargetRepo '.claude')   -PathType Container)
$HasCodex    = $All -or (Test-Path (Join-Path $TargetRepo '.codex')    -PathType Container)
$HasOpenCode = $All -or (Test-Path (Join-Path $TargetRepo '.opencode') -PathType Container)

if (-not $HasClaude -and -not $HasCodex -and -not $HasOpenCode) {
    Write-Host ""
    Write-Host "No AI CLI configuration found in: $TargetRepo"
    Write-Host ""
    Write-Host "Context King supports the following CLI tools:"
    Write-Host "  Claude Code  -> initialize by running 'claude' in your repo  (.claude/ will be created)"
    Write-Host "  Codex CLI    -> initialize by running 'codex init' in your repo  (.codex/ will be created)"
    Write-Host "  OpenCode     -> initialize by running 'opencode' in your repo  (.opencode/ will be created)"
    Write-Host ""
    Write-Host "Create at least one of these directories, then re-run deploy.ps1."
    Write-Host "Or use -All to deploy for all supported CLI tools regardless of detection."
    exit 1
}

$detectedLabels = @()
if ($HasClaude)   { $detectedLabels += 'Claude Code' }
if ($HasCodex)    { $detectedLabels += 'Codex CLI' }
if ($HasOpenCode) { $detectedLabels += 'OpenCode' }

Write-Host "Deploying Context King to: $TargetRepo"
Write-Host "Detected CLI tools: $($detectedLabels -join ', ')"
Write-Host ""

# ── Deploy: Claude Code (.claude/) ────────────────────────────────────────────
if ($HasClaude) {
    Write-Host "── Claude Code (.claude/) ────────────────────────────────────────────────"
    $DotClaude = Join-Path $TargetRepo '.claude'
    New-Item -ItemType Directory -Force -Path $DotClaude | Out-Null

    Write-Host "  Copying models..."
    $modelsDir = Join-Path $DotClaude 'models'
    New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
    Copy-Item -Path (Join-Path $RepoDir 'models\*') -Destination $modelsDir -Recurse -Force

    Write-Host "  Copying skills..."
    $skillsDir = Join-Path $DotClaude 'skills'
    New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null
    Copy-Item -Path (Join-Path $RepoDir 'skills\*') -Destination $skillsDir -Recurse -Force

    Write-Host "  Copying rules..."
    $rulesDir = Join-Path $DotClaude 'rules'
    New-Item -ItemType Directory -Force -Path $rulesDir | Out-Null
    Copy-Item -Path (Join-Path $RepoDir 'rules\ck-code-search-protocol.md') -Destination $rulesDir -Force

    Write-Host "  Copying hooks..."
    $hooksDir = Join-Path $DotClaude 'hooks'
    New-Item -ItemType Directory -Force -Path $hooksDir | Out-Null
    Copy-Item -Path (Join-Path $RepoDir 'hooks\ck-read-guard.sh')       -Destination $hooksDir -Force
    Copy-Item -Path (Join-Path $RepoDir 'hooks\ck-read-guard.ps1')     -Destination $hooksDir -Force
    Copy-Item -Path (Join-Path $RepoDir 'hooks\ck-search-guard.sh')    -Destination $hooksDir -Force
    Copy-Item -Path (Join-Path $RepoDir 'hooks\ck-search-guard.ps1')   -Destination $hooksDir -Force
    Copy-Item -Path (Join-Path $RepoDir 'hooks\agent-usage-guard.sh')  -Destination $hooksDir -Force
    Copy-Item -Path (Join-Path $RepoDir 'hooks\agent-usage-guard.ps1') -Destination $hooksDir -Force

    $settingsPath = Join-Path $DotClaude 'settings.json'
    if (-not (Test-Path $settingsPath)) { '{}' | Set-Content $settingsPath -Encoding UTF8 }

    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable

    if (-not $settings.ContainsKey('permissions')) { $settings['permissions'] = @{} }
    if (-not $settings['permissions'].ContainsKey('allowedTools')) { $settings['permissions']['allowedTools'] = @() }

    $hasCkPermission = $settings['permissions']['allowedTools'] |
        Where-Object { $_ -match 'ck/ck' } | Measure-Object | Select-Object -ExpandProperty Count
    if ($hasCkPermission -eq 0) {
        $settings['permissions']['allowedTools'] += 'Bash(.claude/skills/ck/ck *)'
        $settings['permissions']['allowedTools'] += 'Bash(.claude\skills\ck\ck.cmd *)'
        Write-Host "  Added ck allowedTools permissions to settings.json"
    } else {
        Write-Host "  ck allowedTools permissions already present — skipping."
    }

    if (-not $settings.ContainsKey('hooks')) { $settings['hooks'] = @{} }
    if (-not $settings['hooks'].ContainsKey('PreToolUse')) { $settings['hooks']['PreToolUse'] = @() }

    $hasReadGuard = $settings['hooks']['PreToolUse'] |
        Where-Object { ($_.hooks | Where-Object { $_.command -match 'ck-read-guard' }) } |
        Measure-Object | Select-Object -ExpandProperty Count
    if ($hasReadGuard -eq 0) {
        $settings['hooks']['PreToolUse'] += @{
            matcher = 'Read'
            hooks   = @(
                @{ type = 'command'; command = '.claude/hooks/ck-read-guard.sh' },
                @{ type = 'command'; command = "bash -c 'command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-read-guard.ps1 || exit 0'" }
            )
        }
        Write-Host "  Registered Read hook in settings.json"
    } else { Write-Host "  Read hook already registered — skipping." }

    $hasSearchGuard = $settings['hooks']['PreToolUse'] |
        Where-Object { ($_.hooks | Where-Object { $_.command -match 'ck-search-guard' }) } |
        Measure-Object | Select-Object -ExpandProperty Count
    if ($hasSearchGuard -eq 0) {
        $settings['hooks']['PreToolUse'] += @{
            matcher = 'Glob'
            hooks   = @(
                @{ type = 'command'; command = '.claude/hooks/ck-search-guard.sh' },
                @{ type = 'command'; command = "bash -c 'command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-search-guard.ps1 || exit 0'" }
            )
        }
        $settings['hooks']['PreToolUse'] += @{
            matcher = 'Grep'
            hooks   = @(
                @{ type = 'command'; command = '.claude/hooks/ck-search-guard.sh' },
                @{ type = 'command'; command = "bash -c 'command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/ck-search-guard.ps1 || exit 0'" }
            )
        }
        Write-Host "  Registered Glob/Grep hooks in settings.json"
    } else { Write-Host "  Glob/Grep hooks already registered — skipping." }

    $hasAgentGuard = $settings['hooks']['PreToolUse'] |
        Where-Object { ($_.hooks | Where-Object { $_.command -match 'agent-usage-guard' }) } |
        Measure-Object | Select-Object -ExpandProperty Count
    if ($hasAgentGuard -eq 0) {
        $settings['hooks']['PreToolUse'] += @{
            matcher = 'Agent'
            hooks   = @(
                @{ type = 'command'; command = '.claude/hooks/agent-usage-guard.sh' },
                @{ type = 'command'; command = "bash -c 'command -v pwsh >/dev/null 2>&1 && pwsh -NonInteractive -File .claude/hooks/agent-usage-guard.ps1 || exit 0'" }
            )
        }
        Write-Host "  Registered Agent hook in settings.json"
    } else { Write-Host "  Agent hook already registered — skipping." }

    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

    $gitignorePath = Join-Path $TargetRepo '.gitignore'
    if (Test-Path $gitignorePath) {
        $existing = Get-Content $gitignorePath -Raw
        if ($existing -notmatch '\.ck-index') {
            Add-Content $gitignorePath "`n# Context King index`n.ck-index/"
            Write-Host "  Added .ck-index/ to .gitignore"
        }
    } else {
        "# Context King index`n.ck-index/" | Set-Content $gitignorePath -Encoding UTF8
        Write-Host "  Created .gitignore with .ck-index/"
    }

    Write-Host ""
}

# ── Deploy: Codex CLI (.codex/) ────────────────────────────────────────────────
if ($HasCodex) {
    Write-Host "── Codex CLI (.codex/) ───────────────────────────────────────────────────"
    & pwsh -NonInteractive -File (Join-Path $ScriptDir 'deploy-codex.ps1') -TargetRepo $TargetRepo
    Write-Host ""
}

# ── Deploy: OpenCode (.opencode/) ─────────────────────────────────────────────
if ($HasOpenCode) {
    Write-Host "── OpenCode (.opencode/) ─────────────────────────────────────────────────"
    & pwsh -NonInteractive -File (Join-Path $ScriptDir 'deploy-opencode.ps1') -TargetRepo $TargetRepo
    Write-Host ""
}

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Host "Done. Context King deployed for: $($detectedLabels -join ', ')"
Write-Host ""
Write-Host "First use: run 'ck find-scope' — the index will be built automatically."
