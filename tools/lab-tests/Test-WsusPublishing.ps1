<#
.SYNOPSIS
    Proves that a lab client can publish to the WSUS server of the lab: the API loads, the account is a WSUS administrator, the signing certificate is trusted,
    and one test update publishes, carries a valid signature, and is removed again.

.DESCRIPTION
    Run it on the client, in Windows PowerShell 5.1, as the account that publishes. It dot-sources WsusAdapter.ps1 (the file of the application) and calls the same
    operations that the application calls, so a pass covers the application's own WSUS code. The test update has the rule "False" for installable and installed,
    so no computer can install it, and it is not approved. The script removes it before it ends, also after a failure.
    Requirements for local publishing:
      https://learn.microsoft.com/intune/configmgr/sum/tools/updates-publisher-security
      https://learn.microsoft.com/intune/configmgr/sum/deploy-use/third-party-software-updates
.PARAMETER AdapterPath
    WsusAdapter.ps1 from src\ApplicationPackagerEngine\Services\Wsus.
#>
param(
    [string]$ServerName = 'cm01.contoso.com',
    [int]$PortNumber = 8530,
    [string]$AdapterPath = (Join-Path $PSScriptRoot 'WsusAdapter.ps1')
)
$ErrorActionPreference = 'Stop'
. $AdapterPath
$settings = [pscustomobject]@{ ServerName = $ServerName; PortNumber = $PortNumber; UseSsl = $false }
$request = [pscustomobject]@{ Settings = $settings }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

"Account: $env:USERDOMAIN\$env:USERNAME on $env:COMPUTERNAME, PowerShell $($PSVersionTable.PSVersion)"
$status = Invoke-WsusStatusOperation -Request $request
"API: available=$($status.Api.Available) version=$($status.Api.Version) $($status.Api.Location)"
"Server: connected=$($status.Connected) $($status.ServerName):$PortNumber version=$($status.Version) role=$($status.Role) $($status.ConnectError)"
Assert-True $status.Api.Available "The WSUS API does not load: $($status.Api.Reason)"
Assert-True $status.Connected "No connection to WSUS: $($status.ConnectError)"
Assert-True ($status.Role -eq 'Administrator') "The role is '$($status.Role)': $($status.RoleError)"
Assert-True ($null -ne $status.SigningCertificate) "The server gives no signing certificate. $($status.CertificateError)"
$thumbprint = $status.SigningCertificate.Thumbprint
"Signing certificate: $($status.SigningCertificate.Subject) $thumbprint, ends $($status.SigningCertificate.NotAfter)"
foreach ($store in 'Root', 'TrustedPublisher') {
    Assert-True (Test-Path "Cert:\LocalMachine\$store\$thumbprint") "The signing certificate is not in LocalMachine\$store."
    "Trusted: LocalMachine\$store"
}
$accept = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -ErrorAction SilentlyContinue).AcceptTrustedPublisherCerts
Assert-True ($accept -eq 1) 'The policy "Allow signed content from intranet Microsoft update service location" is not on.'
'Policy: AcceptTrustedPublisherCerts=1'
"Computer groups: $((Invoke-WsusGroupsOperation -Request $request).Names -join ', ')"

$id = [guid]::NewGuid()
$work = Join-Path $env:TEMP "wsus-proof-$($id.ToString('N'))"
$source = Join-Path $work 'content'
$null = New-Item -ItemType Directory -Force $source
$publishAttempted = $false
try {
    # Any small executable serves as the payload: no rule lets a computer run it.
    Copy-Item (Join-Path $env:SystemRoot 'System32\whoami.exe') (Join-Path $source 'probe.exe')
    $plan = [pscustomobject]@{
        PackageId = $id.ToString(); InstallerType = 'EXE'; InstallerFile = 'probe.exe'; Classification = 'Updates'
        Title = "Lab publishing proof $($id.ToString('N').Substring(0, 8))"; Description = 'Test update of Test-WsusPublishing.ps1. It is removed by the same run.'
        VendorName = 'Lab Test'; ProductName = 'Lab Test'; SupportUrl = ''; CommandLine = '/?'; ReturnCodes = @()
        IsInstallable = '<lar:False />'; IsInstalled = '<lar:False />'; SupersededPackageIds = @()
    }
    $publish = [pscustomobject]@{
        Settings = $settings; Plan = $plan; ContentFolder = $source; PayloadFiles = @('probe.exe')
        PayloadSha256 = @((Get-WsusFileSha256 -Path (Join-Path $source 'probe.exe')))
    }
    $publishAttempted = $true
    $result = Invoke-WsusPublishCommit -Request $publish
    "Published: $($result.Published) visible=$($result.UpdateVisible) id=$($result.PackageId)"
    Assert-True ($result.Published -and $result.UpdateVisible) "The update is not on the server. $($result.UpdateVisibilityError)"
    $cab = Get-ChildItem "\\$ServerName\UpdateServicesPackages\$id" -Filter *.cab -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cab) { $cab = Get-ChildItem "\\$($status.ServerName)\UpdateServicesPackages\$id" -Filter *.cab | Select-Object -First 1 }
    Assert-True ($null -ne $cab) 'The package folder on the server has no .cab file.'
    $copy = Join-Path $work $cab.Name
    Copy-Item $cab.FullName $copy
    $signature = Get-AuthenticodeSignature $copy
    "Package signature: $($signature.Status), signer $($signature.SignerCertificate.Thumbprint)"
    Assert-True ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Thumbprint -eq $thumbprint) 'The package is not signed with a certificate this computer trusts.'
}
finally {
    if ($publishAttempted) {
        $removal = Invoke-WsusSetStateOperation -Request ([pscustomobject]@{ Settings = $settings; Action = 'Remove'; PackageIds = @($id.ToString()) })
        foreach ($entry in $removal.Results) { "Removed: found=$($entry.Found) ok=$($entry.Ok) $($entry.Message) $($entry.FolderError)" }
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
'PASS: this computer can publish to the lab WSUS server.'
