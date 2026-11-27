<#
.SYNOPSIS
    Refuses a release unless the CI workflow is green on the commit being released.

.DESCRIPTION
    Reads the CI runs of the repository as JSON (gh run list --workflow ci.yml --json
    databaseId,status,conclusion,event,createdAt,headSha,displayTitle) and decides for one commit:

    - A filtered run counts for nothing, whether it is queued, running or finished. It is a
      workflow_dispatch run with a displayTitle that starts with "[filtered]" (run-name in
      ci.yml). It ran only some tests, so it cannot pass and cannot block the commit.
    - A full run of the commit that is still queued or running refuses the release.
    - A cancelled run counts for nothing (a newer push cancels the older run).
    - Of the other full runs of the commit the newest decides: it must have succeeded.
    - A commit with no usable run is refused.
    - A run of the commit with no event or no displayTitle refuses the release, because the
      check cannot tell if the run was filtered.

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

foreach ($run in $mine) {
    foreach ($field in 'event', 'displayTitle') {
        $property = $run.PSObject.Properties[$field]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "The run list has no $field for a CI run on commit $Sha, so a filtered run cannot be identified; add $field to gh run list --json."
        }
    }
}

# Only a manual run takes a filter. The title of a push run is the commit message, so the marker means nothing there.
$mine = @($mine | Where-Object { -not ($_.event -eq 'workflow_dispatch' -and $_.displayTitle.StartsWith('[filtered]', [StringComparison]::Ordinal)) })
if ($mine.Count -eq 0) { throw "No full CI run exists for commit $Sha; each run on it was filtered. Run the CI workflow on it with an empty filter and wait for it to pass." }

$open = @($mine | Where-Object { $_.status -ne 'completed' })
if ($open.Count -gt 0) { throw "CI run $($open[0].databaseId) on commit $Sha is still $($open[0].status); wait for it to finish." }

$counted = @($mine | Where-Object { $_.conclusion -ne 'cancelled' } | Sort-Object { [DateTime]$_.createdAt } -Descending)
if ($counted.Count -eq 0) { throw "Every full CI run on commit $Sha was cancelled; run the CI workflow on it again." }

$newest = $counted[0]
if ($newest.conclusion -ne 'success') { throw "The newest CI run $($newest.databaseId) on commit $Sha ended $($newest.conclusion); fix the failure before releasing." }

Write-Host "CI run $($newest.databaseId) on commit $Sha succeeded."
