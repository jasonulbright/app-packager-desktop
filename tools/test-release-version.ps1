<#
.SYNOPSIS
    Checks a release version of the form YYYY.MDD.N and its place in the tag sequence.

.DESCRIPTION
    Field 1 is the year. Field 2 is the month and day as one integer without zero
    padding (September 30 is 930, October 3 is 1003). Field 3 is the running release
    number. No field has a leading zero and every field is below 65536, so the version
    is valid as a three-part numeric version and in a Windows version resource.

    The release number of the first release is 1. Every later release number follows
    the newest date tag by one, and its date is not earlier than the date of that tag.

    Throws with the broken rule. Returns nothing when the version passes.

.PARAMETER Version
    The version without a leading v.

.PARAMETER ExistingTag
    The tags of the repository (git tag -l). Tags of another form are ignored.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string[]]$ExistingTag = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$firstReleaseNumber = 1

function ConvertFrom-DateVersion {
    param([string]$Text)
    if ($Text -notmatch '^(\d{4})\.(\d{3,4})\.(\d+)$') { throw "'$Text' is not of the form YYYY.MDD.N." }
    $fields = @($Matches[1], $Matches[2], $Matches[3])
    foreach ($field in $fields) {
        if ($field.Length -gt 1 -and $field.StartsWith('0')) { throw "'$Text' has a leading zero in '$field'." }
        if ([long]$field -ge 65536) { throw "'$Text': field $field is not below 65536." }
    }
    $year = [int]$fields[0]
    $month = [int][math]::Floor([int]$fields[1] / 100)
    $day = [int]$fields[1] % 100
    if ($month -lt 1 -or $month -gt 12) { throw "'$Text': month $month does not exist." }
    if ($day -lt 1 -or $day -gt [datetime]::DaysInMonth($year, $month)) { throw "'$Text': day $day does not exist in $year-$('{0:d2}' -f $month)." }
    $number = [int]$fields[2]
    if ($number -lt $firstReleaseNumber) { throw "'$Text': release number $number is below the first date release ($firstReleaseNumber)." }
    [pscustomobject]@{ Date = [datetime]::new($year, $month, $day); Number = $number; Text = $Text }
}

$candidate = ConvertFrom-DateVersion $Version

$released = foreach ($tag in $ExistingTag) {
    $tag = "$tag".Trim()
    if (-not $tag.StartsWith('v') -or $tag -eq "v$Version") { continue }
    try { ConvertFrom-DateVersion $tag.Substring(1) } catch { }
}
$newest = $released | Sort-Object Number | Select-Object -Last 1

if (-not $newest) {
    if ($candidate.Number -ne $firstReleaseNumber) {
        throw "'$Version': the first date release takes number $firstReleaseNumber."
    }
    return
}
if ($candidate.Number -ne $newest.Number + 1) {
    throw "'$Version': release number $($candidate.Number) does not follow $($newest.Text) by one."
}
if ($candidate.Date -lt $newest.Date) {
    throw "'$Version': the date is earlier than the date of $($newest.Text)."
}
