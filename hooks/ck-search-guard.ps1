#!/usr/bin/env pwsh
# ck-search-guard: PreToolUse hook for the built-in Grep and Glob tools (PowerShell version).
# Enforces CK search prerequisites for source-file Grep/Glob:
# keyword-map must be created and scope must be established first.
#
$ErrorActionPreference = 'SilentlyContinue'

$raw = $input | Out-String
if (-not $raw) { exit 0 }

try { $obj = $raw | ConvertFrom-Json } catch { exit 0 }

$tool = $obj.tool_name
if (-not $tool) { exit 0 }
if ($tool -ne 'Grep' -and $tool -ne 'Glob') { exit 0 }

$denyMsg = @"
[ck-guard] ALLOW (guidance) — source search works better with keyword-map and scope.

Before Grep/Glob searching in source files, run:

  ck get-keyword-map --query "<what you are looking for>"
  ck find-scope --query "<what you are looking for>"
  ck expand-folder --pattern "<keyword>" <returned-folder>

Then keep Grep/Glob paths inside folders returned by find-scope.
"@

$repoRoot = (& git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) { $repoRoot = (Get-Location).Path }
if (-not (Test-Path (Join-Path $repoRoot '.ck.json'))) { exit 0 }

$stateFile = Join-Path $repoRoot ".ck-index/.ck-guard-state.json"
$scopedFolders = @()
$keywordMapSeen = $false
if (Test-Path $stateFile) {
    try {
        $state = Get-Content $stateFile -Raw | ConvertFrom-Json
        if ($state.scopedFolders) { $scopedFolders = @($state.scopedFolders) }
        if ($state.keywordMapSeen -eq $true) { $keywordMapSeen = $true }
    } catch { }
}

function NormalizePath([string]$path) {
    if (-not $path) { return "" }
    return $path.TrimStart('./').TrimEnd('/')
}

function IsWithinScopedFolders([string]$path, [string[]]$folders) {
    $norm = NormalizePath $path
    if (-not $norm) { return $false }
    foreach ($folder in $folders) {
        $f = NormalizePath ([string]$folder)
        if (-not $f) { continue }
        if ($norm -eq $f -or $norm.StartsWith("$f/")) { return $true }
    }
    return $false
}

# Extract static prefix of a glob pattern (before first wildcard)
function GlobStaticPrefix([string]$pattern) {
    $idx = $pattern.IndexOfAny([char[]]@('*','?','{','['))
    $prefix = if ($idx -lt 0) { $pattern } else { $pattern.Substring(0, $idx) }
    return $prefix.TrimEnd('/')
}

# Glob: source file pattern without a narrow static prefix
if ($tool -eq 'Glob') {
    $pattern = [string]$obj.tool_input.pattern
    if ($pattern -match '\.(cs|ts|tsx)$') {
        $prefix  = GlobStaticPrefix $pattern
        $pathArg = [string]$obj.tool_input.path
        if (-not $keywordMapSeen -or $scopedFolders.Count -eq 0) {
            @{
                    hookSpecificOutput = @{
                        hookEventName      = 'PreToolUse'
                        permissionDecision = 'allow'
                        permissionDecisionReason = $denyMsg
                    }
                } | ConvertTo-Json -Depth 3
            exit 0
        }
        if ($scopedFolders.Count -gt 0) {
            $target = if ($pathArg) { $pathArg } else { $prefix }
            if (-not (IsWithinScopedFolders $target $scopedFolders)) {
                @{
                    hookSpecificOutput = @{
                        hookEventName      = 'PreToolUse'
                        permissionDecision = 'allow'
                        permissionDecisionReason = '[ck-guard] ALLOW (guidance) — Glob path is outside current scoped folders from ck find-scope.'
                    }
                } | ConvertTo-Json -Depth 3
                exit 0
            }
        }
    }
}

# Grep: source file include without a narrow path
if ($tool -eq 'Grep') {
    $include = [string]$obj.tool_input.include
    if ($include -match '\*\.(cs|ts|tsx)$') {
        $pathArg = if ($obj.tool_input.path) { [string]$obj.tool_input.path }
                   elseif ($obj.tool_input.cwd) { [string]$obj.tool_input.cwd }
                   else { '' }
        if (-not $keywordMapSeen -or $scopedFolders.Count -eq 0) {
            @{
                    hookSpecificOutput = @{
                        hookEventName      = 'PreToolUse'
                        permissionDecision = 'allow'
                        permissionDecisionReason = $denyMsg
                    }
                } | ConvertTo-Json -Depth 3
            exit 0
        }
        if ($scopedFolders.Count -gt 0) {
            if (-not (IsWithinScopedFolders $pathArg $scopedFolders)) {
                @{
                    hookSpecificOutput = @{
                        hookEventName      = 'PreToolUse'
                        permissionDecision = 'allow'
                        permissionDecisionReason = '[ck-guard] ALLOW (guidance) — Grep path is outside current scoped folders from ck find-scope.'
                    }
                } | ConvertTo-Json -Depth 3
                exit 0
            }
        }
    }
}
