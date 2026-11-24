<#
.SYNOPSIS
    Brings the Application, CMName and AppName of every APD definition to the names of the PowerShell app.
.DESCRIPTION
    Reads the golden file made by Export-PowerShellTitles.ps1 and rewrites, in place and line by line, three
    fields of each definition so that APD shows and creates the names that the PowerShell app does:
      Application  = the App: header of the packager (grid title, first search key)
      CMName       = the CMName: header of the packager (how existing ConfigMgr applications are found)
      AppName      = the stage name, when the packager writes a fixed name ("{version}" for the version);
                     a packager that reads the name from the installer gets no AppName
    A packager listed under Defects in known-differences.json (next to the golden file) keeps the APD value (the PowerShell value is a defect).
    -Check only reports and returns the count of definitions that differ.
#>
[CmdletBinding()]
param(
    [string]$Golden = (Join-Path $PSScriptRoot '..\..\tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json'),
    [string]$Definitions = (Join-Path $PSScriptRoot '..\..\src\ApplicationPackagerEngine\Definitions'),
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
function ConvertTo-JsonString([string]$Text) { return (ConvertTo-Json -InputObject $Text -Compress) }
function Set-Field {
    param([string[]]$Lines, [string]$Name, [string]$Value, [string]$After)
    $pattern = '^(\s*"' + $Name + '"\s*:\s*)"(?:[^"\\]|\\.)*"(,?)\s*$'
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $pattern) { $Lines[$i] = $Matches[1] + (ConvertTo-JsonString $Value) + $Matches[2]; return ,$Lines }
    }
    $anchor = '^(\s*)"' + $After + '"(\s*:\s*)"(?:[^"\\]|\\.)*"(,?)\s*$'
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $anchor) {
            $new = $Matches[1] + '"' + $Name + '"' + $Matches[2] + (ConvertTo-JsonString $Value) + ','
            return ,@($Lines[0..$i] + $new + $Lines[($i + 1)..($Lines.Count - 1)])
        }
    }
    throw "No $After line to place $Name after."
}
$known = @{}
$knownFile = Join-Path (Split-Path -Parent $Golden) 'known-differences.json'
if (Test-Path -LiteralPath $knownFile) { foreach ($k in @((Get-Content -LiteralPath $knownFile -Raw | ConvertFrom-Json).Defects)) { $known[$k.Packager] = $k } }
$changed = 0
foreach ($p in (Get-Content -LiteralPath $Golden -Raw | ConvertFrom-Json).Packagers) {
    if (-not $p.Definition -or $known.ContainsKey($p.Packager)) { continue }
    $path = Join-Path $Definitions ($p.Definition + '.json')
    $raw = [System.IO.File]::ReadAllText($path)
    $bom = $raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF
    $eol = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = @($raw.TrimStart([char]0xFEFF) -split "`r?`n")
    $before = $lines -join $eol
    $lines = Set-Field $lines 'Application' $p.App 'Vendor'
    $lines = Set-Field $lines 'CMName' $p.CMName 'Application'
    if ($p.StageKind -eq 'template' -and @($p.StageNames).Count -eq 1) { $lines = Set-Field $lines 'AppName' @($p.StageNames)[0] 'CMName' }
    $after = $lines -join $eol
    if ($after -ne $before) {
        $changed++
        if (-not $Check) { [System.IO.File]::WriteAllText($path, $(if ($bom) { [string][char]0xFEFF } else { '' }) + $after, (New-Object System.Text.UTF8Encoding($false))) }
    }
}
"$changed definition(s) $(if ($Check) { 'differ' } else { 'rewritten' })"
if ($Check) { exit $(if ($changed) { 1 } else { 0 }) }
