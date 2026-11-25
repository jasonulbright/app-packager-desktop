<#
.SYNOPSIS
    Installs the WSUS administration console (RSAT: Windows Server Update Services Tools) on a lab client, so that APD can publish to the WSUS server of the lab.
    Repeatable: a client that has the console is left as it is.

.DESCRIPTION
    A client that a WSUS server manages (Windows Update policy UseWUServer = 1) asks that server for optional features, and the server has none, so the install waits
    without an end. The setting "Specify settings for optional component installation and component repair" with "Download repair content and optional features directly
    from Windows Update instead of Windows Server Update Services" (registry value RepairContentServerSource = 2 under HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing)
    sends this one request to Windows Update. Sources:
      https://learn.microsoft.com/windows-server/administration/windows-server-update-services/deploy/2-configure-wsus#BKMK_20ConnectConsole
      https://learn.microsoft.com/windows/deployment/update/fod-and-lang-packs (Features on Demand, the Windows Update policy for optional components)
      https://learn.microsoft.com/windows-server/administration/windows-commands/dism (Add-WindowsCapability names the capability Rsat.WSUS.Tools~~~~0.0.1.0)
    Run it elevated on the client (a scheduled task as SYSTEM over WinRM works). Output goes to the transcript and to the pipeline.
.PARAMETER TimeoutMinutes
    The time the install may take before the script ends it and says so.
#>
param([int]$TimeoutMinutes = 20)
$ErrorActionPreference = 'Stop'
$name = 'Rsat.WSUS.Tools~~~~0.0.1.0'
function Test-WsusApi { Test-Path (Join-Path $env:SystemRoot 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.UpdateServices.Administration') }
if ((Get-WindowsCapability -Online -Name $name).State -eq 'Installed' -and (Test-WsusApi)) { 'The WSUS console is installed.'; return }
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name RepairContentServerSource -Type DWord -Value 2
# The repair-source setting alone left the download of a lab client at 75 percent, so the managed-client policy (UseWUServer) is also off for the length of the install and set back after.
$au = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
$was = (Get-ItemProperty -Path $au -Name UseWUServer -ErrorAction SilentlyContinue).UseWUServer
try {
    if ($null -ne $was) { Set-ItemProperty -Path $au -Name UseWUServer -Value 0; Restart-Service wuauserv -Force }
    $job = Start-Job { param($n) Add-WindowsCapability -Online -Name $n | Out-String } -ArgumentList $name
    if (-not (Wait-Job $job -Timeout ($TimeoutMinutes * 60))) { Stop-Job $job; throw "The install did not end in $TimeoutMinutes minutes." }
    Receive-Job $job
}
finally {
    if ($null -ne $was) { Set-ItemProperty -Path $au -Name UseWUServer -Value $was; Restart-Service wuauserv -Force }
}
$state = (Get-WindowsCapability -Online -Name $name).State
"Capability state: $state; WSUS API assembly present: $(Test-WsusApi)"
if ($state -ne 'Installed' -or -not (Test-WsusApi)) { throw 'The WSUS console is not installed.' }
