# install.ps1 — Context King installer for Windows.
#
# Downloads the Windows release archive from GitHub Releases,
# extracts it, and runs deploy.ps1 to install into the target repo.
#
# All assets come from the release archive — nothing is fetched from main.
# This ensures the installed version matches the release exactly.
#
# ── One-liner install ─────────────────────────────────────────────────────────
#   irm https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install.ps1 | iex
#
# ── Run from a cloned repo (uses local assets, no download needed) ────────────
#   .\scripts\install.ps1 [-TargetRepo <path>]
#
# If -TargetRepo is omitted, the current working directory is used.
#
# Requires: PowerShell 7+ (pwsh)

param(
    [string]$TargetRepo = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

$GithubOwner   = 'Fredrik-C'
$GithubRepo    = 'ContextKing'
$GithubRelease = "https://github.com/$GithubOwner/$GithubRepo/releases/latest/download"

# ── Validate target repo ───────────────────────────────────────────────────────
$TargetRepo = (Resolve-Path -LiteralPath $TargetRepo -ErrorAction Stop).Path
if (-not (Test-Path $TargetRepo -PathType Container)) {
    throw "Target directory does not exist: $TargetRepo"
}

# ── Detect if running from inside the ContextKing repo ──────────────────────
$LocalRepo = $null
if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'deploy.ps1'))) {
    $candidateRepo = Split-Path $PSScriptRoot -Parent
    if (Test-Path (Join-Path $candidateRepo 'skills\ck\ck.cmd')) {
        $LocalRepo = $candidateRepo
    }
}
if (-not $LocalRepo -and (Test-Path 'scripts\deploy.ps1') -and (Test-Path 'skills\ck\ck.cmd')) {
    $LocalRepo = (Get-Location).Path
}

# ── Detect configured CLI tools ───────────────────────────────────────────────
$HasClaude   = Test-Path (Join-Path $TargetRepo '.claude')   -PathType Container
$HasCodex    = Test-Path (Join-Path $TargetRepo '.codex')    -PathType Container
$HasOpenCode = Test-Path (Join-Path $TargetRepo '.opencode') -PathType Container
$HasAgents   = Test-Path (Join-Path $TargetRepo '.agents')   -PathType Container

if (-not $HasClaude -and -not $HasCodex -and -not $HasOpenCode -and -not $HasAgents) {
    Write-Host ""
    Write-Host "No AI CLI configuration found in: $TargetRepo"
    Write-Host ""
    Write-Host "Context King supports the following CLI tools:"
    Write-Host "  Claude Code  -> run 'claude' in your repo to initialize (.claude/ will be created)"
    Write-Host "  Codex CLI    -> run 'codex init' in your repo (.codex/ will be created)"
    Write-Host "  OpenCode     -> run 'opencode' in your repo (.opencode/ will be created)"
    Write-Host "  Agents       -> create .agents/ directory manually"
    Write-Host ""
    Write-Host "Initialize at least one CLI tool, then re-run install.ps1."
    exit 1
}

$detected = @()
if ($HasClaude)   { $detected += 'Claude Code' }
if ($HasCodex)    { $detected += 'Codex CLI' }
if ($HasOpenCode) { $detected += 'OpenCode' }
if ($HasAgents)   { $detected += 'Agents' }

Write-Host ""
Write-Host "Context King Installer"
Write-Host "  Target : $TargetRepo"
Write-Host "  Deploy : $($detected -join ', ')"
Write-Host ""

# ── Acquire assets ─────────────────────────────────────────────────────────────
if ($LocalRepo) {
    Write-Host "Using local assets from: $LocalRepo"
    & pwsh -NonInteractive -File (Join-Path $LocalRepo 'scripts\deploy.ps1') -TargetRepo $TargetRepo
} else {
    $archive = 'context-king-windows.zip'
    $archiveUrl = "$GithubRelease/$archive"

    Write-Host "Downloading $archive from latest release..."

    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "ck-install-$(New-Guid)"
    New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

    try {
        $archivePath = Join-Path $tmpDir $archive
        Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing

        Write-Host "Extracting..."
        Expand-Archive -Path $archivePath -DestinationPath $tmpDir -Force

        $assetsDir = Join-Path $tmpDir 'context-king'
        if (-not (Test-Path $assetsDir)) {
            throw "Archive did not contain expected context-king/ directory"
        }

        Write-Host ""
        & pwsh -NonInteractive -File (Join-Path $assetsDir 'scripts\deploy.ps1') -TargetRepo $TargetRepo
    } finally {
        Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
    }
}
