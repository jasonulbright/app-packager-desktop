<#
.SYNOPSIS
    Compares rendered images with the mock images: for each screen a side by side image and an overlay that marks each difference.
.DESCRIPTION
    side-<screen>-<theme>.png: the mock above, the rendered screen below.
    overlay-<screen>-<theme>.png: the mock in gray; every pixel whose color differs by more than -Tolerance is marked magenta.
    blend-<screen>-<theme>.png: both images at half strength; a shifted element shows twice.
    The printed percent is the part of the pixels that differ. It helps to see whether a change moved closer; it is not the pass rule.
#>
param(
    [Parameter(Mandatory)][string]$Rendered,
    [string]$Mocks = (Join-Path $HOME 'Downloads\mockup-inputs\apd-redesign\visuals'),
    [string]$OutDir,
    [int]$Tolerance = 24
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = $Rendered }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing.Common
if (-not ('MockDiff' -as [type])) {
    $references = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location, 'System.Runtime', 'System.Runtime.InteropServices') +
        @(Get-ChildItem (Split-Path ([object].Assembly.Location)) -Filter System.Private.Windows*.dll | ForEach-Object FullName)
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class MockDiff {
    static int[] Read(Bitmap b, int w, int h) {
        var pixels = new int[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = unchecked((int)0xffff00ff);
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Math.Min(h, b.Height); y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w, Math.Min(w, b.Width));
        b.UnlockBits(data); return pixels;
    }
    static void Write(int[] pixels, int w, int h, string path) {
        using (var b = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w, data.Scan0 + y * data.Stride, w);
            b.UnlockBits(data); b.Save(path, ImageFormat.Png);
        }
    }
    public static double Compare(string mockPath, string renderedPath, string overlayPath, string blendPath, string sidePath, int tolerance) {
        using (var m = new Bitmap(mockPath)) using (var r = new Bitmap(renderedPath)) {
            int w = m.Width, h = m.Height; var a = Read(m, w, h); var c = Read(r, w, h);
            var overlay = new int[w * h]; var blend = new int[w * h]; var side = new int[w * (2 * h + 8)]; int differ = 0;
            for (int i = 0; i < a.Length; i++) {
                int ar = (a[i] >> 16) & 255, ag = (a[i] >> 8) & 255, ab = a[i] & 255, cr = (c[i] >> 16) & 255, cg = (c[i] >> 8) & 255, cb = c[i] & 255;
                int d = Math.Max(Math.Abs(ar - cr), Math.Max(Math.Abs(ag - cg), Math.Abs(ab - cb)));
                int gray = (ar * 3 + ag * 6 + ab) / 10; gray = 128 + (gray - 128) / 3;
                if (d > tolerance) { differ++; overlay[i] = unchecked((int)0xffff00c8); } else overlay[i] = unchecked((int)0xff000000) | (gray << 16) | (gray << 8) | gray;
                blend[i] = unchecked((int)0xff000000) | (((ar + cr) / 2) << 16) | (((ag + cg) / 2) << 8) | ((ab + cb) / 2);
                side[i] = a[i]; side[w * (h + 8) + i] = c[i];
            }
            for (int i = w * h; i < w * (h + 8); i++) side[i] = unchecked((int)0xffff00c8);
            Write(overlay, w, h, overlayPath); Write(blend, w, h, blendPath); Write(side, w, 2 * h + 8, sidePath);
            return 100.0 * differ / a.Length;
        }
    }
}
'@
}
foreach ($file in Get-ChildItem -LiteralPath $Rendered -Filter *.png | Where-Object { $_.BaseName -notmatch '^(side|overlay|blend)-' }) {
    $mock = Join-Path $Mocks $file.Name
    if (-not (Test-Path -LiteralPath $mock)) { continue }
    $percent = [MockDiff]::Compare($mock, $file.FullName, (Join-Path $OutDir "overlay-$($file.Name)"), (Join-Path $OutDir "blend-$($file.Name)"), (Join-Path $OutDir "side-$($file.Name)"), $Tolerance)
    '{0}: {1:0.00} percent of the pixels differ' -f $file.Name, $percent
}
