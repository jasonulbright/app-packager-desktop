<#
.SYNOPSIS
    The lab proof of the version rule of the per-application supersedence setting (Q-93b-2): the real app publishes the new version n of an application with the rule Exact (operator
    IsEquals) or Minimum (operator GreaterEquals), answers the confirmation window with Yes, and the run reads the stored detection operator and the supersedence relation back from the site.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Probe-SupersedenceRule.ps1 -Run C:\guide\runs\apd-rule -Rule Exact"
    The ConfigMgr console module on the lab client reads and writes the site (the lab user is a site administrator). What the run does:
      1. stages the application with the command-line build into the isolated data folder and reads the stage manifest (title, publisher, version, detection clauses);
      2. makes the throw-away older application "<title> - <older version>" (same publisher, one script deployment type) on the site and records its revision and an XML hash;
      3. for Exact: records the installed value of the title with the command line (the Exact guard needs it for a clause that is not an MSI product clause);
      4. starts the app with the per-application override (supersedence Replace and the version rule), checks the row, runs Check Latest and Publish to ConfigMgr;
      5. when the confirmation window shows, reads the new application from the site (no relation may exist yet), then answers Yes;
      6. after the publish reads the new application back: the operators of the version clauses of its detection, and the relation to the older application;
         the older application must have the same revision and the same XML hash as before;
      7. removes both applications and the content folder of the new one (also when a step fails).
    Lines are PASS, FAIL or INFO. The run ends with "RULE-FAILS=<n>". It creates nothing outside the site objects named above and the run folder.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][ValidateSet('Exact', 'Minimum')][string]$Rule,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$AppFolder = $PSScriptRoot,
    [string]$ApplicationId = 'catalog:06915f526f2e443f',
    [string]$OlderVersion = '4.30',
    [string]$SiteCode = 'MCM',
    [string]$Provider = 'cm01.contoso.com',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$script:GuideTheme = $Theme
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'
$data = Join-Path $Run "data-$Rule"
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$staging = New-RunStaging $data
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Status-Text($w) { (Find-El $w 'StatusLabel' -Timeout 2).Current.Name }

# ---- the site, through the ConfigMgr console module
$module = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Configuration Manager\AdminConsole\bin\ConfigurationManager.psd1'
Import-Module $module -ErrorAction Stop
if (-not (Get-PSDrive -Name $SiteCode -ErrorAction SilentlyContinue)) { $null = New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $Provider }
$home0 = Get-Location
Set-Location "${SiteCode}:"

function Get-AppDigest([string]$Name) {
    $app = Get-CMApplication -Fast -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $app) { return $null }
    $full = Get-CMApplication -Name $Name | Select-Object -First 1
    $dts = @(Get-CMDeploymentType -ApplicationName $Name)
    $xml = ($dts | ForEach-Object { $_.SDMPackageXML }) -join "`n"
    $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$full.SDMPackageXML + $xml))).Replace('-', '')
    [pscustomobject]@{ Name = $Name; CIVersion = $full.CIVersion; Hash = $sha; DeploymentTypes = $dts; AppXml = [string]$full.SDMPackageXML }
}
function Get-DetectionOperators($Digest) {
    $ops = @()
    foreach ($dt in $Digest.DeploymentTypes) {
        $x = [xml]$dt.SDMPackageXML
        foreach ($n in $x.SelectNodes("//*[local-name()='EnhancedDetectionMethod']//*[local-name()='Operator']")) { $ops += $n.InnerText }
    }
    $ops
}
function Get-Relations($Digest) {
    $names = @()
    foreach ($dt in $Digest.DeploymentTypes) {
        $x = [xml]$dt.SDMPackageXML
        foreach ($n in $x.SelectNodes("//*[local-name()='Supersedes']")) { $names += ($n.OuterXml -replace '\s+', ' ') }
    }
    $names
}

