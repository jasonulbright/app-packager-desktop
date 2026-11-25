<#
.SYNOPSIS
    Puts one public root certificate into the trusted root store of a lab computer, when the computer does not trust it yet. Repeatable.

.DESCRIPTION
    A lab client that a WSUS server manages does not get new roots from Windows Update, so a site such as download.windowsupdate.com (served through Fastly, chain ending in
    "Certainly Root R1") fails with "Could not establish trust relationship" and the optional feature download of the WSUS console stops at 75 percent.
    The file is checked against the thumbprint that the caller gives (the thumbprint of the chain top that the server presented) before anything is imported.
    Where the file comes from: the Mozilla CA bundle (https://curl.se/ca/cacert.pem) has the root; Windows' own list is read with certutil -syncWithWU (Microsoft Learn,
    "Configure Trusted Roots and Disallowed Certificates": https://learn.microsoft.com/previous-versions/windows/it-pro/windows-server-2012-R2-and-2012/dn265983(v=ws.11)).
    Import-Certificate: https://learn.microsoft.com/powershell/module/pki/import-certificate
.PARAMETER Path
    The .cer file.
.PARAMETER Thumbprint
    The SHA-1 thumbprint that the certificate must have.
#>
param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Thumbprint)
$ErrorActionPreference = 'Stop'
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Path)
if ($cert.Thumbprint -ne $Thumbprint.Replace(' ', '').ToUpperInvariant()) { throw "The file has thumbprint $($cert.Thumbprint), not $Thumbprint." }
if ($cert.Subject -ne $cert.Issuer) { throw 'The certificate is not a self-signed root.' }
if (Test-Path "Cert:\LocalMachine\Root\$($cert.Thumbprint)") { "Already trusted: $($cert.Subject)"; return }
Import-Certificate -FilePath $Path -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
"Imported: $($cert.Subject) $($cert.Thumbprint)"
