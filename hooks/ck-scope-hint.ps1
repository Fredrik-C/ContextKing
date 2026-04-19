# ck-scope-hint: PostToolUse hook for the Bash tool (PowerShell).
#
# Fires after ck find-scope or ck search completes. Inspects the score column
# in the output and appends an additionalContext hint when the average gap
# between adjacent scores is <= 0.01 and scores are above the noise floor
# (0.70) — a signal that --top N sliced through a relevant cluster.
#
# Using avg_gap = spread / (count - 1) rather than a fixed spread threshold
# makes the check scale correctly with --top N.

if (-not (Get-Command jq -ErrorAction SilentlyContinue)) { exit 0 }

$input_json = $Input | Out-String

$tool = $input_json | jq -r '.tool_name // empty' 2>$null
if ($tool -ne "Bash") { exit 0 }

$command = $input_json | jq -r '.tool_input.command // empty' 2>$null
if ($command -notmatch 'ck\s+(find-scope|search)\b') { exit 0 }

$output = $input_json | jq -r '.tool_response.output // empty' 2>$null
if ([string]::IsNullOrEmpty($output)) { exit 0 }

# Parse score column from "<float>\t<folder-path>" lines
$scores = @()
foreach ($line in $output -split "`n") {
    if ($line -match '^([0-9]+\.[0-9]+)\t') {
        $scores += [double]$Matches[1]
    }
}

if ($scores.Count -lt 5) { exit 0 }

$maxScore = ($scores | Measure-Object -Maximum).Maximum
$minScore = ($scores | Measure-Object -Minimum).Minimum
$spread   = $maxScore - $minScore
$avgGap   = $spread / ($scores.Count - 1)

if ($avgGap -gt 0.01 -or $minScore -le 0.70) { exit 0 }

$suggested = [math]::Round($minScore - $avgGap, 2).ToString("F2")
$minFmt    = $minScore.ToString("F2")
$maxFmt    = $maxScore.ToString("F2")
$count     = $scores.Count

$hint = "[ck-hint] Scores are tightly clustered (${minFmt}-${maxFmt} across ${count} folders). The cutoff is likely mid-cluster - relevant folders may be missing. Re-run with --min-score ${suggested} to capture the full cluster."

@{
    hookSpecificOutput = @{
        hookEventName   = "PostToolUse"
        additionalContext = $hint
    }
} | ConvertTo-Json -Depth 3
