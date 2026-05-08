# agent-usage-guard.ps1: SubagentStart hook (Windows PowerShell).
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$inputText = [Console]::In.ReadToEnd()

try {
    $json = $inputText | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

# Find the protocol file relative to this hook (.claude/hooks/ → .claude/rules/)
$protocolFile = Join-Path $PSScriptRoot "..\rules\ck-code-search-protocol.md"

if (-not (Test-Path $protocolFile)) {
    exit 0
}

$protocol = Get-Content $protocolFile -Raw

$context = @"
## Code Search Protocol (mandatory)

$protocol

The ck binary is at: ck
Use ck find-files first, then ck get-method-source/get-type-source on returned files. Use find-files/expand-folder only as fallback instead of broad grep/glob.
"@

@{
    additionalContext = $context
} | ConvertTo-Json -Depth 5
