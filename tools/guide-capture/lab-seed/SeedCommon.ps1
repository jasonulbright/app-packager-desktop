<#
.SYNOPSIS
    What the seed, check and cleanup scripts of the guide captures share: a remote session to the ConfigMgr lab site with the console module on
    the site drive, and the record file. Dot-source it.

.DESCRIPTION
    The record file is a JSON array on the development computer. One entry for each object that a seed run created:
        { "Type": "Application", "Id": "<stable id>", "Name": "<name>", "Created": "<time>" }
    Stable ids: Application the ModelName (a CI_ID changes with every revision); Collection the CollectionID; Package and TaskSequence the PackageID;
    Script the ScriptGuid; Folder the full path on CM01 (the name is the text of the marker file in the folder).
#>

# A remote session with the console module loaded and the site drive as the location.
function New-GuideSiteSession([string]$Target, [string]$SiteCode, [string]$Provider, [string]$CredentialScript) {
    . $CredentialScript
    $session = New-PSSession -ComputerName $Target -Credential $LabCredential
    try {
        Invoke-Command -Session $session -ScriptBlock {
            param($code, $provider)
            $ErrorActionPreference = 'Stop'
            Import-Module (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'ConfigurationManager.psd1')
            if (-not (Get-PSDrive -Name $code -ErrorAction SilentlyContinue)) { $null = New-PSDrive -Name $code -PSProvider CMSite -Root $provider }
            Set-Location "${code}:"
        } -ArgumentList $SiteCode, $Provider
    }
    catch { Remove-PSSession $session; throw }
    $session
}

function Read-GuideRecords([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $text = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }
    # ConvertFrom-Json of Windows PowerShell 5.1 returns an array as one object, so it is unrolled here.
    $parsed = $text | ConvertFrom-Json
    @($parsed | ForEach-Object { $_ })
}

# Writes a temporary file beside the record file and then replaces the record file with it; the file of before stays as <record file>.bak.
# -Path is a full path.
function Save-GuideRecords([string]$Path, [object[]]$Records) {
    $json = if (@($Records).Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject @($Records) -Depth 4 }
    $temp = "$Path.tmp"
    Set-Content -LiteralPath $temp -Value $json -Encoding UTF8 -ErrorAction Stop
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, "$Path.bak") }
    else { [IO.File]::Move($temp, $Path) }
}

# Proves before the first call to the site that the record file can be written: it reads the records (a damaged file stops here) and saves them again,
# twice, so that the replace with its backup has worked once.
function Assert-GuideRecordWritable([string]$Path) {
    $folder = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "The folder $folder of the record file does not exist." }
    $records = @(Read-GuideRecords $Path)
    Save-GuideRecords $Path $records
    Save-GuideRecords $Path $records
}

# Written at once after each object is created, so a run that fails later still leaves a record of what it made.
function Add-GuideRecord([string]$Path, [string]$Type, [string]$Id, [string]$Name) {
    if (-not $Id -or -not $Name) { throw "A record needs an id and a name ($Type)." }
    $records = @(Read-GuideRecords $Path)
    $records += [pscustomobject]@{ Type = $Type; Id = $Id; Name = $Name; Created = (Get-Date -Format s) }
    Save-GuideRecords $Path $records
    "recorded $Type '$Name' id $Id"
}
