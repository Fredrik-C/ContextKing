# release.ps1
# Automates Context King GitHub releases on Windows/PowerShell:
# 1) validates clean state + auth
# 2) creates and pushes tag (vX.Y.Z)
# 3) waits for "Create release" workflow to complete
# 4) updates release notes after the release exists
#
# Usage:
#   .\scripts\release.ps1 --tag v1.7.3 --notes-file RELEASE_NOTES_v1.7.3.md
#   .\scripts\release.ps1 --notes-file RELEASE_NOTES_v1.7.3.md   # auto-increment latest tag

param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$RemainingArgs
)

$ErrorActionPreference = "Stop"

$Repo = "Fredrik-C/ContextKing"
$WorkflowName = "Create release"
$DefaultTimeoutSec = 1800
$PollSec = 10

$Tag = ""
$NotesFile = ""
$TimeoutSec = $DefaultTimeoutSec
$DryRun = $false

function Show-Usage {
  @"
Usage:
  .\scripts\release.ps1 [--tag vX.Y.Z] --notes-file <file> [--timeout-sec <n>] [--dry-run]

Options:
  --tag <vX.Y.Z>       Release tag. If omitted, auto-increments latest v* tag (patch bump).
  --notes-file <file>  Markdown file used to overwrite release notes after release creation.
  --timeout-sec <n>    Max wait for workflow/release creation (default: 1800).
  --dry-run            Print planned actions without mutating git/GitHub.
  -h, --help           Show this help.
"@ | Write-Host
}

function Stop-Release([string]$Message) {
  throw "Error: $Message"
}

function Require-Command([string]$CommandName) {
  if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
    Stop-Release "required command not found: $CommandName"
  }
}

function Invoke-Checked {
  param(
    [Parameter(Mandatory = $true)]
    [string]$CommandName,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
  )

  & $CommandName @CommandArgs
  if ($LASTEXITCODE -ne 0) {
    Stop-Release "command failed: $CommandName $($CommandArgs -join ' ')"
  }
}

function Invoke-Output {
  param(
    [Parameter(Mandatory = $true)]
    [string]$CommandName,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
  )

  $Output = & $CommandName @CommandArgs
  if ($LASTEXITCODE -ne 0) {
    Stop-Release "command failed: $CommandName $($CommandArgs -join ' ')"
  }

  return ($Output -join "`n")
}

function Test-CommandSuccess {
  param(
    [Parameter(Mandatory = $true)]
    [string]$CommandName,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
  )

  & $CommandName @CommandArgs *> $null
  return ($LASTEXITCODE -eq 0)
}

function Get-NextPatchTag {
  $Tags = & git tag --list "v*"
  if ($LASTEXITCODE -ne 0) {
    Stop-Release "command failed: git tag --list v*"
  }

  $Tags = @($Tags)
  if ($Tags.Count -eq 0) {
    return "v1.0.0"
  }

  $Latest = $Tags | Sort-Object { Get-VersionSortKey $_ } | Select-Object -Last 1
  if ($Latest -notmatch '^v(\d+)\.(\d+)\.(\d+)$') {
    Stop-Release "latest tag is not semver-like: $Latest"
  }

  $Version = [version]$Latest.Substring(1)
  return "v$($Version.Major).$($Version.Minor).$($Version.Build + 1)"
}

function Get-VersionSortKey([string]$VersionTag) {
  if ($VersionTag -match '^v(\d+)\.(\d+)\.(\d+)$') {
    return "0:{0:D10}.{1:D10}.{2:D10}" -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
  }

  return "1:$VersionTag"
}

function Wait-ForWorkflowRun {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [int]$MaxWaitSec
  )

  $StartedAt = Get-Date

  while ($true) {
    $RunId = & gh run list `
      --repo $Repo `
      --workflow $WorkflowName `
      --branch $ReleaseTag `
      --json databaseId `
      --jq '.[0].databaseId // empty'

    if ($LASTEXITCODE -ne 0) {
      Stop-Release "command failed: gh run list"
    }

    $RunId = ($RunId -join "`n").Trim()
    if ($RunId) {
      Write-Host "Found workflow run id: $RunId"
      Invoke-Checked gh run watch $RunId --repo $Repo --interval $PollSec
      return
    }

    $Elapsed = ((Get-Date) - $StartedAt).TotalSeconds
    if ($Elapsed -ge $MaxWaitSec) {
      Stop-Release "timed out waiting for workflow run for $ReleaseTag"
    }

    Start-Sleep -Seconds $PollSec
  }
}

