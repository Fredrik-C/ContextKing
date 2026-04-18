# ck-update-check.ps1 — SessionStart hook (Windows PowerShell).
# On Mac/Linux the .sh variant handles this — exit silently here.
if ($IsLinux -or $IsMacOS) { exit 0 }

$GithubOwner = 'Fredrik-C'
$GithubRepo  = 'ContextKing'
$CacheHours  = 24

# Find the ck binary relative to this hook
$ckBin = Join-Path $PSScriptRoot '..\skills\ck\ck.cmd'
if (-not (Test-Path $ckBin)) { exit 0 }

# Get installed version
try {
    $versionOutput = & $ckBin --version 2>$null
    $installed = ($versionOutput -replace '^ck\s+', '').Trim()
    if (-not $installed) { exit 0 }
} catch { exit 0 }

# Cache file
$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) { exit 0 }
$cacheDir = Join-Path $repoRoot '.ck-index'
if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null }
$cacheFile = Join-Path $cacheDir 'update-check'

# Check cache
if (Test-Path $cacheFile) {
    $cacheAge = (Get-Date) - (Get-Item $cacheFile).LastWriteTime
    if ($cacheAge.TotalHours -lt $CacheHours) {
        $cachedLatest = (Get-Content $cacheFile -Raw).Trim()
        if ($cachedLatest -and $cachedLatest -ne $installed) {
            Write-Output "[Context King] Update available: v${cachedLatest} (installed: v${installed}). Run: irm https://raw.githubusercontent.com/${GithubOwner}/${GithubRepo}/main/scripts/install.ps1 | iex"
        }
        exit 0
    }
}

# Query GitHub API
try {
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$GithubOwner/$GithubRepo/releases/latest" `
        -TimeoutSec 10 -ErrorAction Stop
    $latest = ($response.tag_name -replace '^v', '').Trim()
} catch {
    exit 0
}

# Cache result
if ($latest) { $latest | Set-Content $cacheFile -NoNewline }

# Compare and notify
if ($latest -and $latest -ne $installed) {
    Write-Output "[Context King] Update available: v${latest} (installed: v${installed}). Run: irm https://raw.githubusercontent.com/${GithubOwner}/${GithubRepo}/main/scripts/install.ps1 | iex"
}

exit 0
