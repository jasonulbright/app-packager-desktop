<#
.SYNOPSIS
    Release gate: every executable in a folder carries a valid, timestamped
    Authenticode signature from the expected signer.

.DESCRIPTION
    Runs signtool verify /pa and Get-AuthenticodeSignature on each *.exe and *.dll
    under -Path. A file that is unsigned, chain-invalid, signed by another subject,
    or not timestamped fails the run. Timestamping matters here because the signing
    service issues short-lived certificates: an untimestamped signature stops
    verifying when its certificate expires.

.PARAMETER Path
    Folder to check, searched recursively.

.PARAMETER ExpectedSubjectCommonName
    The CN the signer certificate must carry.

.PARAMETER SignTool
    signtool.exe to use. Discovered under the Windows Kits bin tree when omitted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$ExpectedSubjectCommonName,
    [string]$SignTool
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-SignTool {
    if ($SignTool) {
        if (-not (Test-Path -LiteralPath $SignTool)) { throw "signtool not found at $SignTool" }
        return (Resolve-Path -LiteralPath $SignTool).Path
    }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin") |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    foreach ($root in $roots) {
        # SDK folders sort wrong as strings (10.0.9 above 10.0.22621); version-sort them.
        $hits = @(Get-ChildItem -LiteralPath $root -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*\x64\*' } |
            Sort-Object -Property @{ Expression = {
                $v = [version]'0.0'
                $name = Split-Path (Split-Path $_.FullName -Parent) -Leaf
                if ([version]::TryParse($name, [ref]$v)) { $v } else { [version]'0.0' }
            } } -Descending)
        if ($hits.Count -gt 0) { return $hits[0].FullName }
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'signtool.exe (x64) was not found under the Windows Kits bin tree or on PATH.'
}

$tool = Find-SignTool
$files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Include *.exe, *.dll)
if ($files.Count -eq 0) { throw "Nothing to verify under $Path" }

foreach ($file in $files) {
    $output = & $tool verify /pa /v $file.FullName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verify /pa failed for $($file.FullName) (exit $LASTEXITCODE):`n$($output -join "`n")"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ([string]$signature.Status -ne 'Valid') {
        throw "$($file.FullName) is not validly signed (status $($signature.Status): $($signature.StatusMessage))"
    }
    if (-not $signature.SignerCertificate) { throw "$($file.FullName) carries no signer certificate" }
    $cn = $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ([string]$cn -ne $ExpectedSubjectCommonName) {
        throw "$($file.FullName) is signed by '$cn', expected '$ExpectedSubjectCommonName' (subject: $($signature.SignerCertificate.Subject))"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "$($file.FullName) is signed but not timestamped"
    }
    Write-Host ("signature ok: {0} (CN={1}, timestamped by {2})" -f $file.Name, $cn, $signature.TimeStamperCertificate.Subject)
}
Write-Host ("verified {0} file(s) under {1}" -f $files.Count, $Path)
