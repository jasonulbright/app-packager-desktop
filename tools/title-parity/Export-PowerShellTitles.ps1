<#
.SYNOPSIS
    Writes the golden file of the names the PowerShell app (App Packager) gives every packager.
.DESCRIPTION
    Reads Packagers/package-*.ps1 of the PowerShell app repository and records, per packager, the three
    names that decide whether App Packager Desktop finds the same ConfigMgr applications:
      App       the grid title (the "App:" header line; the Application column of the grid and of search)
      CMName    the name Check ConfigMgr, Discover and Convert use to find an existing application
      StageName the application name the stage writes (AppName of the stage manifest), as a template:
                {version} stands for the version; a name that the packager reads from the installer
                (MSI ProductName and similar) is kind 'installer', not a literal.
    The Tests project reads the golden file; run this script again after the PowerShell app changes a packager.
.PARAMETER PowerShellRepo
    Folder of the PowerShell app repository (default C:\projects\app-packager).
.PARAMETER Definitions
    Folder of the APD definitions; the golden file records which definition file belongs to each packager.
.PARAMETER Output
    Golden file (default tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json).
#>
[CmdletBinding()]
param(
    [string]$PowerShellRepo = 'C:\projects\app-packager',
    [string]$Definitions = (Join-Path $PSScriptRoot '..\..\src\ApplicationPackagerEngine\Definitions'),
    [string]$Output = (Join-Path $PSScriptRoot '..\..\tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json')
)
$ErrorActionPreference = 'Stop'

function ConvertTo-StageTemplate {
    param([string]$Expression, [hashtable]$Constants)
    $e = $Expression.Trim()
    if ($e -match '^\$(\w+)$' -and $Constants.ContainsKey($Matches[1])) { return [pscustomobject]@{ Kind = 'template'; Name = $Constants[$Matches[1]] } }
    if ($e -match '^"((?:[^"`]|`.)*)"$' -or $e -match "^'([^']*)'$") {
        $text = $Matches[1]
        # Constants the packager sets at the top ($FeatureVersion = 17, $Architecture = "x64") are known.
        $text = [regex]::Replace($text, '\$\(\$(\w+)\.ToUpper\(\)\)', { param($m) if ($Constants.ContainsKey($m.Groups[1].Value)) { $Constants[$m.Groups[1].Value].ToUpperInvariant() } else { $m.Value } })
        $text = [regex]::Replace($text, '\$\{?(\w+)\}?', { param($m) $n = $m.Groups[1].Value; if ($n -in 'version', 'displayVersion') { '{version}' } elseif ($Constants.ContainsKey($n)) { $Constants[$n] } else { $m.Value } }, 'IgnoreCase')
        if ($text -notmatch '[$`]') { return [pscustomobject]@{ Kind = 'template'; Name = $text } }
    }
    return [pscustomobject]@{ Kind = 'installer'; Name = $e }
}

function Get-PackagerConstant {
    param([string[]]$Lines)
    $constants = @{}
    foreach ($l in $Lines) {
        if ($l -match '^\s*\$(\w+)\s*=\s*(?:"([^"$`]*)"|''([^'']*)''|(\d+))\s*(?:#.*)?$') {
            $value = if ($null -ne $Matches[2] -and $Matches[2] -ne '') { $Matches[2] } elseif ($null -ne $Matches[3] -and $Matches[3] -ne '') { $Matches[3] } else { $Matches[4] }
            if (-not $constants.ContainsKey($Matches[1])) { $constants[$Matches[1]] = [string]$value }
        }
    }
    return $constants
}
# A packager and its definition share the file name, except for the renames listed here (a PowerShell packager that
# the APD catalog split or renamed). A packager with no definition stays in the file with an empty Definition.
$alias = @{ 'chrome'='chrome-x64'; 'citrixworkspace-cr'='citrixworkspacecr'; 'corretto-jdk17'='corretto-jdk17-x64'; 'corretto-jdk21'='corretto-jdk21-x64'; 'corretto-jdk25'='corretto-jdk25-x64'
    'dotnet8'='dotnet8-x64'; 'powerbi-desktop'='powerbidesktop'; 'powerbi-desktop-report-server'='powerbidesktopreportserver'; 'teams-new'='teams'
    'temurin-jdk17'='temurin-jdk17-x64'; 'temurin-jdk21'='temurin-jdk21-x64'; 'temurin-jdk25'='temurin-jdk25-x64'
    'temurin-jre17'='temurin-jre17-x64'; 'temurin-jre21'='temurin-jre21-x64'; 'temurin-jre25'='temurin-jre25-x64'
    'xenserver-vmtools'='xenservervmtools'; 'vscode-system'='vscode' }
