<#
.SYNOPSIS
    Saves one region of a mock image and of the rendered image, enlarged, the mock above and the rendered screen below.
#>
param(
    [Parameter(Mandatory)][string]$Mock,
    [Parameter(Mandatory)][string]$Rendered,
    [Parameter(Mandatory)][string]$Region,
    [Parameter(Mandatory)][string]$OutFile,
    [int]$Scale = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$x, $y, $w, $h = $Region.Split(',') | ForEach-Object { [int]$_ }
$result = [System.Drawing.Bitmap]::new($w * $Scale, 2 * $h * $Scale + 6)
$g = [System.Drawing.Graphics]::FromImage($result)
$g.Clear([System.Drawing.Color]::Magenta)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$top = 0
foreach ($path in $Mock, $Rendered) {
    $image = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $path).ProviderPath)
    $g.DrawImage($image, [System.Drawing.Rectangle]::new(0, $top, $w * $Scale, $h * $Scale), [System.Drawing.Rectangle]::new($x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
    $image.Dispose()
    $top += $h * $Scale + 6
}
$g.Dispose()
$result.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$result.Dispose()
