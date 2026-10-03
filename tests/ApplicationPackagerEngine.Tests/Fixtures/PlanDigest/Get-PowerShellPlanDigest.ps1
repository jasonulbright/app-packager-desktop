#Requires -Version 5.1
<#
.SYNOPSIS
    Prints the AppPackager plan digest of every fixture manifest in this folder.

.DESCRIPTION
    Runs Get-StageManifestPlanDigest under Windows PowerShell 5.1 (powershell.exe).
    ModuleRoot is the folder that holds Packagers\AppPackagerCommon.psd1. The
    output lines are the content of powershell-digests.txt.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Get-PowerShellPlanDigest.ps1 -ModuleRoot <folder>
#>
param(
    [Parameter(Mandatory)][string]$ModuleRoot,
    [string]$FixtureFolder
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($FixtureFolder)) { $FixtureFolder = Split-Path -Parent $MyInvocation.MyCommand.Path }
$module = Import-Module (Join-Path $ModuleRoot 'Packagers\AppPackagerCommon.psd1') -Force -PassThru -DisableNameChecking
foreach ($file in Get-ChildItem -LiteralPath $FixtureFolder -Filter '*.json' | Sort-Object Name) {
    $data = [System.IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    $digest = & $module { param($manifest) Get-StageManifestPlanDigest -ManifestData $manifest } $data
    '{0} {1}' -f $digest, $file.Name
}
