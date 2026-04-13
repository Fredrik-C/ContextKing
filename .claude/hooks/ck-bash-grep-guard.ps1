# ck-bash-grep-guard.ps1: PreToolUse hook for the Bash tool (Windows PowerShell).
# When the command looks like a grep targeting .cs files, reminds the agent
# to follow the ck code-search protocol instead. Never blocks the command.
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$input = [Console]::In.ReadToEnd()

try {
    $json = $input | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$command = $json.tool_input.command ?? $json.toolInput.command
if (-not $command) { exit 0 }

# Only act when the command contains grep and references .cs files
if ($command -notmatch 'grep') { exit 0 }
if ($command -notmatch '\.cs|-r|-rn|--include.*cs') { exit 0 }

$reason = "[ck-guard] bash grep on C# files detected.

Do NOT use bash grep to search this codebase — follow the code search protocol:
  rules/ck-code-search-protocol.md

Short version:
  1. Run ck find-scope to locate the right folder
  2. Run ck signatures before reading any .cs file
  3. Use the Grep tool (not bash grep) only within the scoped folder"

@{
    hookSpecificOutput = @{
        hookEventName            = "PreToolUse"
        permissionDecision       = "allow"
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 5
