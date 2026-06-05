#!/usr/bin/env pwsh
# ck-bash-guard: PreToolUse hook for the Bash tool (PowerShell version).
# Enforces the CK navigation protocol by blocking repo-wide search anti-patterns
# and enforcing CK workflow boundaries (file-first by default).
#
# grep/rg/glob are allowed freely within boundaries from find-files.
# Blocked: piping ck output through filters, broad recursive grep/find from source
# roots, full-file bulk reads (find -exec cat), and repeated/premature scope calls.

$ErrorActionPreference = 'SilentlyContinue'

$raw = $input | Out-String
if (-not $raw) { exit 0 }

try { $obj = $raw | ConvertFrom-Json } catch { exit 0 }

$command = if ($obj.tool_input.command) { $obj.tool_input.command }
           elseif ($obj.toolInput.command) { $obj.toolInput.command }
           else { '' }

if (-not $command) { exit 0 }

# Knowledge JSONL guardrail: block direct raw reads/writes outside CK commands.
# Exempt `ck` (the sanctioned path) and `git` — version-control operations such as
# `git diff/status/log/show/add` on the store are legitimate and must not be blocked.
if ($command -match '(^|\s)((\./)?\.ck-knowledge[/\\].*\.jsonl)(\s|$)') {
    if ($command -notmatch '(^|[;&|\s])([^ \t]+/)?(ck(\.exe)?|git)\s') {
        @{
            hookSpecificOutput = @{
                hookEventName = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = @"
[ck-guard] BLOCKED — direct access to CK knowledge JSONL files is not allowed.

Use CK commands so migration/backfill and writes stay centralized in CLI:
  ck recall --folder <path>
  ck learn --content "..." --folders "..."
  ck forget --id <uuid>
"@
            }
        } | ConvertTo-Json -Depth 3
        exit 0
    }
}

# Stateful anti-loop guards (.ck-index/.ck-guard-state.json)
$repoRoot = (& git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) { $repoRoot = (Get-Location).Path }
if (-not (Test-Path (Join-Path $repoRoot '.ck.json'))) { exit 0 }

$stateDir = Join-Path $repoRoot ".ck-index"
$stateFile = Join-Path $stateDir ".ck-guard-state.json"

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
        lastFindFilesCommand = ""
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

$scopedFolders = @()
if ($state.scopedFolders) { $scopedFolders = @($state.scopedFolders) }

function NormalizePathToken([string]$path) {
    if (-not $path) { return "" }
    return $path.TrimStart('./').TrimEnd('/')
}

function IsWithinScopedFolders([string]$path, [string[]]$folders) {
    $norm = NormalizePathToken $path
    if (-not $norm) { return $false }
    foreach ($folder in $folders) {
        $f = NormalizePathToken ([string]$folder)
        if (-not $f) { continue }
        if ($norm -eq $f -or $norm.StartsWith("$f/")) { return $true }
    }
    return $false
}

function ExtractCommandPaths([string]$cmd) {
    [regex]::Matches($cmd, '(\.?/)?src(/[A-Za-z0-9._-]+)+') |
        ForEach-Object { $_.Value.TrimStart('./') } |
        Sort-Object -Unique
}

function ExtractSearchTokenFamily([string]$cmd) {
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
    $token = [regex]::Replace($token.ToLowerInvariant(), '[^a-z0-9_]+', ' ').Trim()
    if (-not $token) { return "" }
    return ($token -split '\s+')[0]
}

