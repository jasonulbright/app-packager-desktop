<#
.SYNOPSIS
    Creates Configuration Manager CIs and Baselines that enforce the full CPU speculative-execution mitigation
    family (Intel AND AMD) via a bitmask check on FeatureSettingsOverride.

.DESCRIPTION
    Windows controls every CPU speculative-execution mitigation through two DWORDs:

        HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management
            FeatureSettingsOverride      (REG_DWORD)  -- a BITMASK of enabled mitigations
            FeatureSettingsOverrideMask  (REG_DWORD)  -- which bits the OS honours (always 0x3)

    FeatureSettingsOverride is a bitmask, NOT a single magic value: each CVE family sets its own
    bit(s), and a host can legitimately carry MORE bits than any one baseline cares about. A naive
    "FeatureSettingsOverride -eq <exact value>" registry rule therefore false-fails the moment a
    host also has another mitigation set. This builder instead uses a SCRIPT compliance setting
    that tests the required bits with -band, so a superset is still compliant.

    The required bits depend on two axes:

      1. Hyper-Threading posture (one CI/CB per variant; targeted to the matching HT collection):
           HT Enabled   base = 0x0048
           HT Disabled  base = 0x2048   (adds bit 0x2000 = "disable HT for L1TF/MDS")

      2. CPU vendor (auto-detected at evaluation time via Win32_Processor.Manufacturer):
           Intel  +0x00800000  (BHI, CVE-2022-0001) on top of the shared base
           AMD    +0x01000040  (Branch Type Confusion, CVE-2022-23825)
                  +0x04000040  (Inception, CVE-2023-20569)

    Resulting target values (FeatureSettingsOverrideMask is always 0x3):
           Intel HT Enabled   0x00800048      AMD HT Enabled   0x05000048
           Intel HT Disabled  0x00802048      AMD HT Disabled  0x05002048

    Bit values follow Microsoft KB4073119 and KB4072698. Hyper-V hosts additionally want
    MinVmVersionForCpuBasedMitigations = "1.0" (HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\
    Virtualization) -- out of scope for this baseline.

    Covered CVEs:
      Shared base : CVE-2017-5715 Spectre v2, CVE-2017-5754 Meltdown, CVE-2018-3639 SSBD,
                    CVE-2018-3620/3646 L1TF, CVE-2018-12126/12127/12130/11091 MDS,
                    CVE-2019-11135 TAA, CVE-2020-0543 SRBDS
      Intel       : CVE-2022-0001 BHI
      AMD         : CVE-2022-23825 Branch Type Confusion, CVE-2023-20569 Inception

.PARAMETER SiteCode
    Configuration Manager site code.

.PARAMETER SiteServer
    Configuration Manager site server FQDN (the SMS Provider host).

.PARAMETER HtEnabledCollectionName
    Collection that receives the HT-Enabled CB. Must already exist.
    Default: "Devices: Hyperthreading Enabled".

.PARAMETER HtDisabledCollectionName
    Collection that receives the HT-Disabled CB. Default: "Devices: Hyperthreading Disabled".

.PARAMETER SkipDeploy
    Create the CIs/CBs without deploying them.

.PARAMETER DetectOnly
    Create with remediation disabled (report non-compliance, never write).

.EXAMPLE
    .\New-SpecExecBaseline.ps1 -SiteCode MCM -SiteServer CM01.contoso.com

