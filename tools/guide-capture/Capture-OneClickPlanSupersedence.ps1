<#
.SYNOPSIS
    The lab capture run of the One Click plan with the step Supersedence (decision D-21): one tracked application whose ConfigMgr publish left the supersedence
    step pending. The run writes that state into the inventory of its own data folder; it publishes nothing and never selects Run.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app with these
    scripts, the folder definitions and the published tool SeedPendingSupersedence in the folder seed-tool (see RUNBOOK.md):
        & .\Capture-OneClickPlanSupersedence.ps1 -Run <work folder>
    The images are <Run>\images\<theme>\core-one-click-plan-supersedence.png. The application is the guide definition zzapd-everything. For each theme the run
    stages it with the command-line build (this gives the latest version), writes the pending record for that version, opens One Click, saves the image and
    closes the window.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$SeedTool = (Join-Path $PSScriptRoot 'seed-tool\SeedPendingSupersedence.exe'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'
if (-not (Test-Path -LiteralPath $SeedTool)) { throw "no seed tool at $SeedTool (give -SeedTool)" }
$id = 'zzapd00000000001'

foreach ($theme in $Themes) {
    $script:GuideTheme = $theme
    $data = Join-Path $script:GuideRun "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    Install-GuideDefinition $data @('zzapd-everything')

    $ErrorActionPreference = 'Continue'
    $out = & $cli --application $id --stage --data-root $data --download-root $staging 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exit -ne 0) { $out; throw "Stage of zzapd-everything failed with exit code $exit" }
    $manifest = Get-ChildItem -LiteralPath $data -Recurse -File -Filter 'stage-manifest.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $stage = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json
    "staged: $($stage.AppName) $($stage.SoftwareVersion)"

    if (Test-Path -LiteralPath 'C:\tools\dotnet') { $env:DOTNET_ROOT = 'C:\tools\dotnet' }
    & $SeedTool $data $id "catalog:$id" $stage.AppName $stage.SoftwareVersion 'MCM' 'cm01.contoso.com'
    if ($LASTEXITCODE -ne 0) { throw "The seed tool ended with exit code $LASTEXITCODE." }

    $settings = @{
        DownloadRoot = $staging; FileShareRoot = $FileShareRoot; RemoveVersionFromTitle = $false
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
        Supersedence = @{ Mode = 'Off'; TitleOverrides = @{ $id = @{ Mode = 'Replace' } } }
        OneClick = @{ Action = 'StageAndPackage'; TrackedApplications = @($id); OnExisting = 'Skip' }
    }
    $null = Start-Apd $exe $data $theme $settings
    try {
        (Main-Window).SetFocus()
        Step 'core-one-click-plan-supersedence' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Click-El (Need-El 'btnOneClick')
            if (-not (Wait-El 'txtPlanSummary' 600)) { throw 'The One Click window did not open.' }
            Start-Sleep 3
            $summary = (Need-El 'txtPlanSummary').Current.Name
            "plan: $summary"
            if ($summary -notmatch 'supersedence') { throw "The plan does not hold the supersedence step: $summary" }
            Shot 'core-one-click-plan-supersedence'
            if (-not (Close-ApdWindow 'One Click')) { throw 'The One Click window did not close. Run was not selected.' }
        }
    }
    finally { Stop-Apd }
}