function GetGitTreeFingerprint([string]$root) {
    $status = & git -C $root status --porcelain --untracked-files=no 2>$null
    if (-not $status) { return "" }
    $statusText = ($status -join "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($statusText)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

if ($state.pendingKeywordMap -and
    $command -match 'ck\s+expand-folder\b' -and
    $command -notmatch 'ck\s+get-keyword-map\b') {
    $pendingQuery = if ([string]::IsNullOrWhiteSpace([string]$state.pendingQuery)) { "<same query>" } else { [string]$state.pendingQuery }
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — previous ck find-files was broad/ambiguous.

Run keyword mapping before more expand-folder calls:

  ck get-keyword-map --query "$pendingQuery"

Then treat keyword-map/session-keyword-atlas as source-of-truth for this direction. Pick 3-7 precision terms (provider/domain + workflow + symbol/DTO/type), then rerun ck find-files with refined terms.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($command -match 'ck\s+find-files\b' -and $command -eq $state.lastFindFilesCommand) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — repeated identical ck find-files command.

Do not rerun the same scope command unchanged. If previous output was broad:
  ck get-keyword-map --query "<same query>"
Then rerun find-files with refined terms.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($command -match 'ck\s+expand-folder\b' -and $command -eq $state.lastExpandFolderCommand) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — repeated identical ck expand-folder command.

Refine --pattern using add-keyword-hints instead of rerunning the same command.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($command -match 'ck\s+expand-folder\b' -and
    $state.noMatchCount -ge 2 -and
    $state.noMatchFolder -and
    $command -like "*$($state.noMatchFolder)*") {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — this folder already had 2 consecutive expand-folder no-match results.

Stop expanding the same folder. Either:
  1) run ck get-keyword-map + refined ck find-files, or
  2) switch to another scoped folder.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($command -match 'ck\s+expand-folder\b' -and
    $state.knownTargetFile) {
    $knownTargetFile = [string]$state.knownTargetFile
    $knownTargetFrom = [string]$state.knownTargetFrom
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — expand-folder is for uncharted map-building, not after a concrete file target is known in this direction.

Known target from $knownTargetFrom:
  $knownTargetFile

Next step in this direction:
  ck signatures "$knownTargetFile"
  ck get-method-source "$knownTargetFile" <MemberName>

If your direction changed, reset scope explicitly with:
  ck find-files --query "<new direction query>"
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

if ($command -match 'ck\s+expand-folder\b' -and
    [int]$state.expandFolderCount -ge 3 -and
    -not $state.knownTargetFile) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — expand-folder map-building budget reached (3 calls for this direction).

Use targeted reads now:
  ck signatures <file.cs>
  ck get-method-source <file.cs> <MemberName>

If still uncharted, reset direction first:
  ck get-keyword-map --query "<same query>"
  ck find-files --query "<refined query>"
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}


# Pattern 0: filtering saved Claude tool-result files
if ($command -match '\.claude/projects/.*/tool-results/' -and
    $command -match '\|\s*(grep|rg|awk|sed|head|tail|less|more)\b') {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — avoid grepping saved Claude tool-result files.

Filtering tool-result files rehydrates previous large outputs and wastes context.
Use the CK command with a narrower pattern instead:

  ck expand-folder --pattern "<keyword>" <folder>
  ck get-method-source <file> <MemberName>
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 1: ck find-files piped through content-filtering tools
# Allow: head, wc (truncation/counting). Block: grep/tail/sort/awk/sed/cut (filter scored results).
$hasCk = $command -match 'ck\s+find-files\b'
$hasPipe = $command -match '\|\s*(tail|grep|sort|awk|sed|cut|less|more)\b'

if ($hasCk -and $hasPipe) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — avoid piping ck find-files through grep/sort/awk.

ck find-files output is already ranked by relevance score. Filtering or sorting
destroys that structure. Instead:

  - Reduce output with --top <n> or --min-score <f>

Remove the pipe and re-run the ck command directly.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 1b: ck expand-folder piped through filters/truncation
$hasExpand = $command -match 'ck\s+expand-folder\b'
$hasExpandPipe = $command -match '\|\s*(head|tail|grep|rg|sort|awk|sed|cut|less|more|wc)\b'

if ($hasExpand -and $hasExpandPipe) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — avoid piping ck expand-folder output.

ck expand-folder now refuses broad output and provides keyword hints. Filtering
or truncating the output hides that guidance and wastes context. Rerun directly
with a more precise pattern:

  ck expand-folder --pattern "<provider>|<workflow>|<symbol>" <folder>
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 2: dotnet build output piped through filters
if ($command -match '(^|[;&|\s])dotnet\s+build\b' -and
    $command -match '\|\s*(tail|grep|sed|awk|head)\b') {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — dotnet build output is being post-filtered.

Use compact build diagnostics directly:

  ck build-check <project.csproj>

This runs dotnet build -v q and emits concise error/warning summaries without tail/grep churn.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Prefer ck build-check for normal verification loops to avoid duplicate
# dotnet-build + build-check churn. Raw dotnet build remains available as an
# explicit fallback by prefixing with CK_ALLOW_RAW_BUILD=1.
if ($command -match '(^|[;&|\s])dotnet\s+build\b' -and
    $command -notmatch 'CK_ALLOW_RAW_BUILD=1') {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — prefer ck build-check as the default verification command.

Raw dotnet build often creates duplicate verification loops. Prefer:

  ck build-check <project.csproj>

If you explicitly need full MSBuild output (fallback only), rerun once with:

  CK_ALLOW_RAW_BUILD=1 dotnet build <project.csproj> -v q
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 2.2: repeated grep/find loop on same token family
if ($command -match '(^|[;&|\s])(grep|rg|find)\b') {
    $tokenFamily = ExtractSearchTokenFamily $command
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $recentToken = [string]($state.recentSearchToken ?? "")
    $recentCount = [int]($state.recentSearchCount ?? 0)
    $recentFirst = [long]($state.recentSearchFirstTs ?? 0)
    if ($tokenFamily -and
        $tokenFamily -eq $recentToken -and
        $recentCount -ge 4 -and
        (($now - $recentFirst) -le 90)) {
        @{
            hookSpecificOutput = @{
                hookEventName = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = @"
[ck-guard] BLOCKED — repeated grep/find loop on token family '$tokenFamily'.

Switch to targeted symbol search instead:

  ck find-symbol "$tokenFamily"
  ck refs "$tokenFamily"

This avoids repeated broad text search churn.
"@
            }
        } | ConvertTo-Json -Depth 3
        exit 0
    }
}

# Pattern 2.4: source search requires established boundaries
$isSourceSearch = $command -match '(^|[;&|\s])(grep|rg|find)\b' -and
                  $command -match '(^|\s)(src|\./src|src/Modules|src/Hosts)(/)?(\s|$)|\.(cs|ts|tsx)\b|--include=.*\.(cs|ts|tsx)'
if (($scopedFolders.Count -eq 0) -and $isSourceSearch) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — source search works better with file-first boundaries.

Before grep/glob/find-style searching, run:
  ck get-keyword-map --query "<domain concept operation>"
  ck find-files --query "<domain concept operation>" --path src/

If results are weak/noisy, fallback to:
  ck get-keyword-map --query "<domain concept operation>"
  ck find-files --query "<refined query from keyword-map>"

Then keep searches inside returned boundaries.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 2.6: repeated identical build-check with no workspace change
if ($command -match 'ck\s+build-check\b') {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $lastBuild = [string]($state.lastBuildCheckCommand ?? "")
    $lastBuildTs = [long]($state.lastBuildCheckTs ?? 0)
    $lastBuildTree = [string]($state.lastBuildCheckTree ?? "")
    $currentTree = GetGitTreeFingerprint $repoRoot
    if ($lastBuild -and
        $command -eq $lastBuild -and
        $currentTree -eq $lastBuildTree -and
        (($now - $lastBuildTs) -le 45)) {
        @{
            hookSpecificOutput = @{
                hookEventName = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = @"
[ck-guard] BLOCKED — repeated identical ck build-check with no workspace change.

Prefer delta verification:

  ck build-check --delta <project.csproj>

or continue coding before rerunning build-check.
"@
            }
        } | ConvertTo-Json -Depth 3
        exit 0
    }
}

# Pattern 2.5: strict boundary lock when boundaries exist
if ($scopedFolders.Count -gt 0) {
    if ($command -match 'ck\s+signatures\b') {
        foreach ($path in (ExtractCommandPaths $command)) {
            if (-not (IsWithinScopedFolders $path $scopedFolders)) {
                @{
                    hookSpecificOutput = @{
                        hookEventName = 'PreToolUse'
                        permissionDecision = 'deny'
                        permissionDecisionReason = @"
[ck-guard] BLOCKED — ck signatures path is outside current CK boundaries.

Active boundaries were set by latest ck find-files. Keep signatures inside:
  $($scopedFolders -join ' ')

If direction changed, run:
  ck get-keyword-map --query "<new direction>"
  ck find-files --query "<new direction>"
"@
                    }
                } | ConvertTo-Json -Depth 3
                exit 0
            }
        }
    }

    if ($command -match '(^|[;&|\s])(grep|rg|find)\b') {
        foreach ($path in (ExtractCommandPaths $command)) {
            if (-not (IsWithinScopedFolders $path $scopedFolders)) {
                @{
                    hookSpecificOutput = @{
                        hookEventName = 'PreToolUse'
                        permissionDecision = 'deny'
                        permissionDecisionReason = @"
[ck-guard] BLOCKED — source search path is outside current CK boundaries.

Keep grep/rg/find inside boundaries from latest ck find-files:
  $($scopedFolders -join ' ')

If this is a new direction, refresh scope first:
  ck get-keyword-map --query "<new direction>"
  ck find-files --query "<new direction>"
"@
                    }
                } | ConvertTo-Json -Depth 3
                exit 0
            }
        }
    }
}

# Pattern 3a: broad recursive grep/rg from source/module roots
$isBroadRecursiveGrep = $command -match '(^|[;&|\s])(grep|rg)\b' -and
                        $command -match '(^|\s)-[A-Za-z]*r[A-Za-z]*\b|\b-rn\b|\b--recursive\b|\brg\b' -and
                        $command -match '(^|\s)(src|\./src|src/Modules|src/Modules/[^/\s]+|src/Hosts|src/Hosts/[^/\s]+)(/)?(\s|$)' -and
                        $command -match '(--include=.*\.(cs|ts|tsx)|\.(cs|ts|tsx)\b|grep|rg)'

if ($isBroadRecursiveGrep) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — broad recursive grep over source/module root may be noisy.

Recursive grep from src/ or a module root scans too much. Use CK to narrow first:

  ck find-files --query "<domain concept operation>" --explain
  ck expand-folder --pattern "<keyword>" <returned-folder>

If you already have focused folders, grep only those exact folders.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 3b: broad source-tree find used as manual navigation
$isBroadSourceFind = $command -match '\bfind\s+([^|;]*\s)?(src|\./src|src/Modules|src/Hosts)(\s|/)' -and
                     $command -match '(-name\s+|--name\s+|\-type\s+[fd])'

if ($isBroadSourceFind) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] ALLOW (guidance) — broad find over source folders may flood context.

Plain find across src/ returns unranked paths and often floods context. Use:

  ck find-files --query "<domain concept operation>"
  ck expand-folder --pattern "<keyword>" <returned-folder>

If you already know the exact narrow folder, run find inside that folder only.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 4: find -exec cat / xargs cat (bulk file read) — block, use ck tools
$isFindExecCat = $command -match '\bfind\b' -and ($command -match '-exec\s+cat\b' -or $command -match '\|\s*xargs\s+cat\b')

if ($isFindExecCat) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — use ck tools instead of find -exec cat.

Bulk-reading source files via find bypasses targeted reads. Use:

  ck signatures <folder>/              # list all members in a folder
  ck get-method-source <file> <Name>   # read one method

These return structured output with exact line spans.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}
