<#
.SYNOPSIS
    Builds the Packr release artifacts: a published zip and a matching checksums file.

.DESCRIPTION
    Publishes ApplicationPackagerUI as a self-contained, single-file win-x64 Release build,
    then writes publish\Packr-<version>.zip and publish\checksums.txt.

    The version comes from <Version> in the UI csproj. Nothing is uploaded; releases are
    created separately from the artifacts this produces.

.PARAMETER OutputRoot
    Directory that receives the zip and checksums.txt. Defaults to <repo>\publish.

.PARAMETER Configuration
    Build configuration. Defaults to Release.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\ApplicationPackagerUI\ApplicationPackagerUI.csproj'
if (-not (Test-Path $projectPath)) {
    throw "UI project not found at $projectPath"
}

if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'publish' }
$stageDir = Join-Path $OutputRoot 'release-stage'

[xml]$projectXml = Get-Content -LiteralPath $projectPath
$version = ($projectXml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) {
    throw "No <Version> element in $projectPath. Release artifacts need a version."
}
$version = "$version".Trim()

$zipPath = Join-Path $OutputRoot "Packr-$version.zip"
$checksumPath = Join-Path $OutputRoot 'checksums.txt'

Write-Host "Packr $version -> $OutputRoot"

if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $stageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Symbols are build output, not release content.
Get-ChildItem -LiteralPath $stageDir -Filter *.pdb -Recurse | Remove-Item -Force

$exePath = Join-Path $stageDir 'ApplicationPackagerUI.exe'
if (-not (Test-Path $exePath)) { throw "Published executable missing: $exePath" }

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

# Bare filename in the checksum line so `sha256sum -c` runs from the download directory.
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Remove-Item -LiteralPath $stageDir -Recurse -Force

$zipItem = Get-Item -LiteralPath $zipPath
Write-Host ''
Write-Host ("zip        : {0} ({1:N1} MB)" -f $zipItem.Name, ($zipItem.Length / 1MB))
Write-Host ("checksums  : {0}" -f (Split-Path -Leaf $checksumPath))
Write-Host ("sha256     : {0}" -f $hash)
