# Install the pinned model from a release bundle or download it for source installs.
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.ck/models/code-reranker'),
    [string]$SourceDirectory
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -eq
    [IO.Path]::GetPathRoot($destinationPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
    throw 'The model destination must not be a filesystem root.'
}
if ((Test-Path -LiteralPath $destinationPath) -and
    !(Test-Path -LiteralPath (Join-Path $destinationPath 'manifest.json')) -and
    @(Get-ChildItem -LiteralPath $destinationPath -Force).Count -gt 0) {
    throw 'Refusing to replace a non-empty directory without a model manifest.'
}
$manifestPath = Join-Path $PSScriptRoot '../models/code-reranker/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$revision = 'e74f446dc6e67e29fcee77213472c142f73a6bbb'
$baseUrl = "https://huggingface.co/mrsladoje/CodeRankEmbed-onnx-int8/resolve/$revision"

if (Test-Path -LiteralPath $destinationPath) {
    $matchesPack = $true
    foreach ($asset in $manifest.files.PSObject.Properties) {
        $path = Join-Path $destinationPath $asset.Name
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $asset.Value) {
            $matchesPack = $false
            break
        }
    }
    if ($matchesPack) {
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destinationPath 'manifest.json') -Force
        Write-Host "Verified existing code model (offline): $destinationPath"
        return
    }
}

$parentPath = [IO.Path]::GetDirectoryName($destinationPath)
[IO.Directory]::CreateDirectory($parentPath) | Out-Null
$stagePath = Join-Path $parentPath ('.ck-code-model-download-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stagePath) | Out-Null
$useBundle = $SourceDirectory -and (Test-Path -LiteralPath (Join-Path $SourceDirectory 'model.onnx'))
if ($useBundle) { Write-Host 'Installing bundled CodeRankEmbed INT8 model (MIT), verifying SHA-256 checksums.' }
else { Write-Host 'Downloading CodeRankEmbed INT8 pack (~139 MB), licensed MIT. No source code is uploaded.' }
try {
    foreach ($asset in $manifest.files.PSObject.Properties) {
        $remoteName = if ($asset.Name -eq 'model.onnx') { 'onnx/model.onnx' } else { $asset.Name }
        $target = Join-Path $stagePath $asset.Name
        if ($useBundle) { Copy-Item -LiteralPath (Join-Path $SourceDirectory $asset.Name) -Destination $target }
        else { Invoke-WebRequest -Uri "$baseUrl/$remoteName" -OutFile $target }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $asset.Value) {
            throw "Checksum mismatch: $($asset.Name)"
        }
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stagePath 'manifest.json')
    $manifest.files.PSObject.Properties | ForEach-Object { "$($_.Value)  $($_.Name)" } |
        Set-Content -LiteralPath (Join-Path $stagePath 'SHA256SUMS') -Encoding utf8NoBOM
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../models/code-reranker/README.md') -Destination (Join-Path $stagePath 'README.md')
    if (Test-Path -LiteralPath $destinationPath) {
        $backupPath = $destinationPath + '.backup-' + [Guid]::NewGuid().ToString('N')
        [IO.Directory]::Move($destinationPath, $backupPath)
        Write-Host "Previous model retained at $backupPath"
    }
    [IO.Directory]::Move($stagePath, $destinationPath)
    Write-Host "Installed code model: $destinationPath"
} catch {
    # Preserve a failed download for diagnosis; never overwrite or delete an installed model.
    Write-Warning "Incomplete download retained at $stagePath"
    throw
}
