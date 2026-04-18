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

# Pattern 1: ck search/find-scope with real shell pipe (not regex \|)
# Allow pipes on signatures and get-method-source (filtering large output is fine).
$hasCk = $command -match 'ck\s+(search|find-scope)\b'
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
  - Use --type and --name for precise matching:
    ck search --query "<scope>" --type class --name "ClassName"
  - Types: class, method, member, file

Remove the pipe and re-run the ck command directly.
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}

# Pattern 2: raw grep on source files
if ($command -match '(^|\s)(grep|rg)\s+.*\.(cs|ts|tsx)\b') {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] bash grep on source files detected.

Do NOT use bash grep to search this codebase — follow the code search protocol:

  1. .claude/skills/ck/ck find-scope --query "<module, concept, operation, type>"
  2. .claude/skills/ck/ck signatures <file.cs> [file2.cs ...]
  3. .claude/skills/ck/ck get-method-source <file.cs> <MemberName>
"@
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}
