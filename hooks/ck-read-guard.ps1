# ck-read-guard.ps1: PreToolUse hook for the Read tool (Windows PowerShell).
# When a .cs file is about to be read, emits a guidance hint suggesting
# 'ck signatures' for multi-file exploration. Never blocks the read.
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$input = [Console]::In.ReadToEnd()

try {
    $json = $input | ConvertFrom-Json -ErrorAction Stop
} catch {
    exit 0
}

$filePath = $json.tool_input.file_path `
         ?? $json.tool_input.path `
         ?? $json.toolInput.file_path `
         ?? $json.toolInput.path

if (-not $filePath -or -not ($filePath -match '\.(cs|tsx?)$')) {
    exit 0
}

$baseName = Split-Path $filePath -Leaf
$reason = "[ck-guard] STOP — you are about to read '$baseName' in full.

Do NOT read the entire file. Use targeted extraction instead:

  1. Run signatures first (if you haven't already):
     .claude\skills\ck\ck.cmd signatures $filePath

  2. Then extract only the method you need:
     .claude\skills\ck\ck.cmd get-method-source $filePath <MethodName>

Full file reads waste tokens. Read the whole file ONLY when you need 3+ methods from it.
If you have already confirmed via signatures that this file is relevant AND you need
multiple members, proceed with the Read."

@{
    hookSpecificOutput = @{
        hookEventName         = "PreToolUse"
        permissionDecision    = "allow"
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 5
