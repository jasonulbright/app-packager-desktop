<#
.SYNOPSIS
    The cleanup of what a guide capture run publishes: ConfigMgr applications, content folders on the file share and locally published WSUS updates.
    Each part removes only what the log of the run proves that the run created, and says what it leaves.

.DESCRIPTION
    Dot-source it after ApdUiDriver.ps1. It runs on the lab client, with the ConfigMgr console. The captures publish only applications of the ZZAPD guide
    definitions (folder definitions).
      - An application is removed when the log holds its ModelName directly before 'Application created successfully', the ModelName was not on the site
        before the run, the title starts with ZZAPD and the log names that title in a 'Packaged:' line. A ModelName stays the same through every revision.
      - A content folder is removed when the log names it in a 'Network content path:' line, it did not exist before the run, a part of its path starts with
        ZZAPD, and every application of the run was removed.
      - A WSUS update is removed when the log says that this run published it. An update that was 'already on the server' is never removed.
#>
$script:GuideSiteCode = 'MCM'

# Opens the site drive. Throws when the console or the site is not there.
function Connect-GuideSite([string]$SiteCode = 'MCM', [string]$Server = 'cm01.contoso.com') {
    $script:GuideSiteCode = $SiteCode
    $ui = $env:SMS_ADMIN_UI_PATH
    if (-not $ui) { throw 'The ConfigMgr console is not installed on this computer.' }
    $module = @((Join-Path $ui 'ConfigurationManager.psd1'), (Join-Path (Split-Path $ui -Parent) 'ConfigurationManager.psd1')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $module) { throw "No ConfigurationManager.psd1 beside $ui." }
    Import-Module $module -ErrorAction Stop -Global
    if (-not (Get-PSDrive -Name $SiteCode -ErrorAction SilentlyContinue)) { $null = New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $Server -Scope Global -ErrorAction Stop }
}

# Runs a script block on the site drive and returns to the folder of before.
function Invoke-GuideSite([scriptblock]$Body) {
    Push-Location "$($script:GuideSiteCode):"
    try { & $Body } finally { Pop-Location }
}

# The applications of the site whose title starts with ZZAPD. Throws when the site cannot be read.
function Get-GuideApplications { @(Invoke-GuideSite { Get-CMApplication -Fast -Name 'ZZAPD*' -ErrorAction Stop }) }

function Format-GuideApp($App) { "CI_ID $($App.CI_ID) '$($App.LocalizedDisplayName)' version '$($App.SoftwareVersion)'" }

