<#
.SYNOPSIS
    Refuses a release unless the CI workflow is green on the commit being released.

.DESCRIPTION
    Reads the CI runs of the repository as JSON (gh run list --workflow ci.yml --json
    databaseId,status,conclusion,event,createdAt,headSha) and decides for one commit:

    - A run of the commit that is still queued or running refuses the release.
    - A cancelled run counts for nothing (a newer push cancels the older run).
    - Of the other runs of the commit the newest decides: it must have succeeded.
    - A commit with no usable run is refused.

    Throws with the reason. Prints the run that passed otherwise.

.PARAMETER Sha
    The full commit SHA being released.

.PARAMETER RunsJson
    The JSON text from gh run list.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sha,
    [Parameter(Mandatory)][AllowEmptyString()][string]$RunsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runs = @()
# Windows PowerShell 5.1 hands a JSON array on as one object; the loop gives one run per item in both editions.
if ($RunsJson.Trim()) { $runs = @(ConvertFrom-Json -InputObject $RunsJson | ForEach-Object { $_ }) }
$mine = @($runs | Where-Object { $_.headSha -eq $Sha })

if ($mine.Count -eq 0) { throw "No CI run exists for commit $Sha; run the CI workflow on it and wait for it to pass." }

$open = @($mine | Where-Object { $_.status -ne 'completed' })
if ($open.Count -gt 0) { throw "CI run $($open[0].databaseId) on commit $Sha is still $($open[0].status); wait for it to finish." }

$counted = @($mine | Where-Object { $_.conclusion -ne 'cancelled' } | Sort-Object { [DateTime]$_.createdAt } -Descending)
if ($counted.Count -eq 0) { throw "Every CI run on commit $Sha was cancelled; run the CI workflow on it again." }

$newest = $counted[0]
if ($newest.conclusion -ne 'success') { throw "The newest CI run $($newest.databaseId) on commit $Sha ended $($newest.conclusion); fix the failure before releasing." }

Write-Host "CI run $($newest.databaseId) on commit $Sha succeeded."
