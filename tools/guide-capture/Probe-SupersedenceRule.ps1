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
         the publish review is confirmed only when it lists exactly -ApplicationName for ConfigMgr, with the create action and the supersedence step;
         else the run selects Back;
      5. when the confirmation window shows, reads the new application from the site (no relation may exist yet), then answers Yes;
      6. after the publish reads the new application back: the operators of the version clauses of its detection, and the relation to the older application;
         the older application must have the same revision and the same XML hash as before;
      7. removes both applications and the content folder of the new one (also when a step fails).
    Lines are PASS, FAIL or INFO. The run ends with "RULE-FAILS=<n>". It creates nothing outside the site objects named above, their content and the run folder.
    Cleanup removes only what this run created and can prove it created: the older application by the CI_ID and ModelName that its creation returned,
    the new application only when the log of the run says that the publish created it and the ModelName of the log is the one on the site, and a content
    folder of that application only when the share did not hold it before the publish. Anything else stays, and a line starting with LEFT names it.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][ValidateSet('Exact', 'Minimum')][string]$Rule,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$AppFolder = $PSScriptRoot,
    [string]$ApplicationId = 'catalog:06915f526f2e443f',
    # The name of the application of -ApplicationId in the Application column of the publish review.
    [string]$ApplicationName = 'WizTree (x64)',
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

