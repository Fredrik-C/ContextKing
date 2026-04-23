#!/usr/bin/env pwsh
# ck-search-guard: PreToolUse hook for the built-in Grep and Glob tools (PowerShell version).
# Blocks broad source-file searches that should go through ck find-scope first.

$ErrorActionPreference = 'SilentlyContinue'

$raw = $input | Out-String
if (-not $raw) { exit 0 }

try { $obj = $raw | ConvertFrom-Json } catch { exit 0 }

$tool = $obj.tool_name
if (-not $tool) { exit 0 }
if ($tool -ne 'Grep' -and $tool -ne 'Glob') { exit 0 }

$denyMsg = @"
[ck-guard] BLOCKED — run ck find-scope before searching source files.

You are trying to search .cs/.ts/.tsx files across a broad path.
Use CK to scope and explore instead:

  .claude/skills/ck/ck find-scope --query "<what you are looking for>"
  .claude/skills/ck/ck expand-folder --pattern "<keyword>" <returned-folder>

find-scope ranks folders semantically. expand-folder filters files and signatures
within a folder by keyword. Together they replace broad Grep/Glob discovery.
"@

# Glob: **/*.cs|ts|tsx
if ($tool -eq 'Glob') {
    $pattern = $obj.tool_input.pattern
    if ($pattern -match '\*\*?/\*\.(cs|ts|tsx)$') {
        @{
            hookSpecificOutput = @{
                hookEventName      = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = $denyMsg
            }
        } | ConvertTo-Json -Depth 3
        exit 0
    }
}

# Grep: include targets source files
if ($tool -eq 'Grep') {
    $include = $obj.tool_input.include
    if ($include -match '\*\.(cs|ts|tsx)$') {
        @{
            hookSpecificOutput = @{
                hookEventName      = 'PreToolUse'
                permissionDecision = 'deny'
                permissionDecisionReason = $denyMsg
            }
        } | ConvertTo-Json -Depth 3
        exit 0
    }
}