function Get-GuideRunLog([string]$DataRoot) {
    @(Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Filter 'packr.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

# The ModelNames that the log proves this run created, and the titles that it packaged.
function Get-GuidePublishEvidence([string]$DataRoot) {
    $log = Get-GuideRunLog $DataRoot
    $models = New-Object System.Collections.Generic.List[string]
    $pending = $null
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match 'Final ModelName: (ScopeId_[A-Za-z0-9-]+/Application_[A-Za-z0-9-]+)\s*$') { $pending = $Matches[1] }
        elseif ($line -match 'Application created successfully') { if ($pending) { $models.Add($pending) }; $pending = $null }
        elseif ($line -match 'Application already exists') { $pending = $null }
    }
    [pscustomobject]@{
        Models   = @($models | Select-Object -Unique)
        Titles   = @([regex]::Matches($log, '(?m)  Packaged: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        Replaced = [bool]($log -match 'replacing deployment types')
    }
}

# Removes the applications that the evidence proves, and no other. Returns lines of text; the last one is 'site: all applications of the run are removed'
# only when no new ZZAPD application is left.
function Remove-GuideRunApplications($Evidence, [string[]]$BeforeModels) {
    $now = @(Get-GuideApplications)
    $left = 0
    foreach ($app in $now) {
        $model = [string]$app.ModelName
        if ($BeforeModels -contains $model) { continue }
        $proven = $Evidence -and -not $Evidence.Replaced -and $Evidence.Models -contains $model -and
            $app.LocalizedDisplayName.StartsWith('ZZAPD', [StringComparison]::Ordinal) -and $Evidence.Titles -ccontains $app.LocalizedDisplayName
        if (-not $proven) { $left++; "LEFT on the site: $(Format-GuideApp $app) (new since the start of the run, and the log does not prove that the run created it)"; continue }
        try {
            Invoke-GuideSite { Remove-CMApplication -InputObject (Get-CMApplication -ModelName $model -ErrorAction Stop) -Force -ErrorAction Stop }
            "site: removed $(Format-GuideApp $app)"
        }
        catch { $left++; "LEFT on the site: $(Format-GuideApp $app): $($_.Exception.Message)" }
    }
    if ($left -eq 0) { 'site: all applications of the run are removed' }
}

# Every folder of the ZZAPD branches of the file share, at any depth below the first ZZAPD folder, without the content folder of the seed script.
# Throws when the share cannot be read completely: an incomplete list of before would make an old folder look new.
function Get-GuideShareFolders([string]$Share) {
    $isGuide = { $below = $_.FullName.Substring($Share.TrimEnd('\').Length); $below -match '\\ZZAPD' -and $below -notmatch '\\ZZAPD-Seed-' }
    $found = @{}
    foreach ($top in @(Get-ChildItem -LiteralPath $Share -Directory -Recurse -Depth 4 -ErrorAction Stop | Where-Object $isGuide)) {
        $found[$top.FullName] = $true
        foreach ($below in @(Get-ChildItem -LiteralPath $top.FullName -Directory -Recurse -ErrorAction Stop)) { $found[$below.FullName] = $true }
    }
    @($found.Keys)
}

# Removes the content folders that the log of this run names in 'Network content path:' lines and that did not exist before the run. No other folder is
# searched for. A parent is removed only when it is empty, is a ZZAPD folder or below one, and did not exist before the run.
function Remove-GuideRunContent([string]$DataRoot, [string]$Share, [string[]]$Before) {
    $root = $Share.TrimEnd('\')
    $paths = @([regex]::Matches((Get-GuideRunLog $DataRoot), '(?m)Network content path: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value.TrimEnd('\') } | Select-Object -Unique)
    if (-not $paths) { 'share: the log names no network content path, nothing removed'; return }
    $inside = { param($path) $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and $path -notmatch '\\\.\.(\\|$)' -and
        $path.Substring($root.Length) -match '\\ZZAPD' -and $path.Substring($root.Length) -notmatch '\\ZZAPD-Seed-' }
    foreach ($path in $paths) {
        if (-not (& $inside $path)) { "LEFT on the share: $path (not a ZZAPD folder below $root)"; continue }
        if ($Before -contains $path) { "LEFT on the share: $path (it existed before the run)"; continue }
        if (-not (Test-Path -LiteralPath $path)) { "share: already gone: $path"; continue }
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            "share: removed $path"
            $parent = Split-Path $path -Parent
            while ((& $inside $parent) -and $Before -notcontains $parent -and -not (Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop | Select-Object -First 1)) {
                # No -Recurse: the call fails when the folder is not empty any more.
                Remove-Item -LiteralPath $parent -Force -ErrorAction Stop
                "share: removed the empty folder $parent"
                $parent = Split-Path $parent -Parent
            }
        }
        catch { "LEFT on the share: $($path): $($_.Exception.Message)" }
    }
}

# The whole ConfigMgr cleanup of one data folder.
function Complete-GuideSiteCleanup([string]$DataRoot, [string[]]$BeforeModels, [string]$Share, [string[]]$BeforeFolders) {
    $result = @(Remove-GuideRunApplications (Get-GuidePublishEvidence $DataRoot) $BeforeModels)
    $result
    if ($result[-1] -eq 'site: all applications of the run are removed') { Remove-GuideRunContent $DataRoot $Share $BeforeFolders }
    else { 'share: no folder removed, because an application of the run can still use it' }
}

# The records of the seed script (a copy of its record file lies beside the capture scripts).
function Read-GuideRecordCopy([string]$RecordFile) {
    if (-not (Test-Path -LiteralPath $RecordFile)) { throw "There is no record file $RecordFile. Copy the record file of the seed script beside the capture scripts." }
    $parsed = Get-Content -LiteralPath $RecordFile -Raw | ConvertFrom-Json
    @($parsed | ForEach-Object { $_ })
}

# Before a run that deletes or removes content of packages: the record holds exactly -Names, each recorded PackageID still has its recorded name on the
# site, and no other package or application of the site has a name that holds -Filter (the text that the run types into the filter of the grid).
# Returns the recorded packages as objects with Id and Name. Throws, so the run does not start, when anything differs.
function Get-GuideRecordedPackages([string]$RecordFile, [string[]]$Names, [string]$Filter) {
    $records = @(Read-GuideRecordCopy $RecordFile | Where-Object { $_.Type -eq 'Package' -and $Names -ccontains $_.Name })
    foreach ($name in $Names) {
        if (@($records | Where-Object { $_.Name -ceq $name }).Count -ne 1) { throw "The record file must hold exactly one package '$name'." }
    }
    foreach ($r in $records) {
        $package = Invoke-GuideSite { Get-CMPackage -Id $r.Id -Fast -ErrorAction Stop }
        if (-not $package -or [string]$package.Name -cne $r.Name) { throw "The recorded PackageID $($r.Id) does not have the name '$($r.Name)' on the site. The run does not start." }
    }
    $pattern = '*' + [System.Management.Automation.WildcardPattern]::Escape($Filter) + '*'
    $others = @(Invoke-GuideSite { Get-CMPackage -Fast -ErrorAction Stop } | Where-Object { $_.Name -like $pattern -and @($records | ForEach-Object { $_.Id }) -notcontains $_.PackageID })
    $others += @(Invoke-GuideSite { Get-CMApplication -Fast -ErrorAction Stop } | Where-Object { $_.LocalizedDisplayName -like $pattern })
    if ($others) { throw "$($others.Count) other object(s) of the site match the filter '$Filter'. The run does not start." }
    @($records | ForEach-Object { [pscustomobject]@{ Id = [string]$_.Id; Name = [string]$_.Name } })
}

# Removes the WSUS updates that the log says this run published. -AdapterPath is WsusAdapter.ps1 of the application.
function Remove-GuideRunUpdates([string]$DataRoot, [string]$Server, [int]$Port, [string]$AdapterPath) {
    $found = @([regex]::Matches((Get-GuideRunLog $DataRoot), '(published|already on the server) ([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12});') |
        ForEach-Object { '{0}|{1}' -f $_.Groups[2].Value, $_.Groups[1].Value } | Select-Object -Unique)
    if (-not $found) { 'WSUS: the log names no update id; if the publish reached the server, check WSUS Updates by hand'; return }
    foreach ($entry in $found) {
        $id, $what = $entry -split '\|'
        if ($what -ne 'published') { "LEFT on WSUS: update $id was already on the server. This run did not create it."; continue }
        if (-not (Test-Path -LiteralPath $AdapterPath)) { "LEFT on WSUS: update $id, published by this run (no WsusAdapter.ps1 at $AdapterPath)"; continue }
        try {
            . $AdapterPath
            $removal = Invoke-WsusSetStateOperation -Request ([pscustomobject]@{ Settings = [pscustomobject]@{ ServerName = $Server; PortNumber = $Port; UseSsl = $false }; Action = 'Remove'; PackageIds = @($id) })
            foreach ($r in @($removal.Results)) { if ($r.Found -and $r.Ok -and -not $r.FolderError) { "WSUS: removed the update $id of this run" } else { "LEFT on WSUS: update $($id): $($r.Message) $($r.FolderError)" } }
        }
        catch { "LEFT on WSUS: update $($id): $($_.Exception.Message)" }
    }
}
