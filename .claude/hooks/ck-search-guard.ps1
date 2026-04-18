# ck-search-guard.ps1: PreToolUse hook for Glob and Grep tools (Windows PowerShell).
# When searching for .cs files across a wide, unscoped path, reminds the agent
# to run ck find-scope first. Never blocks the search.
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$input = [Console]::In.ReadToEnd()

try {
    $json = $input | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$toolName = $json.tool_name ?? $json.toolName

# ── Glob ──────────────────────────────────────────────────────────────────────
if ($toolName -eq "Glob") {
    $pattern    = $json.tool_input.pattern    ?? $json.toolInput.pattern
    $searchPath = $json.tool_input.path       ?? $json.toolInput.path

    if (-not $pattern -or $pattern -notlike "*.cs*") { exit 0 }

    $depth = if ($searchPath) { ($searchPath -split '[/\\]' | Where-Object { $_ -ne '' }).Count } else { 0 }
    if ($depth -gt 3) { exit 0 }

    $displayPath = if ($searchPath) { $searchPath } else { "(repo root)" }
    $reason = "[ck-guard] Globbing for C# files across a wide path (pattern: '$pattern', path: '$displayPath').

Run ck find-scope FIRST to narrow scope:
  .claude\skills\ck\ck.cmd find-scope --query `"<multi-keyword description — module, concept, operation, type>`"

Then scope this search to the returned folder path.
Proceed only if the scope is already narrowed to a specific folder."

    @{
        hookSpecificOutput = @{
            hookEventName            = "PreToolUse"
            permissionDecision       = "allow"
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5
    exit 0
}

# ── Grep ──────────────────────────────────────────────────────────────────────
if ($toolName -eq "Grep") {
    $glob       = $json.tool_input.glob ?? $json.toolInput.glob
    $type       = $json.tool_input.type ?? $json.toolInput.type
    $searchPath = $json.tool_input.path ?? $json.toolInput.path

    $isCs = ($glob -like "*.cs*") -or ($type -eq "cs")
    if (-not $isCs) { exit 0 }

    $depth = if ($searchPath) { ($searchPath -split '[/\\]' | Where-Object { $_ -ne '' }).Count } else { 0 }
    if ($depth -gt 3) { exit 0 }

    $displayPath = if ($searchPath) { $searchPath } else { "(repo root)" }
    $reason = "[ck-guard] Grepping C# files across a wide path (path: '$displayPath').

Run ck find-scope FIRST to narrow scope:
  .claude\skills\ck\ck.cmd find-scope --query `"<multi-keyword description — module, concept, operation, type>`"

Then scope this Grep to the returned folder path.
Proceed only if the scope is already narrowed to a specific folder."

    @{
        hookSpecificOutput = @{
            hookEventName            = "PreToolUse"
            permissionDecision       = "allow"
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5
    exit 0
}