function Wait-ForReleaseObject {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [int]$MaxWaitSec
  )

  $StartedAt = Get-Date

  while ($true) {
    if (Test-CommandSuccess gh release view $ReleaseTag --repo $Repo) {
      return
    }

    $Elapsed = ((Get-Date) - $StartedAt).TotalSeconds
    if ($Elapsed -ge $MaxWaitSec) {
      Stop-Release "timed out waiting for release object $ReleaseTag"
    }

    Start-Sleep -Seconds $PollSec
  }
}

for ($Index = 0; $Index -lt $RemainingArgs.Count; ) {
  switch ($RemainingArgs[$Index]) {
    "--tag" {
      if ($Index + 1 -ge $RemainingArgs.Count) { Stop-Release "--tag requires a value" }
      $Tag = $RemainingArgs[$Index + 1]
      $Index += 2
      continue
    }
    "--notes-file" {
      if ($Index + 1 -ge $RemainingArgs.Count) { Stop-Release "--notes-file requires a value" }
      $NotesFile = $RemainingArgs[$Index + 1]
      $Index += 2
      continue
    }
    "--timeout-sec" {
      if ($Index + 1 -ge $RemainingArgs.Count) { Stop-Release "--timeout-sec requires a value" }
      if (-not [int]::TryParse($RemainingArgs[$Index + 1], [ref]$TimeoutSec)) {
        Stop-Release "--timeout-sec must be an integer"
      }
      $Index += 2
      continue
    }
    "--dry-run" {
      $DryRun = $true
      $Index += 1
      continue
    }
    "-h" {
      Show-Usage
      exit 0
    }
    "--help" {
      Show-Usage
      exit 0
    }
    default {
      Write-Host "Error: unknown argument: $($RemainingArgs[$Index])" -ForegroundColor Red
      Show-Usage
      exit 1
    }
  }
}

Require-Command git
Require-Command gh

if (-not $NotesFile) {
  Stop-Release "--notes-file is required"
}

if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) {
  Stop-Release "notes file not found: $NotesFile"
}

if (-not $Tag) {
  $Tag = Get-NextPatchTag
}

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') {
  Stop-Release "tag must match vX.Y.Z (got: $Tag)"
}

$Status = Invoke-Output git status --porcelain
if ($Status) {
  Stop-Release "working tree is not clean. Commit/stash changes first."
}

if (-not (Test-CommandSuccess gh auth status)) {
  Stop-Release "GitHub CLI is not authenticated. Run: gh auth login"
}

if (Test-CommandSuccess git rev-parse $Tag) {
  Stop-Release "local tag already exists: $Tag"
}

if (Test-CommandSuccess git ls-remote --exit-code --tags origin "refs/tags/$Tag") {
  Stop-Release "remote tag already exists: $Tag"
}

Write-Host "Release plan:"
Write-Host "  repo:        $Repo"
Write-Host "  tag:         $Tag"
Write-Host "  notes file:  $NotesFile"
Write-Host "  timeout sec: $TimeoutSec"

if ($DryRun) {
  Write-Host "Dry-run enabled. No changes applied."
  exit 0
}

Invoke-Checked git fetch --tags origin
Invoke-Checked git checkout main
Invoke-Checked git pull --ff-only origin main

Invoke-Checked git tag $Tag
Invoke-Checked git push origin $Tag

Write-Host "Waiting for workflow: $WorkflowName ($Tag)..."
Wait-ForWorkflowRun -ReleaseTag $Tag -MaxWaitSec $TimeoutSec

Write-Host "Waiting for release object: $Tag..."
Wait-ForReleaseObject -ReleaseTag $Tag -MaxWaitSec $TimeoutSec

Write-Host "Updating release notes from: $NotesFile"
Invoke-Checked gh release edit $Tag `
  --repo $Repo `
  --title "Context King $Tag" `
  --notes-file $NotesFile

Write-Host "Done. Release updated:"
Invoke-Checked gh release view $Tag --repo $Repo