.EXAMPLE
    .\New-SpecExecBaseline.ps1 -SiteCode MCM -SiteServer CM01.contoso.com -DetectOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SiteCode,
    [Parameter(Mandatory)][string]$SiteServer,
    [string]$HtEnabledCollectionName  = 'Devices: Hyperthreading Enabled',
    [string]$HtDisabledCollectionName = 'Devices: Hyperthreading Disabled',
    [switch]$SkipDeploy,
    [switch]$DetectOnly
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# Discovery / remediation script templates. __BASE__ is replaced per HT variant.
# Vendor is detected at evaluation time so one CI covers both Intel and AMD hosts.
# Discovery returns 1 (compliant) when the required bits are present, else 0.
# ----------------------------------------------------------------------------
$discoveryTemplate = @'
$P  = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
$ov = (Get-ItemProperty -Path $P -Name FeatureSettingsOverride     -ErrorAction SilentlyContinue).FeatureSettingsOverride
$mk = (Get-ItemProperty -Path $P -Name FeatureSettingsOverrideMask -ErrorAction SilentlyContinue).FeatureSettingsOverrideMask
if ($null -eq $ov) { $ov = 0 }
if ($null -eq $mk) { $mk = 0 }
$vendor = (Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1).Manufacturer
$base = __BASE__
if     ($vendor -match 'Intel')            { $req = $base -bor 0x00800000 }
elseif ($vendor -match 'AMD|Advanced Micro') { $req = $base -bor 0x01000040 -bor 0x04000040 }
else   { $req = $base }
if (((([int]$ov) -band $req) -eq $req) -and ((([int]$mk) -band 0x3) -eq 0x3)) { 1 } else { 0 }
'@

$remediationTemplate = @'
$P = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
$vendor = (Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1).Manufacturer
$base = __BASE__
if     ($vendor -match 'Intel')            { $req = $base -bor 0x00800000 }
elseif ($vendor -match 'AMD|Advanced Micro') { $req = $base -bor 0x01000040 -bor 0x04000040 }
else   { $req = $base }
$ov = (Get-ItemProperty -Path $P -Name FeatureSettingsOverride -ErrorAction SilentlyContinue).FeatureSettingsOverride
if ($null -eq $ov) { $ov = 0 }
New-ItemProperty -Path $P -Name FeatureSettingsOverride     -Value (([int]$ov) -bor $req) -PropertyType DWord -Force | Out-Null
$mk = (Get-ItemProperty -Path $P -Name FeatureSettingsOverrideMask -ErrorAction SilentlyContinue).FeatureSettingsOverrideMask
if ($null -eq $mk) { $mk = 0 }
New-ItemProperty -Path $P -Name FeatureSettingsOverrideMask -Value (([int]$mk) -bor 0x3) -PropertyType DWord -Force | Out-Null
# Reboot required for the new mitigation posture to take effect.
'@

# ----------------------------------------------------------------------------
# Connect to Configuration Manager
# ----------------------------------------------------------------------------
$modulePath = Join-Path (Split-Path $ENV:SMS_ADMIN_UI_PATH -Parent) 'ConfigurationManager.psd1'
if (-not (Get-Module ConfigurationManager -ErrorAction SilentlyContinue)) {
    if (Test-Path $modulePath) { Import-Module $modulePath }
    else { throw 'ConfigurationManager module not found. Run this from a machine with the Configuration Manager admin console installed.' }
}

$OriginalLocation = Get-Location
if (-not (Get-PSDrive -Name $SiteCode -PSProvider CMSite -ErrorAction SilentlyContinue)) {
    New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $SiteServer | Out-Null
}
Set-Location "${SiteCode}:"