# What the run created, and what it needs to prove that. A record is set only after the creation succeeded.
$newName = $null; $oldName = $null; $oldRecord = $null; $newRecord = $null; $published = $false; $shareBefore = $null; $logFile = $null
$shareRoot = $FileShareRoot.TrimEnd('\')
$shareDepth = 5

# The record of the new application from the log of the run: only when the log says that the publish created it, and the site shows an application of
# that title whose ModelName is the one the publish printed. Every save gives a new CI_ID; the ModelName stays.
function Get-NewRecord {
    if (-not $published -or -not $logFile -or -not (Test-Path -LiteralPath $logFile)) { return $null }
    $log = Get-Content -LiteralPath $logFile -Raw
    if ($log -notmatch 'Creating application\.\.\.' -or $log -notmatch 'Application created successfully') { return $null }
    $models = @([regex]::Matches($log, 'Final ModelName: (ScopeId_[A-Za-z0-9-]+/Application_[A-Za-z0-9-]+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($models.Count -ne 1) { return $null }
    $found = @(Get-CMApplication -Fast -Name $newName -ErrorAction Stop | Where-Object { $_.LocalizedDisplayName -ceq $newName -and [string]$_.ModelName -ceq $models[0] })
    if ($found.Count -ne 1) { return $null }
    @{ CiId = [int]$found[0].CI_ID; ModelName = $models[0]; Name = $newName }
}

# Removes the application of a record, found by its CI_ID, when its name and ModelName are still the recorded ones. $true when it was removed.
function Remove-Recorded($Record) {
    $app = Get-CMApplication -Id $Record.CiId -ErrorAction Stop
    if (-not $app -or [string]$app.ModelName -cne $Record.ModelName -or $app.LocalizedDisplayName -cne $Record.Name) {
        Write-Host "LEFT application '$($Record.Name)' (recorded CI_ID $($Record.CiId)): the site does not show it under that id with the same name and ModelName."
        return $false
    }
    Remove-CMApplication -InputObject $app -Force -ErrorAction Stop
    Write-Host "INFO removed '$($Record.Name)' (CI_ID $($Record.CiId))"
    $true
}
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
    $made = New-CMApplication -Name $oldName -Publisher $publisher -SoftwareVersion $OlderVersion -ErrorAction Stop
    $oldRecord = @{ CiId = [int]$made.CI_ID; ModelName = [string]$made.ModelName; Name = $oldName }
    $null = Add-CMScriptDeploymentType -ApplicationName $oldName -DeploymentTypeName 'ZZAPD old' -InstallCommand 'cmd.exe /c exit 0' -ScriptLanguage PowerShell -ScriptText 'Write-Output 1' `
        -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn -ErrorAction Stop
    # The new deployment type made a new revision with a new CI_ID: record that one, for the same ModelName only.
    $now = @(Get-CMApplication -Fast -Name $oldName -ErrorAction Stop | Where-Object { [string]$_.ModelName -ceq $oldRecord.ModelName })
    if ($now.Count -eq 1) { $oldRecord.CiId = [int]$now[0].CI_ID }
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
            if ($box.Current.AutomationId -eq 'chkSelectAllPackages') { continue }
            if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
        }
        Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
        Start-Sleep 1
        Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
        Start-Sleep 5
        $deadline = (Get-Date).AddSeconds(180)
        while ((Get-Date) -lt $deadline -and (Status-Text $w) -match 'Check|Checking') { Start-Sleep 2 }
        "INFO status after Check Latest: $(Status-Text $w)"

        # The folders of the content share before the publish: only a folder that is not in this list can be one that this run made. When the share
        # cannot be listed, the run removes no folder. The location is the site drive, so the share is named with its provider.
        try {
            $shareBefore = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($d in (Get-ChildItem -LiteralPath "FileSystem::$shareRoot" -Directory -Recurse -Depth $shareDepth -ErrorAction Stop)) { $null = $shareBefore.Add($d.FullName.TrimEnd('\')) }
        } catch { $shareBefore = $null; "INFO the content share could not be listed, so no content folder will be removed: $($_.Exception.Message)" }
        $w = Main-Window
        Assert-ApdSelection $w 1
        $published = $true
        Real-Click (Find-El $w 'btnPublishAll')
        $createText = 'Create application and deployment type'
        $reviewCells = @{ ConfigMgr = "$createText. Then: apply supersedence", "$createText (ConfigMgr has $OlderVersion; if the title matches, the run asks before it replaces it). Then: apply supersedence" }
        $reviews = 0
        $logFile = Join-Path (Split-Path (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName) 'SRLPackr\packr.log'
        $answered = $false; $finished = $false
        $deadline = (Get-Date).AddSeconds(420)
        while ((Get-Date) -lt $deadline -and -not $finished) {
            $review = Find-El $null 'btnReviewConfirm' -Timeout 1
            if ($review -and -not $review.Current.IsOffscreen) {
                if (++$reviews -gt 3) { $back = Find-El $null 'btnReviewBack' -Timeout 2; if ($back) { Real-Click $back }; throw 'The publish review opened more than three times.' }
                Confirm-ApdReview -Applications $ApplicationName -Systems 'ConfigMgr' -CellTexts $reviewCells
                continue
            }
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
            # Application Already Exists is answered with Skip and fails the run; another dialog is clicked only when its title is expected.
            Resolve-ApdContentDialog (Main-Window) @()
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
}
finally {
    # ---- 7. cleanup: the new application first (it supersedes the older one), then the older one, then the content folders of the new one
    $contentDirs = @(); $newRemoved = $false
    try {
        $newRecord = Get-NewRecord
        if ($newRecord) {
            $made = Get-AppDigest $newName
            if ($made) { foreach ($dt in $made.DeploymentTypes) { $x = [xml]$dt.SDMPackageXML; foreach ($n in $x.SelectNodes("//*[local-name()='Location']")) { if ($n.InnerText -like '\\*') { $contentDirs += $n.InnerText.TrimEnd('\') } } } }
            $newRemoved = Remove-Recorded $newRecord
        }
        elseif ($published) { $script:Fails++; "LEFT: the log and the site do not prove that this run created '$newName'. Nothing of that name and no content folder was removed; check the site." }
    } catch { "FAIL cleanup of '$newName': $($_.Exception.Message)"; $script:Fails++ }
    try { if ($oldRecord) { $null = Remove-Recorded $oldRecord } } catch { "FAIL cleanup of '$oldName': $($_.Exception.Message)"; $script:Fails++ }
    foreach ($leaf in @($contentDirs | Select-Object -Unique)) {
        $inside = $leaf.StartsWith("$shareRoot\", [StringComparison]::OrdinalIgnoreCase)
        $parts = if ($inside) { @($leaf.Substring($shareRoot.Length + 1) -split '\\') } else { @() }
        $isNew = $newRemoved -and $inside -and $null -ne $shareBefore -and $parts.Count -ge 1 -and $parts.Count -le ($shareDepth + 1) -and
            @($parts | Where-Object { $_ -in '', '.', '..' }).Count -eq 0 -and -not $shareBefore.Contains($leaf)
        if (-not $isNew) { "LEFT content folder $leaf : this run cannot prove that it made it"; continue }
        try {
            if (Test-Path -LiteralPath "FileSystem::$leaf") { Remove-Item -LiteralPath "FileSystem::$leaf" -Recurse -Force -ErrorAction Stop; "INFO removed content folder $leaf" }
        } catch { "LEFT content folder $leaf : $($_.Exception.Message)" }
    }
    $left = @(foreach ($r in @($newRecord, $oldRecord)) { if ($r) { Get-CMApplication -Fast -Name $r.Name | Where-Object { [string]$_.ModelName -ceq $r.ModelName } } })
    Check 'rule-10 no application that this run created is left on the site' ($left.Count -eq 0) ($left.LocalizedDisplayName -join ', ')
    Set-Location $home0
}
"RULE-FAILS=$script:Fails"

