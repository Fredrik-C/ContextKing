# install.ps1 — Context King self-extracting installer for Windows.
#
# Downloads Context King and deploys it based on which AI CLI tools
# (.claude, .codex, .opencode) are already configured in the target repo.
#
# ── Download and run ──────────────────────────────────────────────────────────
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

$GithubOwner  = 'Fredrik-C'
$GithubRepo   = 'ContextKing'
$GithubBranch = 'main'
$GithubRaw    = "https://raw.githubusercontent.com/$GithubOwner/$GithubRepo/$GithubBranch"

function Get-RemoteFile {
    param([string]$Url, [string]$Dest)
    $dir = Split-Path $Dest -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
}

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
# Also check if running from the repo root
if (-not $LocalRepo -and (Test-Path 'scripts\deploy.ps1') -and (Test-Path 'skills\ck\ck.cmd')) {
    $LocalRepo = (Get-Location).Path
}

# ── Detect configured CLI tools ───────────────────────────────────────────────
$HasClaude   = Test-Path (Join-Path $TargetRepo '.claude')   -PathType Container
$HasCodex    = Test-Path (Join-Path $TargetRepo '.codex')    -PathType Container
$HasOpenCode = Test-Path (Join-Path $TargetRepo '.opencode') -PathType Container

if (-not $HasClaude -and -not $HasCodex -and -not $HasOpenCode) {
    Write-Host ""
    Write-Host "No AI CLI configuration found in: $TargetRepo"
    Write-Host ""
    Write-Host "Context King supports the following CLI tools:"
    Write-Host "  Claude Code  -> run 'claude' in your repo to initialize (.claude/ will be created)"
    Write-Host "  Codex CLI    -> run 'codex init' in your repo (.codex/ will be created)"
    Write-Host "  OpenCode     -> run 'opencode' in your repo (.opencode/ will be created)"
    Write-Host ""
    Write-Host "Initialize at least one CLI tool, then re-run install.ps1."
    exit 1
}

$detected = @()
if ($HasClaude)   { $detected += 'Claude Code' }
if ($HasCodex)    { $detected += 'Codex CLI' }
if ($HasOpenCode) { $detected += 'OpenCode' }

Write-Host ""
Write-Host "Context King Installer"
Write-Host "  Target : $TargetRepo"
Write-Host "  Deploy : $($detected -join ', ')"
Write-Host ""

# ── Acquire assets ─────────────────────────────────────────────────────────────
$AssetsDir = $null

if ($LocalRepo) {
    Write-Host "Using local assets from: $LocalRepo"
    $AssetsDir = $LocalRepo
} else {
    Write-Host "Downloading Context King assets for Windows (win-x64) from GitHub..."

    $AssetsDir = Join-Path ([System.IO.Path]::GetTempPath()) "ck-install-$(New-Guid)"
    New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null

    try {
        # Deploy scripts
        foreach ($script in @('deploy.ps1', 'deploy-codex.ps1', 'deploy-opencode.ps1')) {
            Write-Host "  scripts/$script..." -NoNewline
            Get-RemoteFile "$GithubRaw/scripts/$script" (Join-Path $AssetsDir "scripts\$script")
            Write-Host " done"
        }

        # Skills: SKILL.md files
        foreach ($skill in @('ck-find-scope', 'ck-signatures', 'ck-get-method-source', 'ck-index')) {
            Write-Host "  skills/$skill/SKILL.md..." -NoNewline
            Get-RemoteFile "$GithubRaw/skills/$skill/SKILL.md" (Join-Path $AssetsDir "skills\$skill\SKILL.md")
            Write-Host " done"
        }

        # Wrapper scripts
        foreach ($wrapper in @('ck', 'ck.cmd')) {
            Write-Host "  skills/ck/$wrapper..." -NoNewline
            Get-RemoteFile "$GithubRaw/skills/ck/$wrapper" (Join-Path $AssetsDir "skills\ck\$wrapper")
            Write-Host " done"
        }

        # Windows binary (~29 MB)
        Write-Host "  binary: ck-win-x64.exe (~29 MB)..." -NoNewline
        Get-RemoteFile "$GithubRaw/skills/ck/ck-win-x64.exe" (Join-Path $AssetsDir 'skills\ck\ck-win-x64.exe')
        Write-Host " done"

        # Hooks
        foreach ($hook in @('ck-read-guard.sh', 'ck-read-guard.ps1', 'ck-search-guard.sh',
                             'ck-search-guard.ps1', 'agent-usage-guard.sh', 'agent-usage-guard.ps1')) {
            Write-Host "  hooks/$hook..." -NoNewline
            Get-RemoteFile "$GithubRaw/hooks/$hook" (Join-Path $AssetsDir "hooks\$hook")
            Write-Host " done"
        }

        # Models
        Write-Host "  models/bge-small-en-v1.5/vocab.txt..." -NoNewline
        Get-RemoteFile "$GithubRaw/models/bge-small-en-v1.5/vocab.txt" `
            (Join-Path $AssetsDir 'models\bge-small-en-v1.5\vocab.txt')
        Write-Host " done"

        Write-Host "  models/bge-small-en-v1.5/onnx/model_quantized.onnx (~34 MB)..." -NoNewline
        Get-RemoteFile "$GithubRaw/models/bge-small-en-v1.5/onnx/model_quantized.onnx" `
            (Join-Path $AssetsDir 'models\bge-small-en-v1.5\onnx\model_quantized.onnx')
        Write-Host " done"

        # Rules
        Write-Host "  rules/ck-code-search-protocol.md..." -NoNewline
        Get-RemoteFile "$GithubRaw/rules/ck-code-search-protocol.md" `
            (Join-Path $AssetsDir 'rules\ck-code-search-protocol.md')
        Write-Host " done"

        Write-Host ""
    } catch {
        Remove-Item -Recurse -Force $AssetsDir -ErrorAction SilentlyContinue
        throw
    }
}

# ── Run deployment ─────────────────────────────────────────────────────────────
$deployScript = Join-Path $AssetsDir 'scripts\deploy.ps1'
& pwsh -NonInteractive -File $deployScript -TargetRepo $TargetRepo

# Cleanup temp dir if we created one
if ($AssetsDir -ne $LocalRepo) {
    Remove-Item -Recurse -Force $AssetsDir -ErrorAction SilentlyContinue
}
