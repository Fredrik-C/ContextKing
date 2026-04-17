# ck-search-guard.ps1: PreToolUse hook for Glob and Grep tools (Windows PowerShell).
# When searching for source files (.cs, .ts, .tsx) across a wide, unscoped path,
# reminds the agent to run ck find-scope first. Never blocks the search.
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

    if (-not $pattern -or $pattern -notmatch '\.(cs|tsx?)') { exit 0 }

    $depth = if ($searchPath) { ($searchPath -split '[/\\]' | Where-Object { $_ -ne '' }).Count } else { 0 }
    if ($depth -gt 3) { exit 0 }

    $displayPath = if ($searchPath) { $searchPath } else { "(repo root)" }
    $reason = "[ck-guard] Broad source file glob detected (pattern: '$pattern', path: '$displayPath').

Use ck search to find what you need with semantic scoping:
  .claude\skills\ck\ck.cmd search --query `"<domain description>`" --pattern `"<keyword>`"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  .claude\skills\ck\ck.cmd find-scope --query `"<multi-keyword description>`"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files."

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

    $isSource = ($glob -match '\.(cs|tsx?)') -or ($type -match '^(cs|tsx?)$')
    if (-not $isSource) { exit 0 }

    $depth = if ($searchPath) { ($searchPath -split '[/\\]' | Where-Object { $_ -ne '' }).Count } else { 0 }
    if ($depth -gt 3) { exit 0 }

    $displayPath = if ($searchPath) { $searchPath } else { "(repo root)" }
    $reason = "[ck-guard] Broad source file grep detected (path: '$displayPath').

Use ck search to find what you need with semantic scoping:
  .claude\skills\ck\ck.cmd search --query `"<domain description>`" --pattern `"<keyword>`"

If you don't have a keyword yet, use ck find-scope to discover the right area:
  .claude\skills\ck\ck.cmd find-scope --query `"<multi-keyword description>`"

Do NOT use broad glob/grep — it wastes tokens scanning irrelevant files."

    @{
        hookSpecificOutput = @{
            hookEventName            = "PreToolUse"
            permissionDecision       = "allow"
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5
    exit 0
}
