<#
.SYNOPSIS
    Builds the release artifacts: a published zip and a matching checksums file.

.DESCRIPTION
    Publishes ApplicationPackagerUI as a self-contained, single-file win-x64 Release build
    into a stage folder, then zips the stage into <OutputRoot>\AppPackagerDesktop-<version>.zip
    and writes checksums.txt beside it.

    The two halves run separately when something has to happen to the published files
    in between, which is how the release workflow signs them: -PublishOnly leaves the
    stage in place, -PackageOnly zips an existing stage.

    The version comes from <Version> in the UI csproj. Nothing is uploaded; releases are
    created from the artifacts this produces.

.PARAMETER OutputRoot
    Directory that receives the zip and checksums.txt. Defaults to <repo>\publish.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER PublishOnly
    Publish into <OutputRoot>\release-stage and stop; the stage is kept.

.PARAMETER PackageOnly
    Zip the existing <OutputRoot>\release-stage without publishing; the stage is removed.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$Configuration = 'Release',
    [switch]$PublishOnly,
    [switch]$PackageOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PublishOnly -and $PackageOnly) { throw '-PublishOnly and -PackageOnly cannot be combined.' }

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

$zipPath = Join-Path $OutputRoot "AppPackagerDesktop-$version.zip"
$checksumPath = Join-Path $OutputRoot 'checksums.txt'
$exePath = Join-Path $stageDir 'ApplicationPackagerUI.exe'

Write-Host "App Packager Desktop $version -> $OutputRoot"

if (-not $PackageOnly) {
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

    if (-not (Test-Path $exePath)) { throw "Published executable missing: $exePath" }

    if ($PublishOnly) {
        Write-Host ''
        Write-Host "stage      : $stageDir"
        Get-ChildItem -LiteralPath $stageDir -File -Recurse | ForEach-Object {
            Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($stageDir.Length + 1))
        }
        return
    }
}

if (-not (Test-Path $exePath)) { throw "No published stage to package at $stageDir; run without -PackageOnly first." }

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
