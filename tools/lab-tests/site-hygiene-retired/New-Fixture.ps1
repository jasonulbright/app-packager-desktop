<#
.SYNOPSIS
    Makes the throw-away objects of the Site Hygiene lab test for the broken supersedence fix (SUP-01) and for the objects of retired
    applications (Q-136). Run it on the ConfigMgr site server (CM01) in Windows PowerShell 5.1 with the console module installed.

.DESCRIPTION
    Every object name starts with the prefix (default SHT2). The test removes them with Remove-Fixture.ps1.

    SUP-01 (a supersedence to a deleted application):
      <prefix>-BS-Plain         active application
      <prefix>-BS-Retired       retired application (the site keeps the old document of a retired application when it is written)
      <prefix>-BS-Deployed      active application with a deployment
      <prefix>-BS-RetDeployed   retired application with a deployment (the fix must refuse it and say why)
    A retired application with dependencies:
      <prefix>-Ret              retired, deployed to <prefix>-Coll
      <prefix>-Sup              active, supersedes <prefix>-Ret
      <prefix>-Dep              active, depends on <prefix>-Ret
#>
param([string]$Prefix = 'SHT2')
$ErrorActionPreference = 'Stop'
Import-Module "$($env:SMS_ADMIN_UI_PATH)\..\ConfigurationManager.psd1"
$code = (Get-CimInstance -Namespace root\sms -ClassName SMS_ProviderLocation).SiteCode
if (-not (Get-PSDrive $code -ErrorAction SilentlyContinue)) { New-PSDrive -Name $code -PSProvider CMSite -Root ([System.Net.Dns]::GetHostEntry('').HostName) | Out-Null }
Set-Location "${code}:"

function New-TestApp([string]$Name) {
    $null = New-CMApplication -Name $Name -Description 'Q-136 lab test'
    $null = Add-CMScriptDeploymentType -ApplicationName $Name -DeploymentTypeName "$Name DT" -InstallCommand 'cmd /c exit 0' -ScriptLanguage PowerShell -ScriptText 'exit 0' `
        -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn
    Get-CMApplication -Name $Name
}
function Set-Supersedes($New, $Old) {
    Set-CMApplicationSupersedence -InputObject $New -CurrentDeploymentTypeId (Get-CMDeploymentType -InputObject $New).CI_ID -SupersededApplicationId $Old.CI_ID `
        -OldDeploymentTypeId (Get-CMDeploymentType -InputObject $Old).CI_ID | Out-Null
}

$collection = "$Prefix-Coll"
if (-not (Get-CMDeviceCollection -Name $collection)) { $null = New-CMDeviceCollection -Name $collection -LimitingCollectionName 'All Systems' }

foreach ($kind in 'Plain', 'Retired', 'Deployed', 'RetDeployed') {
    $old = New-TestApp "$Prefix-Gone-$kind"
    $new = New-TestApp "$Prefix-BS-$kind"
    Set-Supersedes $new $old
    # The site deletes an application that another application supersedes and leaves the rule in the document of the other application.
    Remove-CMApplication -Id $old.CI_ID -Force
    if ($kind -in 'Deployed', 'RetDeployed') { $null = New-CMApplicationDeployment -Name "$Prefix-BS-$kind" -CollectionName $collection -DeployAction Install -DeployPurpose Available }
    if ($kind -in 'Retired', 'RetDeployed') { Suspend-CMApplication -InputObject (Get-CMApplication -Name "$Prefix-BS-$kind") }
}

$ret = New-TestApp "$Prefix-Ret"; $dep = New-TestApp "$Prefix-Dep"; $sup = New-TestApp "$Prefix-Sup"
$null = New-CMApplicationDeployment -Name "$Prefix-Ret" -CollectionName $collection -DeployAction Install -DeployPurpose Available
Set-Supersedes $sup $ret
$group = New-CMDeploymentTypeDependencyGroup -GroupName "$Prefix group" -InputObject (Get-CMDeploymentType -InputObject (Get-CMApplication -Name "$Prefix-Dep"))
$null = Add-CMDeploymentTypeDependency -InputObject $group -DeploymentTypeDependency (Get-CMDeploymentType -InputObject (Get-CMApplication -Name "$Prefix-Ret")) -IsAutoInstall $false
Suspend-CMApplication -InputObject (Get-CMApplication -Name "$Prefix-Ret")

Get-CMApplication -Name "$Prefix-*" | ForEach-Object { '{0}  retired={1}  revision={2}' -f $_.LocalizedDisplayName, $_.IsExpired, $_.CIVersion }
