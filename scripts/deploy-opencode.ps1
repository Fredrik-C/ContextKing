# deploy-opencode.ps1 — installs Context King assets into a target repo's .opencode/ directory.
#
# Usage:
#   .\scripts\deploy-opencode.ps1 -TargetRepo <target-repo-root>
#
# After deploy, the target repo's .opencode/ will contain:
#   models/bge-small-en-v1.5/   — embedding model
#   skills/ck/                  — ck binaries + platform wrapper
#   skills/ck-*/SKILL.md        — skill docs (opencode paths)
#   AGENTS.md                   — code search protocol instructions
#   config.json                 — tool allowlist (created or merged)

param(
    [Parameter(Mandatory = $true)]
    [string]$TargetRepo
)

$ErrorActionPreference = 'Stop'
$RepoDir     = Split-Path $PSScriptRoot -Parent
$DotOpenCode = Join-Path $TargetRepo '.opencode'

Write-Host "Deploying Context King to OpenCode: $DotOpenCode"
New-Item -ItemType Directory -Force -Path $DotOpenCode | Out-Null

# ── 1. Copy models ──────────────────────────────────────────────────────────────
Write-Host "  Copying models..."
$modelsDir = Join-Path $DotOpenCode 'models'
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
Copy-Item -Path (Join-Path $RepoDir 'models\*') -Destination $modelsDir -Recurse -Force

# ── 2. Copy skills (with path rewriting) ────────────────────────────────────────
Write-Host "  Copying skills..."
$skillsDir = Join-Path $DotOpenCode 'skills'
New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null
Copy-Item -Path (Join-Path $RepoDir 'skills\*') -Destination $skillsDir -Recurse -Force

Write-Host "  Rewriting skill paths for OpenCode..."
$skillFiles = Get-ChildItem -Path $skillsDir -Recurse -Filter 'SKILL.md' -File
foreach ($skillFile in $skillFiles) {
    $text = Get-Content -LiteralPath $skillFile.FullName -Raw
    $text = $text.Replace('.claude/skills/ck/', '.opencode/skills/ck/')
    $text = $text.Replace('.claude\skills\ck\ck.cmd', '.opencode\skills\ck\ck.cmd')
    Set-Content -LiteralPath $skillFile.FullName -Value $text -Encoding UTF8
}

# ── 3. Write protocol file + short AGENTS.md pointer ───────────────────────────
# Full protocol goes to a dedicated file; AGENTS.md gets only a short pointer
# so the user's own AGENTS.md content isn't crowded out.
$protocolDest = Join-Path $DotOpenCode 'ck-code-search-protocol.md'
$protocolSrc  = Join-Path $RepoDir 'rules\ck-code-search-protocol.md'
$text = Get-Content -LiteralPath $protocolSrc -Raw
$text = $text.Replace('.claude/skills/ck/ck ', '.opencode/skills/ck/ck ')
$text = $text.Replace('.claude\skills\ck\ck.cmd', '.opencode\skills\ck\ck.cmd')
Set-Content -LiteralPath $protocolDest -Value $text -Encoding UTF8
Write-Host "  Wrote CK code search protocol to .opencode/ck-code-search-protocol.md"

$agentsMd = Join-Path $DotOpenCode 'AGENTS.md'
$needsPointer = $true
if (Test-Path $agentsMd) {
    $existing = Get-Content $agentsMd -Raw -ErrorAction SilentlyContinue
    if ($existing -match 'ck-code-search-protocol') { $needsPointer = $false }
}
if ($needsPointer) {
    $pointer = "## Context King — code search protocol`n`nThis repo has Context King installed for fast C# navigation.`nRead ``.opencode/ck-code-search-protocol.md`` for mandatory instructions before browsing ``.cs`` files.`n"
    Add-Content -LiteralPath $agentsMd -Value $pointer
    Write-Host "  Added Context King pointer to .opencode/AGENTS.md"
} else {
    Write-Host "  .opencode/AGENTS.md already references CK — skipping."
}

# ── 4. Copy plugin (OpenCode hook enforcement) ─────────────────────────────────
Write-Host "  Copying plugin..."
$pluginDir = Join-Path $DotOpenCode 'plugin'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoDir 'plugins\ck-guards.ts') `
          -Destination (Join-Path $pluginDir 'ck-guards.ts') -Force
Write-Host "  Deployed ck-guards.ts to .opencode/plugin/ (auto-loaded by OpenCode)"

# ── 5. Update .opencode/config.json tool allowlist (idempotent) ────────────────
$configPath = Join-Path $DotOpenCode 'config.json'
if (-not (Test-Path $configPath)) {
    '{}' | Set-Content $configPath -Encoding UTF8
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable

if (-not $config.ContainsKey('tools')) { $config['tools'] = @{} }
if (-not $config['tools'].ContainsKey('bash')) { $config['tools']['bash'] = @{} }
if (-not $config['tools']['bash'].ContainsKey('allow')) { $config['tools']['bash']['allow'] = @() }

$hasCkAllow = $config['tools']['bash']['allow'] |
    Where-Object { $_ -match 'opencode/skills/ck/ck' } |
    Measure-Object | Select-Object -ExpandProperty Count

if ($hasCkAllow -eq 0) {
    $config['tools']['bash']['allow'] += '.opencode/skills/ck/ck *'
    $config['tools']['bash']['allow'] += '.opencode\skills\ck\ck.cmd *'
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
    Write-Host "  Added ck to .opencode/config.json tool allowlist"
} else {
    Write-Host "  ck already in .opencode/config.json tool allowlist — skipping."
}

# ── 6. Add .ck-index/ to .gitignore ────────────────────────────────────────────
$gitignorePath = Join-Path $TargetRepo '.gitignore'
if (Test-Path $gitignorePath) {
    $existing = Get-Content $gitignorePath -Raw
    if ($existing -notmatch [regex]::Escape('.ck-index/')) {
        Add-Content $gitignorePath "`n# Context King index`n.ck-index/"
        Write-Host "  Added .ck-index/ to .gitignore"
    }
} else {
    "# Context King index`n.ck-index/" | Set-Content $gitignorePath -Encoding UTF8
    Write-Host "  Created .gitignore with .ck-index/"
}

Write-Host ""
Write-Host "Done. Context King is deployed for OpenCode."
Write-Host ""
Write-Host "First use: run 'ck find-scope' — the index will be built automatically."
Write-Host "Or pre-build now: cd `"$TargetRepo`" && .opencode/skills/ck/ck index"
