<#
.SYNOPSIS
    Installs the WSUS administration console (RSAT: Windows Server Update Services Tools) on a lab client, so that APD can publish to the WSUS server of the lab.
    Repeatable: a client that has the console is left as it is.

.DESCRIPTION
    Windows 11 22H2 and later ask a WSUS server for Features on Demand through on-premises UUP. The WSUS server of a lab software update point has the metadata of the
    package and not its files, so every file URL answers HTTP 404 and the install ends with 0x80d02002 (no download progress). The setting
    "Download repair content and optional features directly from Windows Update" (RepairContentServerSource = 2) is no longer an option from Windows 11 24H2, and
    UseWUServer = 0 alone leaves WSUS as the source. Windows Update also reads a cached copy of the policy (UpdatePolicy\GPCache), which a registry change and a
    service restart do not rebuild. So for the length of the install this script takes the WSUS source values away and removes that cache. The Windows Update service
    then reads the policy keys, and the client gets the feature from Windows Update. The script writes both keys to files before it changes them, and puts them back
    after the install, also when the install fails. A run that finds the files of a run that did not end restores them first. Sources:
      https://learn.microsoft.com/windows/deployment/update/fod-and-lang-packs
      https://learn.microsoft.com/windows-server/administration/windows-server-update-services/plan/plan-your-wsus-deployment (UUP considerations)
      https://learn.microsoft.com/windows/win32/delivery_optimization/do-return-values (0x80d02002)
      https://learn.microsoft.com/windows-server/administration/windows-server-update-services/deploy/2-configure-wsus#BKMK_20ConnectConsole
    Run it elevated on the client (a scheduled task as SYSTEM over WinRM works).
.PARAMETER TimeoutMinutes
    The time the install may take before the script ends it and says so.
#>
param([int]$TimeoutMinutes = 40)
$ErrorActionPreference = 'Stop'
$name = 'Rsat.WSUS.Tools~~~~0.0.1.0'
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$cache = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\GPCache'
$store = Join-Path $env:ProgramData 'ApdLab\wsus-console'
$saved = [ordered]@{ $policy = (Join-Path $store 'policy.reg'); $cache = (Join-Path $store 'policy-cache.reg') }
function Test-WsusApi { Test-Path (Join-Path $env:SystemRoot 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.UpdateServices.Administration') }
function Stop-UpdateService { foreach ($service in 'usosvc', 'wuauserv') { Stop-Service $service -Force -ErrorAction SilentlyContinue } }
function Start-UpdateService { foreach ($service in 'usosvc', 'wuauserv') { Start-Service $service -ErrorAction SilentlyContinue } }
function Restore-UpdatePolicy {
    if (-not (Test-Path (Join-Path $store 'complete'))) { return }
    Stop-UpdateService
    foreach ($key in $saved.Keys) {
        if (-not (Test-Path $saved[$key])) {
            # A cache that did not exist before the install holds the values of the install.
            if ($key -eq $cache -and (Test-Path $key)) { Remove-Item $key -Recurse -Force }
            continue
        }
        # The key is replaced, not merged, so that a value that appeared during the install does not stay.
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
        # reg.exe writes its success text to the error stream, which Windows PowerShell makes a terminating error when it is redirected.
        $run = Start-Process reg.exe -ArgumentList 'import', ('"{0}"' -f $saved[$key]) -Wait -PassThru -WindowStyle Hidden
        if ($run.ExitCode -ne 0) { throw "$key was not restored from $($saved[$key])." }
    }
    Remove-Item $store -Recurse -Force
    Start-UpdateService
    'The Windows Update policy and its cache are restored.'
}

Restore-UpdatePolicy
if ((Get-WindowsCapability -Online -Name $name).State -eq 'Installed' -and (Test-WsusApi)) { 'The WSUS console is installed.'; return }

try {
    if (Test-Path $policy) {
        if (Test-Path $store) { Remove-Item $store -Recurse -Force }
        $null = New-Item -ItemType Directory -Force $store
        foreach ($key in $saved.Keys) {
            if (-not (Test-Path $key)) { continue }
            $null = & reg.exe export ($key -replace '^HKLM:', 'HKLM') $saved[$key] /y
            if ($LASTEXITCODE -ne 0) { throw "$key was not saved, so nothing is changed." }
        }
        $null = New-Item -ItemType File (Join-Path $store 'complete')
        Stop-UpdateService
        foreach ($value in 'WUServer', 'WUStatusServer', 'UpdateServiceUrlAlternate', 'FillEmptyContentUrls') {
            Remove-ItemProperty -Path $policy -Name $value -ErrorAction SilentlyContinue
        }
        if (Test-Path "$policy\AU") { Set-ItemProperty -Path "$policy\AU" -Name UseWUServer -Value 0 }
        if (Test-Path $cache) { Remove-Item $cache -Recurse -Force }
        Start-UpdateService
        Start-Sleep -Seconds 15
        $source = ((New-Object -ComObject Microsoft.Update.ServiceManager).Services | Where-Object IsDefaultAUService).Name
        "Default update service for the install: $source"
        if ($source -eq 'Windows Server Update Service') { throw 'The client still takes its updates from WSUS.' }
    }
    $job = Start-Job { param($n) Add-WindowsCapability -Online -Name $n | Out-String } -ArgumentList $name
    # No Stop-Job on a timeout: it waits for a job in a servicing call, and the policy restore must not wait. The job ends with this process.
    if (-not (Wait-Job $job -Timeout ($TimeoutMinutes * 60))) { throw "The install did not end in $TimeoutMinutes minutes." }
    Receive-Job $job
}
finally {
    Restore-UpdatePolicy
}
$state = (Get-WindowsCapability -Online -Name $name).State
"Capability state: $state; WSUS API assembly present: $(Test-WsusApi)"
if ($state -ne 'Installed' -or -not (Test-WsusApi)) { throw 'The WSUS console is not installed.' }
