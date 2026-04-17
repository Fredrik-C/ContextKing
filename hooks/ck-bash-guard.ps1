#!/usr/bin/env pwsh
# ck-bash-guard: PreToolUse hook for the Bash tool (PowerShell version).
# Detects piping ck output and raw grep on source files.

$ErrorActionPreference = 'SilentlyContinue'

$raw = $input | Out-String
if (-not $raw) { exit 0 }

try { $obj = $raw | ConvertFrom-Json } catch { exit 0 }

$command = if ($obj.tool_input.command) { $obj.tool_input.command }
           elseif ($obj.toolInput.command) { $obj.toolInput.command }
           else { '' }

if (-not $command) { exit 0 }

# Pattern 1: ck commands piped
if ($command -match 'ck\s+(search|find-scope|signatures|get-method-source)\b.*\|') {
    @{
        hookSpecificOutput = @{
            hookEventName = 'PreToolUse'
            permissionDecision = 'allow'
            permissionDecisionReason = @"
[ck-guard] Do NOT pipe ck output through head, grep, or tail.

ck output is already structured and scoped. Piping discards folder scores and
grouping structure you need. Instead:

  - Reduce output with --top <n> or --min-score <f>
  - Use --type and --name for precise matching:
    ck search --query "<scope>" --type class --name "ClassName"
  - Types: class, method, member, file

Remove the pipe and run the ck command directly.
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
