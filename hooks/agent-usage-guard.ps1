# agent-usage-guard.ps1: PreToolUse hook for the Agent tool (Windows PowerShell).
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$inputText = [Console]::In.ReadToEnd()

try {
    $json = $inputText | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$toolInput = if ($json.tool_input) { $json.tool_input } else { $json.toolInput }
$prompt    = $toolInput.prompt ?? ""
$desc      = $toolInput.description ?? ""
$combined  = "$prompt`n$desc"

# Only act when the agent appears to be doing code search/navigation
if ($combined -notmatch '(?i)(\.(cs|tsx?)\b|find|search|explore|scope|controller|component|service|repository|business|layer|interface|namespace|module|handler|route|routing)') {
    exit 0
}

# Find the protocol file relative to this hook (.claude/hooks/ → .claude/rules/)
$protocolFile = Join-Path $PSScriptRoot "..\rules\ck-code-search-protocol.md"

if (-not (Test-Path $protocolFile)) {
    # Protocol not found — fall back to a plain guidance message
    @{
        hookSpecificOutput = @{
            hookEventName            = "PreToolUse"
            permissionDecision       = "allow"
            permissionDecisionReason = "[ck-guard] Use ck find-scope before delegating navigation to a sub-agent."
        }
    } | ConvertTo-Json -Depth 5
    exit 0
}

$protocol = Get-Content $protocolFile -Raw

$modifiedPrompt = @"
The following code search protocol is mandatory for this task:

$protocol

The ck binary is at: .claude\skills\ck\ck.cmd (Windows) or .claude/skills/ck/ck (Mac/Linux/Git Bash)

---

$prompt
"@

# Deep-clone the tool input and replace the prompt
$updatedInput = $toolInput | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$updatedInput.prompt = $modifiedPrompt

@{
    hookSpecificOutput = @{
        hookEventName            = "PreToolUse"
        permissionDecision       = "allow"
        permissionDecisionReason = "[ck-guard] CK code search protocol injected into sub-agent prompt — sub-agent will use ck tools instead of broad searches."
        updatedInput             = $updatedInput
    }
} | ConvertTo-Json -Depth 10