$newName = $null; $oldName = $null; $contentDirs = @()
try {
    # ---- 1. stage
    $ErrorActionPreference = 'Continue'
    $out = & $cli --application $ApplicationId --stage --data-root $data --download-root $staging 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Check 'rule-01 the command-line build stages the application' ($exit -eq 0) $out
    $manifestPath = Get-ChildItem $staging -Recurse -Filter stage-manifest.json | Select-Object -First 1
    $m = Get-Content $manifestPath.FullName -Raw | ConvertFrom-Json
    $version = [string]$m.SoftwareVersion
    $publisher = [string]$m.Publisher
    $appName = [string]$m.AppName
    "INFO manifest: AppName='$appName' Publisher='$publisher' Version='$version' InstallerType='$($m.InstallerType)'"
    $clauses = @($m.Detection)
    foreach ($c in $clauses) { "INFO detection clause: $($c | ConvertTo-Json -Compress)" }
    # The title of a stage with supersedence on holds the version ("WizTree (x64) - 4.34"): the older application is the same base title with the older version.
    $base = if ($appName.EndsWith(" - $version")) { $appName.Substring(0, $appName.Length - $version.Length - 3) } else { $appName }
    $newName = "$base - $version"
    $oldName = "$base - $OlderVersion"

    # ---- 2. the older application (throw-away)
    if (Get-AppDigest $newName) { throw "A site application named '$newName' already exists. The run does not touch it." }
    if (Get-AppDigest $oldName) { throw "A site application named '$oldName' already exists. The run does not touch it." }
    $null = New-CMApplication -Name $oldName -Publisher $publisher -SoftwareVersion $OlderVersion
    $null = Add-CMScriptDeploymentType -ApplicationName $oldName -DeploymentTypeName 'ZZAPD old' -InstallCommand 'cmd.exe /c exit 0' -ScriptLanguage PowerShell -ScriptText 'Write-Output 1' `
        -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn -ErrorAction Stop
    $oldBefore = Get-AppDigest $oldName
    Check 'rule-02 the older application exists on the site' ($null -ne $oldBefore) 'not created'
    "INFO older '$oldName' revision $($oldBefore.CIVersion) hash $($oldBefore.Hash.Substring(0, 12))"

    # ---- 3. the installed value for Exact (a clause that is not an MSI product clause needs it)
    $expected = @($clauses | Where-Object { $_.ExpectedValue } | Select-Object -First 1).ExpectedValue
    $isMsi = ([string]$m.InstallerType -eq 'MSI')
    if ($Rule -eq 'Exact') {
        if (-not $isMsi -and $expected) {
            $ErrorActionPreference = 'Continue'
            $rec = & $cli --application $ApplicationId --record-installed-value $expected --version $version --data-root $data 2>&1 | Out-String
            $ErrorActionPreference = 'Stop'
            "INFO record installed value '$expected': exit $LASTEXITCODE $($rec.Trim())"
        }
    }

    # ---- 4. the app, with the override of this application
    $override = @{ Mode = 'Replace'; VersionRule = $Rule }
    # The app rewrites settings.json when it starts, so the installed value that the command line recorded is seeded here again (the same key and value).
    $installed = @{}
    if ($Rule -eq 'Exact' -and -not $isMsi -and $expected) { foreach ($k in @($ApplicationId, ($ApplicationId -replace '^catalog:', ''))) { $installed["$k|$version"] = [string]$expected } }
    $keys = @($ApplicationId, ($ApplicationId -replace '^catalog:', ''))
    $overrides = @{}; foreach ($k in $keys) { $overrides[$k] = $override }
    $settings = @{
        FileShareRoot = $FileShareRoot; DownloadRoot = $staging; RemoveVersionFromTitle = $true
        SiteCode = $SiteCode; ProviderMachine = $Provider; SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
        Supersedence = @{ Mode = 'Off'; TitleOverrides = $overrides; InstalledValues = $installed }
    }
    $null = Start-Apd $exe $data $Theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        $term = ($appName -split ' ')[0]
        Set-ElText (Find-El $w 'txtAppPackagerSearch') $term
        Start-Sleep 2
        $cell = Find-El $w -Name $term -ControlType 'Custom' -Timeout 15
        if (-not $cell) { $cell = Find-El $w -Name "$term (x64)" -ControlType 'Custom' -Timeout 5 }
        if (-not $cell) { $cell = Find-El $w -Name $base -ControlType 'Custom' -Timeout 5 }
        if (-not $cell) { throw "The grid has no row named '$term'." }
        Click-El $cell; Start-Sleep 1
        $grid = Find-El $w 'PackagerGrid'
        foreach ($box in $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))) {
            if ($box.Current.AutomationId -eq 'SelectAllCheckBox') { continue }
            if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
        }
        Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
        Start-Sleep 1
        Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
        Start-Sleep 5
        $deadline = (Get-Date).AddSeconds(180)
        while ((Get-Date) -lt $deadline -and (Status-Text $w) -match 'Check|Checking') { Start-Sleep 2 }
        "INFO status after Check Latest: $(Status-Text $w)"

        $w = Main-Window
        Real-Click (Find-El $w 'btnPublishConfigMgr')
        $logFile = Join-Path (Split-Path (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName) 'SRLPackr\packr.log'
        $answered = $false; $finished = $false
        $deadline = (Get-Date).AddSeconds(420)
        while ((Get-Date) -lt $deadline -and -not $finished) {
            $yes = Find-El $null 'btnSupersedeYes' -Timeout 2
            if ($yes -and -not $answered) {
                "INFO confirmation: $((Find-El $null 'txtSupersedeOld' -Timeout 2).Current.Name) / $((Find-El $null 'txtSupersedeNew' -Timeout 2).Current.Name)"
                # Before Yes: the new application has no relation (it may not be on the site yet: the relation is written after the answer).
                $before = Get-AppDigest $newName
                Check 'rule-03 at the window the new application has no relation yet' ($null -eq $before -or @(Get-Relations $before).Count -eq 0) "exists: $($null -ne $before); relations: $(if ($before) { @(Get-Relations $before).Count } else { 0 })"
                "INFO at the window the new application is on the site: $($null -ne $before)"
                Move-MouseAway; Shot "probe-rule-$Rule-confirm"
                Real-Click $yes
                $answered = $true
                Start-Sleep 2
                continue
            }
            $primary = Find-El $null 'PrimaryButton' -Timeout 1
            if ($primary) { 'INFO a dialog with a primary button is open'; Real-Click $primary; Start-Sleep 2; continue }
            $text = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
            if ($text -match '(?m)^\S+ \S+  Packaged\.' -or $text -match 'Package FAILED' -or $text -match 'declined at the confirmation|cancelled|Cancelled') { $finished = $true }
            Start-Sleep 3
        }
        Check 'rule-04 the publish finished' $finished 'timeout'
        Check 'rule-05 the confirmation window was shown and answered Yes' $answered 'no window'
        Move-MouseAway
        '--- log lines ---'
        Get-Content $logFile | Where-Object { $_ -match 'Supersedence|Packaged|Package FAILED|Chain check|Application title|Version rule|declined|skipped|Exact' } | ForEach-Object { if ($_.Length -gt 250) { $_.Substring(0, 250) } else { $_ } }
        '--- end ---'
        Shot "probe-rule-$Rule-log"
    }
    finally { Stop-Apd }

    # ---- 6. read back
    $after = Get-AppDigest $newName
    Check 'rule-06 the new application is on the site' ($null -ne $after) 'missing'
    if ($after) {
        $ops = @(Get-DetectionOperators $after)
        "INFO detection operators read back from '$newName': $($ops -join ', ')"
        $want = if ($Rule -eq 'Exact') { 'Equals' } else { 'GreaterEquals' }
        $other = if ($Rule -eq 'Exact') { 'GreaterEquals' } else { 'Equals' }
        Check "rule-07 the stored version operator is $want ($Rule)" (($ops -contains $want) -and -not ($ops -contains $other)) ("operators: " + ($ops -join ', '))
        $rel = @(Get-Relations $after)
        "INFO relations on '$newName': $($rel.Count)"
        foreach ($r in $rel) { "INFO relation: $($r.Substring(0, [Math]::Min(400, $r.Length)))" }
        Check 'rule-08 the relation on the new application names the older application' ($rel.Count -ge 1 -and (($rel -join ' ') -match [regex]::Escape($oldBefore.DeploymentTypes[0].LocalizedDisplayName) -or ($rel -join ' ') -match 'ScopeId_')) 'no relation'
    }
    $oldAfter = Get-AppDigest $oldName
    Check 'rule-09 the older application is unchanged (same revision and hash)' ($oldAfter.CIVersion -eq $oldBefore.CIVersion -and $oldAfter.Hash -eq $oldBefore.Hash) "revision $($oldBefore.CIVersion) -> $($oldAfter.CIVersion)"
    if ($after) { foreach ($dt in $after.DeploymentTypes) { $x = [xml]$dt.SDMPackageXML; foreach ($n in $x.SelectNodes("//*[local-name()='Location']")) { if ($n.InnerText -like '\\*') { $contentDirs += $n.InnerText } } } }
}
finally {
    # ---- 7. cleanup: the new application first (it supersedes the older one), then the older one, then the content folder
    try { if ($newName -and (Get-AppDigest $newName)) { Remove-CMApplication -Name $newName -Force -ErrorAction Stop; "INFO removed '$newName'" } } catch { "FAIL cleanup of '$newName': $($_.Exception.Message)"; $script:Fails++ }
    try { if ($oldName -and (Get-AppDigest $oldName)) { Remove-CMApplication -Name $oldName -Force -ErrorAction Stop; "INFO removed '$oldName'" } } catch { "FAIL cleanup of '$oldName': $($_.Exception.Message)"; $script:Fails++ }
    foreach ($d in ($contentDirs | Select-Object -Unique)) {
        $leaf = $d.TrimEnd('\')
        if ($leaf -like "$FileShareRoot*" -and (Test-Path -LiteralPath $leaf)) { Remove-Item -LiteralPath $leaf -Recurse -Force -ErrorAction SilentlyContinue; "INFO removed content folder $leaf" }
    }
    $left = @(Get-CMApplication -Fast | Where-Object { $_.LocalizedDisplayName -in @($newName, $oldName) })
    Check 'rule-10 no test application is left on the site' ($left.Count -eq 0) ($left.LocalizedDisplayName -join ', ')
    Set-Location $home0
}
"RULE-FAILS=$script:Fails"

