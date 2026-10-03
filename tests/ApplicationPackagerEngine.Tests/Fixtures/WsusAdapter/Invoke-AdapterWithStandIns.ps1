#Requires -Version 5.1
<#
.SYNOPSIS
    Runs one WsusAdapter.ps1 request against a stand-in WSUS server.

.DESCRIPTION
    Dot-sources the adapter, which defines its functions without running a
    request, replaces the functions that touch the WSUS assembly or the
    network with stand-ins for the named scenario, then runs the request.
    The stand-in server records what it receives in the result Data where a
    scenario needs to show it.
#>
param(
    [Parameter(Mandatory)][string]$AdapterPath,
    [Parameter(Mandatory)][string]$RequestPath,
    [Parameter(Mandatory)][string]$Scenario
)
$ErrorActionPreference = 'Stop'
$script:StandInScenario = $Scenario
$script:StandInRequest = $RequestPath
. $AdapterPath

$global:StandInLog = New-Object System.Collections.Generic.List[string]

function New-StandInUpdate {
    param([string]$Id, [string]$Title, [bool]$Declined = $false, [string]$Description = '')
    $update = [pscustomobject]@{
        Id = [pscustomobject]@{ UpdateId = [guid]$Id }; Title = $Title; Description = $Description; IsDeclined = $Declined
        IsSuperseded = $false; CreationDate = [datetime]'2026-09-01T10:00:00'; PublicationState = 'Published'
    }
    $update | Add-Member -MemberType ScriptMethod -Name GetUpdateApprovals -Value { @() }
    return $update
}

function Import-WsusAdministrationAssembly { return [pscustomobject]@{ } }
function Test-WsusServerReachable { param($ServerName, $PortNumber) }
function New-WsusServerObject {
    param($ServerName, $UseSsl, $PortNumber)
    if ($script:StandInScenario -eq 'ConnectFails') { throw 'The request failed with HTTP status 404: Not Found.' }
    $server = [pscustomobject]@{ Name = $ServerName; Version = '10.0.20348.1'; IsConnectionSecureForApiRemoting = ($script:StandInScenario -ne 'Insecure') }
    $server | Add-Member -MemberType ScriptMethod -Name GetCurrentUserRole -Value { if ($script:StandInScenario -eq 'Reporter') { 'Reporter' } else { 'Administrator' } }
    $server | Add-Member -MemberType ScriptMethod -Name GetConfiguration -Value {
        $configuration = [pscustomobject]@{ LocalPublishingMaxCabSize = 384 }
        $configuration | Add-Member -MemberType ScriptMethod -Name SetSigningCertificate -Value {
            param($Path, $Password)
            $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))
            $global:StandInLog.Add(('pfx {0} password {1}' -f $Path, $plain))
        }
        $configuration | Add-Member -MemberType ScriptMethod -Name Save -Value { $global:StandInLog.Add('save') }
        $configuration
    }
    $server | Add-Member -MemberType ScriptMethod -Name GetComputerTargetGroups -Value {
        if ($script:StandInScenario -eq 'OneGroup') { return @([pscustomobject]@{ Id = [guid]'11111111-2222-3333-4444-555555555555'; Name = 'Pilot' }) }
        @([pscustomobject]@{ Id = [guid]'11111111-2222-3333-4444-555555555555'; Name = 'Pilot' },
          [pscustomobject]@{ Id = [guid]::NewGuid(); Name = 'All Computers' },
          [pscustomobject]@{ Id = [guid]::NewGuid(); Name = 'Pilot' })
    }
    return $server
}
function Get-WsusSigningCertificateObject { param($Server) return $null }
function Get-WsusLocallyPublishedUpdateObjects {
    param($Server)
    return @(New-StandInUpdate -Id 'aaaaaaaa-0000-0000-0000-000000000001' -Title 'Tool 2' -Description "Tool 2`r`n`r`nAppPackager identity: AppPackager:catalog:package-tool/default; type Update; version 2")
}
function Get-WsusUpdateObject {
    param($Server, $PackageId)
    switch ([string]$PackageId) {
        'bbbbbbbb-0000-0000-0000-000000000001' { return (New-StandInUpdate -Id $PackageId -Title 'Tool 1' -Declined $true) }
        'bbbbbbbb-0000-0000-0000-000000000002' { return (New-StandInUpdate -Id $PackageId -Title 'Tool 2') }
        'bbbbbbbb-0000-0000-0000-000000000004' { return (New-StandInUpdate -Id $PackageId -Title 'Tool 4') }
        default { return $null }
    }
}
function Invoke-WsusUpdateApproval { param($Server, $Update, $GroupName) throw ("WSUS has no computer group named '{0}'." -f $GroupName) }
function Invoke-WsusUpdateDecline { param($Update) $global:StandInLog.Add('decline ' + $Update.Title) }
function Invoke-WsusUpdateDeletion {
    param($Server, $UpdateId)
    if ([string]$UpdateId -eq 'bbbbbbbb-0000-0000-0000-000000000004') { throw 'This update is still referenced by at least another update in the database.' }
    $global:StandInLog.Add('delete ' + $UpdateId)
}
function Remove-WsusPackageFolder { param($Server, $PackageId) throw 'Access is denied' }
function New-WsusSoftwareDistributionPackageFile {
    param($Plan, $SourceFolder, $Path)
    foreach ($file in @(Get-ChildItem -LiteralPath $SourceFolder -File -Recurse)) {
        $global:StandInLog.Add(('package {0} {1}' -f $file.Name, [System.IO.File]::ReadAllText($file.FullName)))
    }
}
function Invoke-WsusPublisher { param($Server, $SdpPath, $SourceFolder, $PackageId) $global:StandInLog.Add('publish') }
function Invoke-WsusCatalogImport {
    param($Server, $UpdateId)
    if ([string]$UpdateId -eq '00000000-0000-0000-0000-00000000dead') { throw 'Update not found in the catalog' }
}

# The stand-in log reaches the test through the result document.
function Write-WsusAdapterResult {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Result)
    $Result['StandInLog'] = @($global:StandInLog.ToArray())
    $json = ConvertTo-Json -InputObject $Result -Depth 10 -Compress
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

Invoke-WsusAdapter -InputPath $script:StandInRequest