# ----------------------------------------------------------------------------
# Helper: build one HT-variant CI + CB
# ----------------------------------------------------------------------------
function New-SpecExecVariant {
    param(
        [string]$Variant,     # 'HT Enabled' or 'HT Disabled'
        [string]$BaseHex,     # '0x0048' or '0x2048'
        [string]$CollectionName
    )

    $CIName = "CPU SpecExec Mitigations ($Variant)"
    $CBName = "CPU SpecExec Mitigations - $Variant"

    Write-Host ""
    Write-Host "=== $Variant variant ===" -ForegroundColor Cyan
    Write-Host "Creating Configuration Item: $CIName"

    $ci = New-CMConfigurationItem `
        -Name $CIName `
        -Description "Bitmask enforcement of the CPU speculative-execution mitigation family (Intel + AMD, vendor auto-detected). $Variant. Covers Spectre v2/Meltdown/SSBD/L1TF/MDS/TAA/SRBDS, Intel BHI (CVE-2022-0001), AMD Branch Type Confusion (CVE-2022-23825) and Inception (CVE-2023-20569). Per Microsoft KB4073119 / KB4072698." `
        -CreationType WindowsOS

    $disc = $discoveryTemplate.Replace('__BASE__', $BaseHex)
    $rem  = $remediationTemplate.Replace('__BASE__', $BaseHex)

    Write-Host "  Adding script setting: SpecExecMitigationBits (base $BaseHex)"
    $settingParams = @{
        InputObject               = $ci
        Name                      = 'SpecExecMitigationBits'
        Description               = "Returns 1 when FeatureSettingsOverride carries the required mitigation bits for this CPU vendor ($Variant), else 0."
        DataType                  = 'Integer'
        Is64Bit                   = $true
        DiscoveryScriptLanguage   = 'PowerShell'
        DiscoveryScriptText       = $disc
        RemediationScriptLanguage = 'PowerShell'
        RemediationScriptText     = $rem
        ValueRule                 = $true
        ExpressionOperator        = 'IsEquals'
        ExpectedValue             = '1'
        RuleName                  = "SpecExec mitigation bits present ($Variant)"
        NoncomplianceSeverity     = 'Critical'
        ReportNoncompliance       = $true
    }
    if (-not $DetectOnly) { $settingParams.Remediate = $true }
    Add-CMComplianceSettingScript @settingParams

    Write-Host "  CI created." -ForegroundColor Green

    $ci = Get-CMConfigurationItem -Name $CIName -Fast

    Write-Host "Creating Configuration Baseline: $CBName"
    New-CMBaseline `
        -Name $CBName `
        -Description "Enforces the CPU speculative-execution mitigation bitmask ($Variant), Intel + AMD, per Microsoft + vendor guidance."
    Set-CMBaseline -Name $CBName -AddOSConfigurationItem $ci.CI_ID
    Write-Host "  Baseline created." -ForegroundColor Green

    if (-not $SkipDeploy) {
        Write-Host "Deploying baseline to: $CollectionName"
        $schedule = New-CMSchedule -RecurInterval Days -RecurCount 1
        New-CMBaselineDeployment `
            -Name $CBName `
            -CollectionName $CollectionName `
            -EnableEnforcement (-not $DetectOnly) `
            -OverrideServiceWindow $false `
            -GenerateAlert $false `
            -MonitoredByScom $false `
            -Schedule $schedule
        $mode = if ($DetectOnly) { 'DETECT ONLY' } else { 'DETECT + REMEDIATE' }
        Write-Host "  Deployed daily, mode: $mode" -ForegroundColor Green
    }
    else {
        Write-Host "  Deployment skipped (-SkipDeploy)." -ForegroundColor Yellow
    }
}

# ----------------------------------------------------------------------------
# Execute both HT variants
# ----------------------------------------------------------------------------
try {
    New-SpecExecVariant -Variant 'HT Enabled'  -BaseHex '0x0048' -CollectionName $HtEnabledCollectionName
    New-SpecExecVariant -Variant 'HT Disabled' -BaseHex '0x2048' -CollectionName $HtDisabledCollectionName

    Write-Host ""
    Write-Host "=== Summary ===" -ForegroundColor Cyan
    Write-Host "Reg path:    HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
    Write-Host "Target bits: Intel HT-On 0x00800048 / HT-Off 0x00802048 ; AMD HT-On 0x05000048 / HT-Off 0x05002048 ; Mask 0x3"
    Write-Host "Mode:        $(if ($DetectOnly) { 'Detect only' } else { 'Detect + remediate' })"
    Write-Host "Deployment:  $(if ($SkipDeploy) { 'Skipped' } else { 'Created' })"
    Write-Host "NOTE: a reboot is required on each client after remediation."
}
finally {
    Set-Location $OriginalLocation
}
