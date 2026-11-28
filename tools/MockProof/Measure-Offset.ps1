<#
.SYNOPSIS
    For each named region, finds the shift of the rendered image that fits the mock best (-3 to 3 px). A best shift of 0,0 means the element
    stands where the mock has it. The comparison is in gray, so the smoothing of text does not decide it.
.PARAMETER Regions
    Entries "name=x,y,width,height" in mock coordinates.
#>
param(
    [Parameter(Mandatory)][string]$Mock,
    [Parameter(Mandatory)][string]$Rendered,
    [Parameter(Mandatory)][string[]]$Regions
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
function Read-Gray([string]$path) {
    $image = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $path).ProviderPath)
    $data = $image.LockBits([System.Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height), 'ReadOnly', 'Format32bppArgb')
    $bytes = [byte[]]::new($data.Stride * $image.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $image.UnlockBits($data)
    $result = @{ Bytes = $bytes; Stride = $data.Stride; Width = $image.Width; Height = $image.Height }
    $image.Dispose()
    $result
}
$a = Read-Gray $Mock
$b = Read-Gray $Rendered
foreach ($entry in $Regions) {
    $name, $box = $entry.Split('=')
    $x, $y, $w, $h = $box.Split(',') | ForEach-Object { [int]$_ }
    $best = $null; $zero = 0
    foreach ($dy in -3..3) {
        foreach ($dx in -3..3) {
            $sum = 0
            for ($row = $y; $row -lt $y + $h; $row++) {
                for ($col = $x; $col -lt $x + $w; $col++) {
                    $i = $row * $a.Stride + $col * 4
                    $j = ($row + $dy) * $b.Stride + ($col + $dx) * 4
                    if ($j -lt 0 -or $j + 2 -ge $b.Bytes.Length) { continue }
                    $sum += [Math]::Abs(($a.Bytes[$i] + 2 * $a.Bytes[$i + 1] + $a.Bytes[$i + 2]) - ($b.Bytes[$j] + 2 * $b.Bytes[$j + 1] + $b.Bytes[$j + 2]))
                }
            }
            if ($dx -eq 0 -and $dy -eq 0) { $zero = $sum }
            if ($null -eq $best -or $sum -lt $best.Sum) { $best = @{ Sum = $sum; X = $dx; Y = $dy } }
        }
    }
    '{0,-22} shift {1},{2}   error at best {3:0.0}  at zero {4:0.0}' -f $name, $best.X, $best.Y, ($best.Sum / (4.0 * $w * $h)), ($zero / (4.0 * $w * $h))
}
