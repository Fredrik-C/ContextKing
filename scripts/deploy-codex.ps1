# deploy-codex.ps1 — installs Context King assets into Codex home.
#
# Usage:
#   .\scripts\deploy-codex.ps1
#   .\scripts\deploy-codex.ps1 -CodexHome C:\path\to\.codex
#   .\scripts\deploy-codex.ps1 -TargetRepo C:\path\to\repo
#
# Notes:
# - Copies models and skills into Codex home.
# - Rewrites skill command examples from .claude paths to Codex-home paths.
# - Does not install Claude-specific hooks/settings.

param(
    [string]$CodexHome,
    [string]$TargetRepo
)

$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($CodexHome)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $CodexHome = $env:CODEX_HOME
    } else {
        $CodexHome = Join-Path $HOME '.codex'
    }
}

$CodexHome = (Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force -Path $CodexHome).FullName).Path
$modelsDir = Join-Path $CodexHome 'models'
$skillsDir = Join-Path $CodexHome 'skills'

Write-Host "Deploying Context King to Codex home: $CodexHome"

Write-Host '  Copying models...'
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
Copy-Item -Path (Join-Path $RepoDir 'models\*') -Destination $modelsDir -Recurse -Force

Write-Host '  Copying skills...'
New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null
Copy-Item -Path (Join-Path $RepoDir 'skills\*') -Destination $skillsDir -Recurse -Force

# Rewrite skill command examples so they point at Codex-home binaries rather than .claude.
Write-Host '  Rewriting skill paths for Codex...'
$skillFiles = Get-ChildItem -Path $skillsDir -Recurse -Filter 'SKILL.md' -File
foreach ($skillFile in $skillFiles) {
    $text = Get-Content -LiteralPath $skillFile.FullName -Raw

    $text = $text.Replace(
        '.claude\skills\ck\ck.cmd',
        '& "$($env:CODEX_HOME ? $env:CODEX_HOME : (Join-Path $HOME ".codex"))\skills\ck\ck.cmd"'
    )

    $text = $text.Replace('.claude/skills/ck/', '${CODEX_HOME:-$HOME/.codex}/skills/ck/')

    Set-Content -LiteralPath $skillFile.FullName -Value $text -Encoding UTF8
}

if (-not [string]::IsNullOrWhiteSpace($TargetRepo)) {
    if (-not (Test-Path -LiteralPath $TargetRepo -PathType Container)) {
        throw "TargetRepo does not exist or is not a directory: $TargetRepo"
    }

    $gitignorePath = Join-Path $TargetRepo '.gitignore'
    if (Test-Path -LiteralPath $gitignorePath) {
        $existing = Get-Content -LiteralPath $gitignorePath -Raw
        if ($existing -notmatch '\.ck-index') {
            Add-Content -LiteralPath $gitignorePath -Value "`n# Context King index`n.ck-index/"
            Write-Host '  Added .ck-index/ to .gitignore'
        } else {
            Write-Host '  .ck-index/ already present in .gitignore'
        }
    } else {
        "# Context King index`n.ck-index/" | Set-Content -LiteralPath $gitignorePath -Encoding UTF8
        Write-Host '  Created .gitignore with .ck-index/'
    }

    # Write the full CK code search protocol to a dedicated file so it doesn't
    # pollute the repo's own AGENTS.md with a large block of instructions.
    $codexRepoDir = Join-Path $TargetRepo '.codex'
    New-Item -ItemType Directory -Force -Path $codexRepoDir | Out-Null
    $protocolDest = Join-Path $codexRepoDir 'ck-code-search-protocol.md'
    $protocolSrc  = Join-Path $RepoDir 'rules\ck-code-search-protocol.md'
    $text = Get-Content -LiteralPath $protocolSrc -Raw
    # Rewrite .claude/ binary paths to the global Codex home path
    $text = $text.Replace(
        '.claude/skills/ck/ck ',
        '${CODEX_HOME:-$HOME/.codex}/skills/ck/ck '
    )
    $text = $text.Replace(
        '.claude\skills\ck\ck.cmd',
        '& "$($env:CODEX_HOME ? $env:CODEX_HOME : (Join-Path $HOME ".codex"))\skills\ck\ck.cmd"'
    )
    Set-Content -LiteralPath $protocolDest -Value $text -Encoding UTF8
    Write-Host '  Wrote CK code search protocol to .codex/ck-code-search-protocol.md'

    # Add a minimal pointer entry to AGENTS.md at the repo root — just enough
    # for Codex to know the protocol file exists, without bloating AGENTS.md.
    $agentsMdPath = Join-Path $TargetRepo 'AGENTS.md'
    $needsPointer = $true
    if (Test-Path $agentsMdPath) {
        $existing = Get-Content $agentsMdPath -Raw -ErrorAction SilentlyContinue
        if ($existing -match 'ck-code-search-protocol') { $needsPointer = $false }
    }
    if ($needsPointer) {
        $pointer = "`n## Context King — code search protocol`n`nThis repo has Context King installed for fast C# navigation.`nRead ``.codex/ck-code-search-protocol.md`` for mandatory instructions before browsing ``.cs`` files.`n"
        Add-Content -LiteralPath $agentsMdPath -Value $pointer
        Write-Host '  Added Context King pointer to AGENTS.md'
    } else {
        Write-Host '  AGENTS.md already references CK — skipping.'
    }
}

Write-Host ''
Write-Host 'Done. Context King is deployed for Codex.'
Write-Host ''
Write-Host 'Notes:'
Write-Host '  - Claude-specific hooks/settings were intentionally not installed.'
Write-Host '  - Use ck via skills or directly from:'
Write-Host "    $skillsDir\ck\ck.cmd (Windows)"
Write-Host "    $skillsDir/ck/ck     (Mac/Linux)"

