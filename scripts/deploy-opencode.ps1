# deploy-opencode.ps1 — installs Context King assets into a target repo's .opencode/ directory.
#
# Usage:
#   .\scripts\deploy-opencode.ps1 -TargetRepo <target-repo-root>
#
# After deploy, the target repo's .opencode/ will contain:
#   models/bge-small-en-v1.5/   — embedding model
#   skills/ck/                  — ck binaries + platform wrapper
#   skills/ck-*/SKILL.md        — skill docs (opencode paths)
#   ck-code-search-protocol.md  — full protocol reference
#   config.json                 — tool allowlist (created or merged)
#
# And in the target repo root:
#   AGENTS.md                   — inline 4-step workflow appended (OpenCode auto-loads this)

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

# ── 3. Write protocol file + inline workflow to repo-root AGENTS.md ────────────
# Full protocol goes to a dedicated file inside .opencode/.
# The 4-step workflow is appended to the repo-root AGENTS.md because that is
# the file OpenCode auto-loads into the system prompt. Writing to
# .opencode/AGENTS.md does NOT get auto-loaded.
$protocolDest = Join-Path $DotOpenCode 'ck-code-search-protocol.md'
$protocolSrc  = Join-Path $RepoDir 'rules\ck-code-search-protocol.md'
$text = Get-Content -LiteralPath $protocolSrc -Raw
$text = $text.Replace('.claude/skills/ck/ck ', '.opencode/skills/ck/ck ')
$text = $text.Replace('.claude\skills\ck\ck.cmd', '.opencode\skills\ck\ck.cmd')
Set-Content -LiteralPath $protocolDest -Value $text -Encoding UTF8
Write-Host "  Wrote CK code search protocol to .opencode/ck-code-search-protocol.md"

$agentsBlock = @'

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
'@

$agentsMd = Join-Path $TargetRepo 'AGENTS.md'
$existing = ''
if (Test-Path $agentsMd) {
    $existing = Get-Content $agentsMd -Raw -ErrorAction SilentlyContinue
}

if ($existing -match 'expand-folder') {
    Write-Host "  AGENTS.md already has CK expand-folder workflow — skipping."
} elseif ($existing -match 'ck-code-search-protocol') {
    # Upgrade: strip the old pointer-only CK section, then append the new inline block
    $lines = $existing -split "`r?`n"
    $sb = New-Object System.Text.StringBuilder
    $inCk = $false
    foreach ($line in $lines) {
        if ($line -match '^## Context King') { $inCk = $true; continue }
        if ($inCk -and $line -match '^## ') { $inCk = $false }
        if (-not $inCk) { [void]$sb.AppendLine($line) }
    }
    $cleaned = $sb.ToString().TrimEnd() + "`n" + $agentsBlock
    Set-Content -LiteralPath $agentsMd -Value $cleaned -Encoding UTF8
    Write-Host "  Upgraded Context King section in AGENTS.md (added expand-folder workflow)"
} else {
    Add-Content -LiteralPath $agentsMd -Value $agentsBlock
    Write-Host "  Added Context King inline workflow to AGENTS.md (repo root)"
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
    if ($existing -notmatch '\.ck-index') {
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