$definitionNames = @{}
foreach ($d in Get-ChildItem -LiteralPath $Definitions -Filter *.json) { $definitionNames[$d.BaseName.ToLowerInvariant()] = $d.BaseName }

$packagers = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PowerShellRepo 'Packagers') -Filter 'package-*.ps1' | Sort-Object Name) {
    $lines = @([System.IO.File]::ReadAllLines($file.FullName))
    $head = $lines | Select-Object -First 200
    $app = $null; $cm = $null; $vendor = $null
    foreach ($l in $head) {
        $t = $l.TrimStart([char]0xFEFF)
        if (-not $vendor -and $t -match '^\s*(?:#\s*)?Vendor\s*:\s*(.+?)\s*$') { $vendor = $Matches[1]; continue }
        if (-not $app -and $t -match '^\s*(?:#\s*)?App\s*:\s*(.+?)\s*$') { $app = $Matches[1]; continue }
        if (-not $cm -and $t -match '^\s*(?:#\s*)?CMName\s*:\s*(.+?)\s*$') { $cm = $Matches[1]; continue }
    }
    if (-not $cm) { $cm = $app }

    # The stage name: AppName of the manifest hashtable; "$appName" is followed back to its assignments.
    $direct = @($lines | Where-Object { $_ -match '^\s*AppName\s*=\s*(?!\$appName\s*$)\S' } | ForEach-Object { ($_ -replace '^\s*AppName\s*=\s*', '').Trim() })
    $viaVariable = @($lines | Where-Object { $_ -match '^\s*\$appName\s*=\s*\S' } | ForEach-Object { ($_ -replace '^\s*\$appName\s*=\s*', '').Trim() })
    $expressions = @(@($direct) + @($viaVariable) | Where-Object { $_ } | Select-Object -Unique)
    $constants = Get-PackagerConstant -Lines $lines
    $templates = @($expressions | ForEach-Object { ConvertTo-StageTemplate $_ $constants })
    $kind = if ($templates.Count -eq 0) { 'none' } elseif (@($templates | Where-Object Kind -eq 'installer').Count -gt 0) { 'installer' } else { 'template' }
    $packagerName = $file.BaseName -replace '^package-', ''
    $definitionKey = $packagerName.ToLowerInvariant()
    if (-not $definitionNames.ContainsKey($definitionKey) -and $alias.ContainsKey($definitionKey)) { $definitionKey = $alias[$definitionKey] }
    [ordered]@{
        Packager  = $packagerName
        Definition = $(if ($definitionNames.ContainsKey($definitionKey)) { $definitionNames[$definitionKey] } else { '' })
        Vendor    = $vendor
        App       = $app
        CMName    = $cm
        StageKind = $kind
        StageNames = @($templates | Where-Object Kind -eq 'template' | ForEach-Object { $_.Name })
        StageExpressions = $expressions
    }
}
$dir = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
# The architecture rule and its default, read from the PowerShell source so that a change there shows in the golden file.
$common = [System.IO.File]::ReadAllText((Join-Path $PowerShellRepo 'Packagers\AppPackagerCommon.psm1'))
if ($common -notmatch "function Format-ApplicationTitleArchitecture[\s\S]*?\`$tag = '([^']+)'") { throw 'Format-ApplicationTitleArchitecture not found in AppPackagerCommon.psm1' }
$tagPattern = $Matches[1]
$launcher = [System.IO.File]::ReadAllText((Join-Path $PowerShellRepo 'start-apppackager.ps1'))
if ($launcher -notmatch 'AppendArchitectureToTitle\s*=\s*\$(true|false)') { throw 'The AppendArchitectureToTitle default is not in start-apppackager.ps1' }
$appendDefault = $Matches[1] -eq 'true'
$json = [ordered]@{
    Source = 'package-*.ps1 headers and stage manifest AppName; AppPackagerCommon.psm1 Format-ApplicationTitleArchitecture; start-apppackager.ps1 defaults'
    Count = @($packagers).Count
    ArchitectureTagPattern = $tagPattern
    AppendArchitectureDefault = $appendDefault
    Packagers = @($packagers)
} | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $dir).Path + '\' + (Split-Path -Leaf $Output), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
"Wrote $(@($packagers).Count) packagers to $Output"
