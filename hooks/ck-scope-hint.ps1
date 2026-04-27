# ck-scope-hint: PostToolUse hook for the Bash tool (PowerShell).
#
# Responsibilities:
# 1) Existing hint: for tight find-scope score clusters, suggest --min-score.
# 2) Stateful loop control support for PreToolUse guard:
#    - mark broad find-scope as requiring get-keyword-map before next scope/explore
#    - track last find-scope / expand-folder command (dedupe)
#    - track consecutive expand-folder no-match count per folder

if (-not (Get-Command jq -ErrorAction SilentlyContinue)) { exit 0 }

$input_json = $Input | Out-String
$tool = $input_json | jq -r '.tool_name // empty' 2>$null
if ($tool -ne "Bash") { exit 0 }

$command = $input_json | jq -r '.tool_input.command // empty' 2>$null
if ([string]::IsNullOrEmpty($command)) { exit 0 }

$output = $input_json | jq -r '.tool_response.output // empty' 2>$null

$repoRoot = (& git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) { $repoRoot = (Get-Location).Path }
$stateDir = Join-Path $repoRoot ".ck-index"
$stateFile = Join-Path $stateDir ".ck-guard-state.json"
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

if (Test-Path $stateFile) {
    try { $state = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { $state = $null }
} else {
    $state = $null
}

if (-not $state) {
    $state = [pscustomobject]@{
        keywordMapSeen = $false
        pendingKeywordMap = $false
        pendingQuery = ""
        lastFindScopeCommand = ""
        lastExpandFolderCommand = ""
        noMatchFolder = ""
        noMatchCount = 0
        knownTargetFile = ""
        knownTargetFolder = ""
        knownTargetFrom = ""
        expandFolderCount = 0
        signaturesFolderCount = 0
        scopedFolders = @()
        recentSearchToken = ""
        recentSearchCount = 0
        recentSearchFirstTs = 0
        lastBuildCheckCommand = ""
        lastBuildCheckTs = 0
        lastBuildCheckTree = ""
    }
}
if (-not ($state.PSObject.Properties.Name -contains 'keywordMapSeen')) {
    $state | Add-Member -NotePropertyName keywordMapSeen -NotePropertyValue $false
}

function Save-State($obj, $path) {
    $obj | ConvertTo-Json -Depth 6 | Set-Content -Path $path -NoNewline
}

function Extract-FileArgForTool([string]$cmd, [string]$toolName) {
    $m = [regex]::Match($cmd, "ck\s+$toolName\b\s+`"?([^`"\s]+\.(cs|ts|tsx))`"?")
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

function Extract-SearchTokenFamily([string]$cmd) {
    $token = ""
    if ($cmd -match '(^|[;&|\s])(grep|rg)\b') {
        $m = [regex]::Match($cmd, '(grep|rg)[^"]*"([^"]{3,})"')
        if ($m.Success) { $token = $m.Groups[2].Value }
        if (-not $token) {
            $m = [regex]::Match($cmd, "(grep|rg)[^']*'([^']{3,})'")
            if ($m.Success) { $token = $m.Groups[2].Value }
        }
        if (-not $token) {
            $parts = $cmd -split '\s+'
            for ($i = 0; $i -lt $parts.Length; $i++) {
                if ($parts[$i] -in @('grep', 'rg')) {
                    for ($j = $i + 1; $j -lt $parts.Length; $j++) {
                        if ($parts[$j].StartsWith('-')) { continue }
                        $token = $parts[$j]
                        break
                    }
                }
                if ($token) { break }
            }
        }
    } elseif ($cmd -match '(^|[;&|\s])find\b') {
        $m = [regex]::Match($cmd, '-name\s+"([^"]{3,})"')
        if ($m.Success) { $token = $m.Groups[1].Value }
        if (-not $token) {
            $m = [regex]::Match($cmd, "-name\s+'([^']{3,})'")
            if ($m.Success) { $token = $m.Groups[1].Value }
        }
    }

    if (-not $token) { return "" }
    $token = $token.ToLowerInvariant()
    $token = [regex]::Replace($token, '[^a-z0-9_]+', ' ').Trim()
    if (-not $token) { return "" }
    return ($token -split '\s+')[0]
}

function Get-GitTreeFingerprint([string]$root) {
    $status = & git -C $root status --porcelain --untracked-files=no 2>$null
    if (-not $status) { return "" }
    $statusText = ($status -join "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($statusText)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

if ($command -match 'ck\s+get-keyword-map\b') {
    $state.keywordMapSeen = $true
    $state.pendingKeywordMap = $false
    $state.pendingQuery = ""
    $state.noMatchFolder = ""
    $state.noMatchCount = 0
    $state.knownTargetFile = ""
    $state.knownTargetFolder = ""
    $state.knownTargetFrom = ""
    $state.expandFolderCount = 0
    $state.signaturesFolderCount = 0
    $state.scopedFolders = @()
    $state.recentSearchToken = ""
    $state.recentSearchCount = 0
    $state.recentSearchFirstTs = 0
    Save-State $state $stateFile
    exit 0
}

if ($command -match 'ck\s+find-scope\b') {
    $state.lastFindScopeCommand = $command
    $scoped = @()
    foreach ($line in ($output -split "`n")) {
        if ($line -match '^[0-9]+\.[0-9]+\t(.+)$') {
            $folder = $Matches[1].Replace('\', '/').TrimStart('./').TrimEnd('/')
            if ($folder) { $scoped += $folder }
        }
    }
    $state.scopedFolders = $scoped | Select-Object -Unique

    if ($output -match '\[ck find-scope\] Scope is too broad or ambiguous\.') {
        $q = ""
        if ($command -match '--query\s+"([^"]+)"') { $q = $Matches[1] }
        if (-not $q) { $q = "<same query>" }
        $state.pendingKeywordMap = $true
        $state.pendingQuery = $q
        $state.noMatchFolder = ""
        $state.noMatchCount = 0
        $state.knownTargetFile = ""
        $state.knownTargetFolder = ""
        $state.knownTargetFrom = ""
        $state.expandFolderCount = 0
        $state.signaturesFolderCount = 0
        $state.scopedFolders = @()
    } else {
        $state.pendingKeywordMap = $false
        $state.pendingQuery = ""
        $state.noMatchFolder = ""
        $state.noMatchCount = 0
        $state.knownTargetFile = ""
        $state.knownTargetFolder = ""
        $state.knownTargetFrom = ""
        $state.expandFolderCount = 0
        $state.signaturesFolderCount = 0
    }
    Save-State $state $stateFile
}

if ($command -match 'ck\s+expand-folder\b') {
    $state.lastExpandFolderCommand = $command
    $state.expandFolderCount = [int]$state.expandFolderCount + 1

    $noMatchLine = ($output -split "`n" | Where-Object { $_ -match '\[ck expand-folder\] No signatures matched pattern' } | Select-Object -First 1)
    if ($noMatchLine -and $noMatchLine -match " in '([^']+)'") {
        $folder = $Matches[1]
        if ($state.noMatchFolder -eq $folder) {
            $state.noMatchCount = [int]$state.noMatchCount + 1
        } else {
            $state.noMatchFolder = $folder
            $state.noMatchCount = 1
        }
    }
    Save-State $state $stateFile
}

if ($command -match 'ck\s+get-method-source\b') {
    $file = Extract-FileArgForTool $command "get-method-source"
    if ($file -and $output -notmatch '\b(ERROR|Error)\b') {
        $state.knownTargetFile = $file
        $state.knownTargetFolder = Split-Path -Parent $file
        $state.knownTargetFrom = "get-method-source"
        Save-State $state $stateFile
    }
}

if ($command -match 'ck\s+(get-constructors|get-usings|get-base-types)\b') {
    $file = Extract-FileArgForTool $command "get-constructors"
    if (-not $file) { $file = Extract-FileArgForTool $command "get-usings" }
    if (-not $file) { $file = Extract-FileArgForTool $command "get-base-types" }
    if ($file -and $output -notmatch '\b(ERROR|Error)\b') {
        $state.knownTargetFile = $file
        $state.knownTargetFolder = Split-Path -Parent $file
        $state.knownTargetFrom = "file-ast-read"
        Save-State $state $stateFile
    }
}

if ($command -match 'ck\s+signatures\b') {
    $file = Extract-FileArgForTool $command "signatures"
    if ($file -and $output -notmatch '\b(ERROR|Error)\b') {
        $state.knownTargetFile = $file
        $state.knownTargetFolder = Split-Path -Parent $file
        $state.knownTargetFrom = "signatures-file"
        Save-State $state $stateFile
    } elseif (-not $file -and $output -notmatch '\b(ERROR|Error)\b') {
        $state.signaturesFolderCount = [int]$state.signaturesFolderCount + 1
        Save-State $state $stateFile
    }
}

if ($command -match '(^|[;&|\s])(grep|rg|find)\b') {
    $token = Extract-SearchTokenFamily $command
    if ($token) {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        if ($state.recentSearchToken -eq $token -and (($now - [long]$state.recentSearchFirstTs) -le 90)) {
            $state.recentSearchCount = [int]$state.recentSearchCount + 1
        } else {
            $state.recentSearchToken = $token
            $state.recentSearchCount = 1
            $state.recentSearchFirstTs = $now
        }
        Save-State $state $stateFile
    }
}

if ($command -match 'ck\s+build-check\b') {
    $state.lastBuildCheckCommand = $command
    $state.lastBuildCheckTs = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $state.lastBuildCheckTree = Get-GitTreeFingerprint $repoRoot
    Save-State $state $stateFile
}

# Existing score-cluster hint logic (find-scope only)
if ($command -notmatch 'ck\s+find-scope\b') { exit 0 }
if ([string]::IsNullOrEmpty($output)) { exit 0 }

$scores = @()
foreach ($line in $output -split "`n") {
    if ($line -match '^([0-9]+\.[0-9]+)\t') {
        $scores += [double]$Matches[1]
    }
}
if ($scores.Count -lt 5) { exit 0 }

$maxScore = ($scores | Measure-Object -Maximum).Maximum
$minScore = ($scores | Measure-Object -Minimum).Minimum
$spread   = $maxScore - $minScore
$avgGap   = $spread / ($scores.Count - 1)
if ($avgGap -gt 0.01 -or $minScore -le 0.70) { exit 0 }

$suggested = [math]::Round($minScore - $avgGap, 2).ToString("F2")
$minFmt    = $minScore.ToString("F2")
$maxFmt    = $maxScore.ToString("F2")
$count     = $scores.Count

$hint = "[ck-hint] Scores are tightly clustered (${minFmt}-${maxFmt} across ${count} folders). The cutoff is likely mid-cluster - relevant folders may be missing. Re-run with --min-score ${suggested} to capture the full cluster."

@{
    hookSpecificOutput = @{
        hookEventName = "PostToolUse"
        additionalContext = $hint
    }
} | ConvertTo-Json -Depth 3
