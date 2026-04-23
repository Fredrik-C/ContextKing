#!/usr/bin/env pwsh
# ck-bash-guard: PreToolUse hook for the Bash tool (PowerShell version).
# Detects piped ck output and raw grep on source files.
# Piped ck output is BLOCKED; raw grep is allowed with a hint.

$ErrorActionPreference = 'SilentlyContinue'

$raw = $input | Out-String
if (-not $raw) { exit 0 }

try { $obj = $raw | ConvertFrom-Json } catch { exit 0 }

$command = if ($obj.tool_input.command) { $obj.tool_input.command }
           elseif ($obj.toolInput.command) { $obj.toolInput.command }
           else { '' }

if (-not $command) { exit 0 }

# Pattern 1: ck find-scope with real shell pipe (not regex \|)
# Allow pipes on signatures and get-method-source (filtering large output is fine).
$hasCk = $command -match 'ck\s+find-scope\b'
$hasPipe = $command -match '\|\s*(head|tail|grep|wc|sort|awk|sed|cut|less|more)\b'

if ($hasCk -and $hasPipe) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — do not pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  - Reduce output with --top <n> or --min-score <f>

Remove the pipe and re-run the ck command directly.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 2: raw grep on a specific source file (not -r) — block, use AST tools
$isGrepSpecificFile = $command -match '(^|\s)(grep|rg)\s+.*[^/]\.(cs|ts|tsx)\b' -and $command -notmatch '(grep|rg)\s+-[a-zA-Z]*r'

if ($isGrepSpecificFile) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — use CK AST tools instead of grep on a known file.

You already know the file. Use:

  .claude/skills/ck/ck signatures <file.cs>              # list all members
  .claude/skills/ck/ck get-method-source <file.cs> <Name> # read one method

These return structured output with exact line spans. grep loses context.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 3: find piped to xargs grep — block, use ck find-scope + expand-folder
$isFindXargsGrep = $command -match '\bfind\b' -and $command -match '\|\s*xargs\s+(grep|rg)\b'

if ($isFindXargsGrep) {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = @"
[ck-guard] BLOCKED — use ck tools instead of find | xargs grep.

find | xargs grep is manual file discovery. Use CK instead:

  .claude/skills/ck/ck find-scope --query "<what you are looking for>"
  .claude/skills/ck/ck expand-folder --pattern "<keyword>" <folder>

This returns ranked, scoped results without reading every file.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}
