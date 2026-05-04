# ck-postsession: Stop hook (PowerShell) — knowledge capture gating.
#
# Fires after every supported CLI turn (Claude/Codex Stop hook). Guards against over-firing by scoring
# the new portion of the session transcript for codebase exploration signals.
#
# Signal thresholds (evaluated against tool_use entries only, not raw JSONL):
#   Strong (any one -> fire):   ck find-scope, ck signatures, ck get-method-source
#   Moderate (need >=2 -> fire): source Read call (.cs/.ts/.tsx), Edit/Write tool call, ck recall
#   Large-session fallback:      many source reads and/or many edits in this turn window
#
# Per-session state: .ck-knowledge/.postsession-offset
#   Line 1: transcript_path  (detects new session -> resets offset)
#   Line 2: line count at last run  (skips already-evaluated lines)

$inputData = $null
try { $inputData = $Input | Out-String | ConvertFrom-Json -ErrorAction Stop } catch { exit 0 }

$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) { exit 0 }

# Find CK binary: prefer project-local (Claude Code), fall back to Codex global.
$ck = $null
$ckLocal = Join-Path $repoRoot "ck"
if (Test-Path $ckLocal) {
    $ck = "ck"
} else {
    $codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME ".codex" }
    $ckCodex = Join-Path $codexHome "skills\ck\ck.cmd"
    if (Test-Path $ckCodex) {
        # Codex global install: only proceed if this project has CK knowledge enabled.
        if (-not (Test-Path (Join-Path $repoRoot ".ck-knowledge"))) { exit 0 }
        $ck = $ckCodex
    }
}
if (-not $ck) { exit 0 }

$transcript = $inputData.transcript_path
if (-not $transcript -or -not (Test-Path $transcript)) { exit 0 }

$knowledgeDir = Join-Path $repoRoot ".ck-knowledge"
if (-not (Test-Path $knowledgeDir)) { New-Item -ItemType Directory -Path $knowledgeDir -Force | Out-Null }
$offsetFile = Join-Path $knowledgeDir ".postsession-offset"

$storedPath = ""
$offset = 0
if (Test-Path $offsetFile) {
    $offsetLines = Get-Content $offsetFile -ErrorAction SilentlyContinue
    if ($offsetLines.Count -ge 1) { $storedPath = $offsetLines[0] }
    if ($offsetLines.Count -ge 2) { [int]::TryParse($offsetLines[1], [ref]$offset) | Out-Null }
}

# New session detected (transcript path changed) — start from the beginning.
if ($storedPath -ne $transcript) { $offset = 0 }

$allLines = @(Get-Content $transcript -ErrorAction SilentlyContinue)
$total = $allLines.Count

# Persist updated offset now so the knowledge-capture turn's own content
# is not re-evaluated on the subsequent turn.
Set-Content $offsetFile "$transcript`n$total" -ErrorAction SilentlyContinue

# Avoid infinite re-entry: hooks set stop_hook_active=true on the
# follow-up turn triggered by this hook's own additionalContext output.
if ($inputData.stop_hook_active -eq $true) { exit 0 }

# Nothing new since last run.
if ($total -le $offset) { exit 0 }

$newLines = if ($offset -lt $total) { $allLines[$offset..($total - 1)] } else { @() }

# ── Signal scoring (parse tool_use entries only — avoids skill_listing false positives) ──
$strong   = 0
$moderate = 0

$bashCmds  = [System.Collections.Generic.List[string]]::new()
$readPaths = [System.Collections.Generic.List[string]]::new()
$toolNames = [System.Collections.Generic.List[string]]::new()

foreach ($line in $newLines) {
    try {
        $entry = $line | ConvertFrom-Json -ErrorAction Stop
        $items = $entry.message.content
        if (-not $items) { continue }
        foreach ($item in $items) {
            if ($item.type -ne 'tool_use') { continue }
            $cmd = $item.input.command
            $fp = $item.input.file_path
            if ($cmd) {
                $bashCmds.Add($cmd)
                continue
            }
            if ($fp) {
                $readPaths.Add($fp)
                continue
            }
            $n = $item.name
            if ($n) {
                $toolNames.Add($n)
            }
        }
    } catch { }
}

$bashText = $bashCmds  -join "`n"
$readText = $readPaths -join "`n"
$toolText = $toolNames -join "`n"

if ($bashText -match 'find-scope')        { $strong = 1 }
if ($bashText -match 'ck signatures')     { $strong = 1 }
if ($bashText -match 'get-method-source') { $strong = 1 }

if ($readText -match '\.(cs|ts|tsx)(\b|$)')  { $moderate++ }
if ($toolText -match '(?m)^(Edit|Write|apply_patch)$')   { $moderate++ }
if ($bashText -match 'ck recall')   { $moderate++ }

$sourceReadCount = @($readPaths | Where-Object { $_ -match '\.(cs|ts|tsx)(\b|$)' }).Count
$editCount = @($toolNames | Where-Object { $_ -in @('Edit','Write','apply_patch') }).Count
$largeSession = ($sourceReadCount -ge 3) -or ($editCount -ge 3) -or (($sourceReadCount -ge 2) -and ($editCount -ge 1))

if ($strong -eq 0 -and $moderate -lt 2 -and -not $largeSession) { exit 0 }

# ── Inject knowledge-capture prompt ───────────────────────────────────────────
$context = @"
## Knowledge capture (ck-postsession)

Before this session ends, record institutional knowledge and review existing snippets.

**1. Record new discoveries**

``````powershell
$ck learn ``
  --content "<2-4 sentences>" ``
  --folders "<comma-separated folder paths>" ``
  --tags "<comma-separated keywords>"
``````

**What to record:** architectural patterns not obvious from folder structure, the WHY behind
decisions, gotchas and constraints, cross-module relationships that span folders.

**Before writing the content, strip:**
- File paths -- those go in ``--folders``, not in the text
- Function/method names discovered via ``ck signatures`` -- an agent can find those in 1 call
- Internal helper names, parameter types, specific flag names -- implementation detail
- Anything a future agent would see immediately by reading the relevant file

**Length check:** if your draft is more than 4 sentences, you are including implementation
detail. Cut until only the non-obvious insight remains.

**2. Review existing snippets for staleness**

``````powershell
$ck recall --folder <path-you-worked-in>
``````

If a snippet describes something refactored, renamed, or no longer true -- remove it:

``````powershell
$ck forget --id <snippet-id>
``````

If you worked in no source folders or found nothing worth recording, do nothing.
"@

@{additionalContext = $context} | ConvertTo-Json -Compress
