<#
.SYNOPSIS
    Creates a Configuration Manager CI and baseline that detects LSA Protection (RunAsPPL).

.DESCRIPTION
    Single native registry-value compliance setting:

        HKLM\SYSTEM\CurrentControlSet\Control\Lsa
            RunAsPPL = 1 or 2 (DWORD)

    The registry rule accepts both supported enabled values. Configuration Manager cannot
    remediate a OneOf registry rule, so this baseline runs in detection-only mode.

    REBOOT REQUIRED: protection engages on the next boot. Validate driver/plugin
    compatibility (some smartcard / AV LSA plugins must be PPL-signed) before broad
    enforcement.

.PARAMETER SiteCode
    Configuration Manager site code.

.PARAMETER SiteServer
    Configuration Manager site server FQDN.

.PARAMETER CollectionName
    If specified, deploys the detection-only baseline to this collection daily.

.EXAMPLE
    .\New-LSAProtectionBaseline.ps1 -SiteCode MCM -SiteServer CM01.contoso.local
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SiteCode,

    [Parameter(Mandatory)]
    [string]$SiteServer,

    [string]$CollectionName
)

$ErrorActionPreference = 'Stop'

# ============================================================================
# Connect to Configuration Manager
# ============================================================================

$modulePath = Join-Path (Split-Path $ENV:SMS_ADMIN_UI_PATH -Parent) "ConfigurationManager.psd1"
if (-not (Get-Module ConfigurationManager -ErrorAction SilentlyContinue)) {
    if (Test-Path $modulePath) {
        Import-Module $modulePath
    }
    else {
        throw "ConfigurationManager module not found. Run this from a machine with the Configuration Manager admin console installed."
    }
}

$OriginalLocation = Get-Location

if (-not (Get-PSDrive -Name $SiteCode -PSProvider CMSite -ErrorAction SilentlyContinue)) {
    New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $SiteServer | Out-Null
}
Set-Location "${SiteCode}:"

try {
    $CIName   = "Hardening: LSA Protection (RunAsPPL)"
    $CBName   = "Hardening LSA Protection RunAsPPL"
    $Severity = 'Critical'

    $settings = @(
        @{ Name = 'RunAsPPL'
           KeyName = 'SYSTEM\CurrentControlSet\Control\Lsa'
           ValueName = 'RunAsPPL'
           Expected = @('1', '2')
           Description = 'LSA Protection. 1 enables protection with a UEFI lock; 2 enables protection without a UEFI lock. Effective after reboot.' }
    )

    Write-Host "Creating Configuration Item: $CIName"
    $ci = New-CMConfigurationItem `
        -Name $CIName `
        -Description "Requires LSA Protection (RunAsPPL = 1 or 2) so LSASS runs as a Protected Process Light. Effective after reboot. Validate LSA plugin signing before broad enforcement." `
        -CreationType WindowsOS

    foreach ($s in $settings) {
        Write-Host "  Adding setting: $($s.ValueName) = $($s.Expected)"
        $p = @{
            InputObject = $ci; Name = $s.Name; Description = $s.Description; Hive = 'LocalMachine'
            KeyName = $s.KeyName; ValueName = $s.ValueName; DataType = 'Integer'; Is64Bit = $true
            ValueRule = $true; RuleName = "$($s.ValueName) equals $($s.Expected)"
            ExpressionOperator = 'OneOf'; ExpectedValue = $s.Expected
            NoncomplianceSeverity = $Severity; ReportNoncompliance = $true
        }
        Add-CMComplianceSettingRegistryKeyValue @p
    }
    Write-Host "  CI created." -ForegroundColor Green

    $ci = Get-CMConfigurationItem -Name $CIName -Fast

    Write-Host "Creating Configuration Baseline: $CBName"
    New-CMBaseline -Name $CBName -Description "Enforces LSA Protection (RunAsPPL) on targeted Windows hosts. Reboot required to take effect."
    Set-CMBaseline -Name $CBName -AddOSConfigurationItem $ci.CI_ID
    Write-Host "  Baseline created." -ForegroundColor Green

    if ($CollectionName) {
        Write-Host "Deploying baseline to: $CollectionName"
        $schedule = New-CMSchedule -RecurInterval Days -RecurCount 1
        New-CMBaselineDeployment `
            -Name $CBName -CollectionName $CollectionName `
            -EnableEnforcement $false -OverrideServiceWindow $false `
            -GenerateAlert $false -MonitoredByScom $false -Schedule $schedule
        Write-Host "  Deployed daily, mode: DETECT ONLY" -ForegroundColor Green
    }
    else {
        Write-Host "  Baseline created but NOT deployed. Re-run with -CollectionName." -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "=== Summary ===" -ForegroundColor Cyan
    Write-Host "CI:       $CIName"
    Write-Host "Baseline: $CBName"
    Write-Host "Severity: $Severity"
    Write-Host "Reboot:   REQUIRED to engage protection"
    Write-Host "Mode:     Detect only"
    $settings | ForEach-Object { Write-Host ("Setting:  HKLM\{0}\{1} is one of {2}" -f $_.KeyName, $_.ValueName, ($_.Expected -join ', ')) }
}
finally {
    Set-Location $OriginalLocation
}
