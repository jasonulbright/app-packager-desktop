<#
.SYNOPSIS
    Builds the release artifacts: a published zip, the installer and a checksums file.

.DESCRIPTION
    Publishes ApplicationPackagerUI and the command-line build (ApplicationPackagerBuild,
    AppPackagerBuild.exe) as framework-dependent, single-file win-x64 Release builds into one
    stage folder, compiles the installer from it (tools\build-installer.ps1, when
    makensis is installed), then zips the stage into <OutputRoot>\AppPackagerDesktop-<version>.zip
    and writes checksums.txt covering the zip and the installer.

    The halves run separately when something has to happen to the published files in
    between, which is how the release workflow signs them: -PublishOnly leaves the stage
    in place, build-installer.ps1 runs on the signed stage, -PackageOnly zips the stage
    and hashes whatever installer sits in <OutputRoot>.

    The version comes from <Version> in the UI csproj. Nothing is uploaded; releases are
    created from the artifacts this produces.

.PARAMETER OutputRoot
    Directory that receives the zip, the installer and checksums.txt. Defaults to <repo>\publish.

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
$buildProjectPath = Join-Path $repoRoot 'src\ApplicationPackagerBuild\ApplicationPackagerBuild.csproj'
if (-not (Test-Path $buildProjectPath)) {
    throw "Command-line build project not found at $buildProjectPath"
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
$buildExePath = Join-Path $stageDir 'AppPackagerBuild.exe'

Write-Host "App Packager Desktop $version -> $OutputRoot"

if (-not $PackageOnly) {
    if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    # The command-line build references the window project. A framework-dependent publish of it leaves the small
    # apphost of the window project in the stage, so the window project is published last and its single-file
    # executable is the one that stays.
    foreach ($project in @($buildProjectPath, $projectPath)) {
        & dotnet publish $project `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained false `
            -p:PublishSingleFile=true `
            --output $stageDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $(Split-Path -Leaf $project) failed with exit code $LASTEXITCODE" }
    }

    # Symbols are build output, not release content.
    Get-ChildItem -LiteralPath $stageDir -Filter *.pdb -Recurse | Remove-Item -Force

    foreach ($published in @($exePath, $buildExePath)) {
        if (-not (Test-Path $published)) { throw "Published executable missing: $published" }
        # A single-file executable carries the application and the engine; the apphost stub is under 1 MB.
        if ((Get-Item -LiteralPath $published).Length -lt 1MB) { throw "$published is the small apphost, not the single-file executable." }
    }

    # The license of the product and the notices of the bundled components ship with the binaries.
    foreach ($notice in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item -LiteralPath (Join-Path $repoRoot $notice) -Destination $stageDir -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $stageDir -Recurse -Force

    if ($PublishOnly) {
        Write-Host ''
        Write-Host "stage      : $stageDir"
        Get-ChildItem -LiteralPath $stageDir -File -Recurse | ForEach-Object {
            Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($stageDir.Length + 1))
        }
        return
    }

    # A full local build compiles the installer here; the release workflow
    # instead runs build-installer.ps1 itself, after signing the stage.
    $makensis = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if (-not $makensis -and (Test-Path -LiteralPath "${env:ProgramFiles(x86)}\NSIS\makensis.exe")) { $makensis = $true }
    if ($makensis) {
        & (Join-Path $PSScriptRoot 'build-installer.ps1') -StageDir $stageDir -OutputDir $OutputRoot | Out-Null
    }
    else {
        Write-Warning 'makensis.exe not found; the zip is built without an installer.'
    }
}

if (-not (Test-Path $exePath) -or -not (Test-Path $buildExePath)) { throw "No published stage to package at $stageDir; run without -PackageOnly first." }

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$installerPath = Join-Path $OutputRoot "AppPackagerDesktop-Setup-$version.exe"
$artifacts = @($zipPath) + @(if (Test-Path -LiteralPath $installerPath) { $installerPath })
$lines = & (Join-Path $PSScriptRoot 'write-checksums.ps1') -Path $artifacts -Destination $checksumPath

Remove-Item -LiteralPath $stageDir -Recurse -Force
$generatedDir = Join-Path $OutputRoot 'installer-generated'
if (Test-Path -LiteralPath $generatedDir) { Remove-Item -LiteralPath $generatedDir -Recurse -Force }
$uninstallerDir = Join-Path $OutputRoot 'uninstaller'
if (Test-Path -LiteralPath $uninstallerDir) { Remove-Item -LiteralPath $uninstallerDir -Recurse -Force }

Write-Host ''
foreach ($artifact in $artifacts) {
    $item = Get-Item -LiteralPath $artifact
    Write-Host ("artifact   : {0} ({1:N1} MB)" -f $item.Name, ($item.Length / 1MB))
}
Write-Host ("checksums  : {0}" -f (Split-Path -Leaf $checksumPath))
$lines | ForEach-Object { Write-Host ("  {0}" -f $_) }
