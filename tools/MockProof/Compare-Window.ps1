<#
.SYNOPSIS
    For a screen that is a window over the main window: cuts the window out of the mock and out of the rendered image and writes the pair, an
    overlay of the differences and the percent of differing pixels. The mock draws another layout of the main window behind these windows, so only
    the window is compared.
.PARAMETER Windows
    Entries "screen=left,top,width,height", as MockProof prints them.
#>
param(
    [Parameter(Mandatory)][string]$Rendered,
    [Parameter(Mandatory)][string[]]$Windows,
    [string]$Theme = 'light',
    [string]$Mocks = (Join-Path $HOME 'Downloads\mockup-inputs\apd-redesign\visuals'),
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $Rendered 'windows' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing.Common
$temp = Join-Path $OutDir '_cut'
New-Item -ItemType Directory -Force "$temp\mock", "$temp\built" | Out-Null
foreach ($entry in $Windows) {
    $screen, $box = $entry.Split('=')
    $x, $y, $w, $h = $box.Split(',') | ForEach-Object { [int]$_ }
    foreach ($pair in @(@((Join-Path $Mocks "$screen-$Theme.png"), "$temp\mock\$screen-$Theme.png"), @((Join-Path $Rendered "$screen-$Theme.png"), "$temp\built\$screen-$Theme.png"))) {
        $source = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $pair[0]).ProviderPath)
        $cut = $source.Clone([System.Drawing.Rectangle]::new($x, $y, $w, $h), $source.PixelFormat)
        $cut.Save($pair[1], [System.Drawing.Imaging.ImageFormat]::Png)
        $cut.Dispose(); $source.Dispose()
    }
}
& (Join-Path $PSScriptRoot 'Compare-Mock.ps1') -Rendered "$temp\built" -Mocks "$temp\mock" -OutDir $OutDir
