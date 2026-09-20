[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dataset,
    [Parameter(Mandatory)][string]$CommandPath,
    [Parameter(Mandatory)][string]$Output,
    [int]$CandidateTop = 50
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Dataset)) { throw "Dataset not found: $Dataset" }
if (!(Test-Path -LiteralPath $CommandPath)) { throw "CLI not found: $CommandPath" }

function Invoke-Run([object]$record) {
    $arguments = @('find-files', $record.query, '--task', $record.task, '--top', [string]$record.top, '--explain', '--verbose')
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $CommandPath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($arg in $arguments) { [void]$psi.ArgumentList.Add([string]$arg) }
    $process = [Diagnostics.Process]::Start($psi)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $watch.Stop()
    if ($process.ExitCode -ne 0) { throw "ck failed for $($record.id): $stderr" }
    $rows = @($stdout -split "`r?`n" | Where-Object { $_ -match '^[^`t]+`t[^`t]+$' -or $_ -match '^[^`t]+`t[^`t]+`t' })
    $paths = @($rows | ForEach-Object { ($_ -split "`t", 3)[1] } | ForEach-Object { $_.Replace('\','/') })
    $grades = @{}
    foreach ($item in $record.relevant) { $grades[$item.path.Replace('\','/')] = [int]$item.grade }
    $firstRelevant = $null
    for ($i = 0; $i -lt $paths.Count; $i++) { if ($grades.ContainsKey($paths[$i])) { $firstRelevant = $i + 1; break } }
    $relevant5 = @($paths | Select-Object -First 5 | Where-Object { $grades.ContainsKey($_) }).Count
    $relevant10 = @($paths | Select-Object -First 10 | Where-Object { $grades.ContainsKey($_) }).Count
    $dcg = 0.0; $idcg = 0.0
    for ($i = 0; $i -lt [Math]::Min(10,$paths.Count); $i++) { if ($grades.ContainsKey($paths[$i])) { $dcg += ($grades[$paths[$i]] / [Math]::Log(2 + $i, 2)) } }
    $idealPosition = 0
    foreach ($grade in @($grades.Values | Sort-Object -Descending | Select-Object -First 10)) { $idcg += ($grade / [Math]::Log(2 + $idealPosition, 2)); $idealPosition++ }
    [pscustomobject]@{ id=$record.id; split=$record.split; recall5=([double]$relevant5 / [Math]::Max(1,@($record.relevant).Count)); recall10=([double]$relevant10 / [Math]::Max(1,@($record.relevant).Count)); mrr=if ($null -eq $firstRelevant) { 0 } else { 1.0/$firstRelevant }; ndcg10=if ($idcg -eq 0) { 0 } else { $dcg/$idcg }; first=$firstRelevant; latencyMs=$watch.ElapsedMilliseconds; exitCode=$process.ExitCode; stderr=$stderr }
}

$records = @(Get-Content -LiteralPath $Dataset | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$results = @($records | ForEach-Object { Invoke-Run $_ })
$report = [pscustomobject]@{ generatedAt=(Get-Date).ToUniversalTime().ToString('o'); dataset=$Dataset; command=$CommandPath; count=$results.Count; metrics=[pscustomobject]@{ recall5=($results.recall5 | Measure-Object -Average).Average; recall10=($results.recall10 | Measure-Object -Average).Average; mrr=($results.mrr | Measure-Object -Average).Average; ndcg10=($results.ndcg10 | Measure-Object -Average).Average }; tasks=$results }
$parent = Split-Path -Parent $Output; if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
Write-Output ("Evaluated {0} tasks. Recall@5={1:P2}; MRR={2:P2}; report={3}" -f $results.Count,$report.metrics.recall5,$report.metrics.mrr,$Output)
