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

if (-not $filePath -or -not $filePath.EndsWith('.cs')) {
    exit 0
}

$baseName = Split-Path $filePath -Leaf
$reason = "[ck-guard] Reading a C# source file: '$baseName'.
If you are evaluating multiple candidate files, prefer:
  .claude\skills\ck\ck.cmd signatures <filepath>
This lists all method signatures via live AST parsing without reading the full file.
Proceed with Read when this is the specific file you need."

@{
    hookSpecificOutput = @{
        hookEventName         = "PreToolUse"
        permissionDecision    = "allow"
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 5
