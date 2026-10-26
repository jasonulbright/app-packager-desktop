<#
.SYNOPSIS
    Writes the SHA-256 checksum file of the release artifacts.

.DESCRIPTION
    Writes one line per artifact, "<lowercase hash>  <file name>", and returns the lines.

.PARAMETER Path
    The artifact files.

.PARAMETER Destination
    The checksum file to write.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Bare file names in the lines so `sha256sum -c` runs from the download directory.
$lines = foreach ($artifact in $Path) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $artifact)
}
# sha256sum -c reads a carriage return as part of the file name, so the file is LF-only.
$text = (@($lines) | ForEach-Object { "$_`n" }) -join ''
[System.IO.File]::WriteAllText($Destination, $text, [System.Text.ASCIIEncoding]::new())
$lines
