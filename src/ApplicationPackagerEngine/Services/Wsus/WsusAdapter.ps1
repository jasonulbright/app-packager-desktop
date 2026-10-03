#Requires -Version 5.1
<#
.SYNOPSIS
    The one place the application calls Microsoft.UpdateServices.Administration.

.DESCRIPTION
    Runs under Windows PowerShell 5.1: the WSUS administration API is a .NET
    Framework assembly. Every value arrives in the JSON request file named by
    -InputPath; the script text itself carries none. A PFX password arrives on
    standard input, never in a file or on the command line. The result is one
    JSON document written to the request's ResultPath.

    Result: { Ok, Operation, ErrorKind, Error, Data }. ErrorKind is one of
    ApiMissing, Unreachable, Connect, NotAdministrator, InsecureConnection,
    NoCertificate, SelfSignedRefused, PublishRejected, Input, Failed.

    Dot-sourcing the script defines its functions without running a request.
#>
param([string]$InputPath = '')

Set-StrictMode -Version 2.0

$script:WsusAdministrationAssemblyName = 'Microsoft.UpdateServices.Administration'

class WsusAdapterError : System.Exception {
    [string]$Kind
    WsusAdapterError([string]$kind, [string]$message) : base($message) { $this.Kind = $kind }
}

function Get-InputValue {
    param($InputObject, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $InputObject) { return $Default }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($property -and $null -ne $property.Value) { return $property.Value }
    return $Default
}

function Get-WsusErrorMessage {
    # API failures arrive wrapped in MethodInvocationException; the inner
    # exception carries the server's own text.
    param([Parameter(Mandatory)]$ErrorRecord)
    $exception = $ErrorRecord
    if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $exception = $ErrorRecord.Exception }
    while ($exception -is [System.Management.Automation.MethodInvocationException] -and $exception.InnerException) {
        $exception = $exception.InnerException
    }
    return [string]$exception.Message
}

function Get-WsusErrorKind {
    param([Parameter(Mandatory)]$ErrorRecord)
    $exception = $ErrorRecord
    if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $exception = $ErrorRecord.Exception }
    if ($exception -is [WsusAdapterError]) { return $exception.Kind }
    return 'Failed'
}

function Import-WsusAdministrationAssembly {
    $loaded = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $script:WsusAdministrationAssemblyName })
    if ($loaded.Count -gt 0) { return $loaded[0] }
    if ($PSVersionTable.PSEdition -eq 'Core') {
        throw [WsusAdapterError]::new('ApiMissing', 'The WSUS administration API is a .NET Framework assembly and does not load in PowerShell 7. Run AppPackager under Windows PowerShell 5.1 (powershell.exe).')
    }
    $assembly = $null
    try { $assembly = [System.Reflection.Assembly]::LoadWithPartialName($script:WsusAdministrationAssemblyName) } catch { $assembly = $null }
    if ($assembly) { return $assembly }
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $path = [System.IO.Path]::Combine([string]$programFiles, 'Update Services\Api\Microsoft.UpdateServices.Administration.dll')
    if (Test-Path -LiteralPath $path -PathType Leaf) { return [System.Reflection.Assembly]::LoadFrom($path) }
    throw [WsusAdapterError]::new('ApiMissing', ('The WSUS administration API is not installed on this computer. Install the WSUS console: on Windows 10 or 11, ' +
        'Settings > System > Optional features > RSAT: Windows Server Update Services Tools (Add-WindowsCapability -Online -Name Rsat.WSUS.Tools~~~~0.0.1.0); ' +
        'on Windows Server, Install-WindowsFeature UpdateServices-UI.'))
}

function Test-WsusServerReachable {
    # A short TCP probe first: the API's own connect waits out a full HTTP
    # timeout against an address that never answers.
    param([Parameter(Mandatory)][string]$ServerName, [Parameter(Mandatory)][int]$PortNumber, [int]$TimeoutMilliseconds = 5000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect($ServerName, $PortNumber, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            throw [WsusAdapterError]::new('Unreachable', ('WSUS server {0} did not answer on port {1} within {2} seconds.' -f $ServerName, $PortNumber, [int]($TimeoutMilliseconds / 1000)))
        }
        try { $client.EndConnect($pending) }
        catch { throw [WsusAdapterError]::new('Unreachable', ('WSUS server {0} refused port {1}: {2}' -f $ServerName, $PortNumber, (Get-WsusErrorMessage -ErrorRecord $_))) }
    }
    finally { $client.Close() }
}

function Test-WsusServerIsLocal {
    # The API reports only its parameterless local connection as secure; a
    # connection to this computer by name reports insecure and would refuse
    # a PFX import on the WSUS server itself.
    param([Parameter(Mandatory)][string]$ServerName)
    $name = $ServerName.Trim().TrimEnd('.')
    if ($name -in @('localhost', '127.0.0.1')) { return $true }
    if ($name -ieq $env:COMPUTERNAME) { return $true }
    $fqdn = ''
    try { $fqdn = [System.Net.Dns]::GetHostEntry([System.Net.Dns]::GetHostName()).HostName } catch { $fqdn = '' }
    return ([bool]$fqdn -and $name -ieq $fqdn)
}

function New-WsusServerObject {
    param([Parameter(Mandatory)][string]$ServerName, [Parameter(Mandatory)][bool]$UseSsl, [Parameter(Mandatory)][int]$PortNumber)
    if (Test-WsusServerIsLocal -ServerName $ServerName) {
        $server = [Microsoft.UpdateServices.Administration.AdminProxy]::GetUpdateServer()
    }
    else {
        $server = [Microsoft.UpdateServices.Administration.AdminProxy]::GetUpdateServer($ServerName, $UseSsl, $PortNumber)
    }
    $server.PreferredCulture = 'en'
    return $server
}

function Get-WsusServerConnection {
    param([Parameter(Mandatory)]$Settings)
    $serverName = [string](Get-InputValue $Settings 'ServerName' '')
    $portNumber = [int](Get-InputValue $Settings 'PortNumber' 8530)
    $useSsl = [bool](Get-InputValue $Settings 'UseSsl' $false)
    if ([string]::IsNullOrWhiteSpace($serverName)) { throw [WsusAdapterError]::new('Input', 'No WSUS server is configured.') }
    [void](Import-WsusAdministrationAssembly)
    Test-WsusServerReachable -ServerName $serverName -PortNumber $portNumber
    try { return (New-WsusServerObject -ServerName $serverName -UseSsl $useSsl -PortNumber $portNumber) }
    catch { throw [WsusAdapterError]::new('Connect', (Get-WsusErrorMessage -ErrorRecord $_)) }
}

function Get-WsusUserRoleName {
    param([Parameter(Mandatory)]$Server)
    return [string]$Server.GetCurrentUserRole()
}

function Assert-WsusAdministrator {
    # Publishing, approval and certificate changes need WSUS Administrators
    # membership; the role check names that before a call fails halfway.
    param([Parameter(Mandatory)]$Server)
    $role = Get-WsusUserRoleName -Server $Server
    if ($role -ne 'Administrator') {
        throw [WsusAdapterError]::new('NotAdministrator', ('The account {0}\{1} has the role {2} on WSUS {3}. Add it to the WSUS Administrators group on the server.' -f $env:USERDOMAIN, $env:USERNAME, $role, [string]$Server.Name))
    }
}

function Get-WsusSigningCertificateObject {
    # The API saves the public certificate to a file on this computer rather
    # than returning it. $null when the server hands back no certificate.
    param([Parameter(Mandatory)]$Server)
    $path = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ('apwsus-cert-' + [guid]::NewGuid().ToString('N') + '.cer'))
    try {
        try { $Server.GetConfiguration().GetSigningCertificate($path) }
        catch {
            # A server without a registered certificate answers with the
            # Win32 file-not-found error rather than an empty result.
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            $nativeCode = $null
            if ($inner.PSObject.Properties['NativeErrorCode']) { $nativeCode = $inner.NativeErrorCode }
            if ($nativeCode -eq 2 -or $inner.HResult -eq -2147024894 -or $inner.Message -match 'cannot find the file') { return $null }
            throw
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { return $null }
        return (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, [System.IO.File]::ReadAllBytes($path)))
    }
    finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function ConvertTo-WsusCertificateInfo {
    param($Certificate)
    if ($null -eq $Certificate) { return $null }
    $keyLength = 0
    try { $keyLength = [int]$Certificate.PublicKey.Key.KeySize } catch { $keyLength = 0 }
    return [ordered]@{
        Subject    = [string]$Certificate.Subject
        Issuer     = [string]$Certificate.Issuer
        Thumbprint = [string]$Certificate.Thumbprint
        NotBefore  = ([datetime]$Certificate.NotBefore).ToUniversalTime().ToString('o')
        NotAfter   = ([datetime]$Certificate.NotAfter).ToUniversalTime().ToString('o')
        KeyLength  = $keyLength
    }
}

function Get-WsusLocallyPublishedUpdateObjects {
    param([Parameter(Mandatory)]$Server)
    $scope = New-Object Microsoft.UpdateServices.Administration.UpdateScope
    $scope.UpdateSources = [Microsoft.UpdateServices.Administration.UpdateSources]::Other
    return @($Server.GetUpdates($scope))
}

function Get-WsusUpdateObject {
    # $null when no update carries this id.
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)][guid]$PackageId)
    try { return $Server.GetUpdate((New-Object Microsoft.UpdateServices.Administration.UpdateRevisionId($PackageId))) }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        if ($inner.GetType().Name -eq 'WsusObjectNotFoundException' -or (Get-WsusErrorMessage -ErrorRecord $_) -match 'not found|could not be found|does not exist') { return $null }
        throw
    }
}

function Get-WsusComputerGroupObjects {
    param([Parameter(Mandatory)]$Server)
    return @($Server.GetComputerTargetGroups())
}

function Invoke-WsusUpdateApproval {
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)]$Update, [Parameter(Mandatory)][string]$GroupName)
    $group = @(Get-WsusComputerGroupObjects -Server $Server | Where-Object { [string]$_.Name -eq $GroupName }) | Select-Object -First 1
    if (-not $group) { throw ("WSUS has no computer group named '{0}'." -f $GroupName) }
    [void]$Update.Approve([Microsoft.UpdateServices.Administration.UpdateApprovalAction]::Install, $group)
}

function Invoke-WsusUpdateDecline { param([Parameter(Mandatory)]$Update) $Update.Decline() }

function Invoke-WsusUpdateExpiry { param([Parameter(Mandatory)]$Update) $Update.ExpirePackage() }

function Invoke-WsusUpdateDeletion {
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)][guid]$UpdateId)
    $Server.DeleteUpdate($UpdateId)
}

function Remove-WsusPackageFolder {
    # Deleting an update leaves its signed cab in the server's
    # UpdateServicesPackages share. The folder is named by the package id
    # and belongs to that one update.
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)][guid]$PackageId)
    $folder = '\\{0}\UpdateServicesPackages\{1}' -f [string]$Server.Name, $PackageId.ToString()
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return $false }
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop
    return $true
}

function Invoke-WsusCatalogImport {
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)][guid]$UpdateId)
    $Server.ImportUpdateFromCatalogSite($UpdateId.ToString(), [string[]]@())
}

function Resolve-WsusPayloadPath {
    # A recorded payload path resolves inside the content folder or not at
    # all: a rooted, drive-relative, stream, or parent-relative path would
    # copy a file from elsewhere on this computer into a signed update.
    param([Parameter(Mandatory)][string]$ContentFolder, [Parameter(Mandatory)][AllowEmptyString()][string]$RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains(':') -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw [WsusAdapterError]::new('Input', ("The staged file path '{0}' is not a path inside the content folder." -f $RelativePath))
    }
    $root = [System.IO.Path]::GetFullPath($ContentFolder).TrimEnd('\')
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $RelativePath))
    if (-not $full.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw [WsusAdapterError]::new('Input', ("The staged file path '{0}' points outside the content folder." -f $RelativePath))
    }
    return $full
}

function New-WsusSoftwareDistributionPackageFile {
    # Builds the Software Distribution Package through the API class, which
    # computes the file digests and writes schema-valid XML.
    param([Parameter(Mandatory)]$Plan, [Parameter(Mandatory)][string]$SourceFolder, [Parameter(Mandatory)][string]$Path)

    $sdp = New-Object Microsoft.UpdateServices.Administration.SoftwareDistributionPackage
    $installerPath = Resolve-WsusPayloadPath -ContentFolder $SourceFolder -RelativePath ([string]$Plan.InstallerFile)
    if ([string]$Plan.InstallerType -eq 'MSI') { $sdp.PopulatePackageFromWindowsInstaller($installerPath) }
    else { $sdp.PopulatePackageFromExe($installerPath) }

    $sdp.PackageId = [guid][string]$Plan.PackageId
    # Every package is a WSUS Update: WSUS files an Application package under
    # the Applications classification, which a Configuration Manager
    # software update point cannot subscribe to. Classification is an
    # Update-only property: the type is set first.
    $sdp.PackageType = [Microsoft.UpdateServices.Administration.PackageType]::Update
    $sdp.PackageUpdateClassification = [Microsoft.UpdateServices.Administration.PackageUpdateClassification][string]$Plan.Classification
    $sdp.Title = [string]$Plan.Title
    $sdp.Description = [string]$Plan.Description
    $sdp.VendorName = [string]$Plan.VendorName
    $sdp.ProductNames.Clear()
    [void]$sdp.ProductNames.Add([string]$Plan.ProductName)
    $supportUri = $null
    $supportUrl = [string](Get-InputValue $Plan 'SupportUrl' '')
    if ($supportUrl -and [Uri]::TryCreate($supportUrl, [UriKind]::Absolute, [ref]$supportUri)) { $sdp.SupportUrl = $supportUri }
    $sdp.IsInstallable = [string]$Plan.IsInstallable
    foreach ($superseded in @(Get-InputValue $Plan 'SupersededPackageIds' @())) { $sdp.SupersededPackages.Add([guid][string]$superseded) }

    # Populate writes item-level rules (InstalledOnce for an EXE,
    # MsiApplicationInstalled for an MSI) that are ANDed with any package
    # rule; the item rules are replaced so detection means what the manifest
    # says.
    $item = $sdp.InstallableItems[0]
    $item.IsInstallableApplicabilityRule = [string]$Plan.IsInstallable
    $item.IsInstalledApplicabilityRule = [string]$Plan.IsInstalled
    if ([string]$Plan.InstallerType -eq 'MSI') {
        $item.InstallCommandLine = [string]$Plan.CommandLine
    }
    else {
        $item.Arguments = [string]$Plan.CommandLine
        $item.RebootByDefault = $false
        $item.DefaultResult = [Microsoft.UpdateServices.Administration.InstallationResult]::Failed
        $item.ReturnCodes.Clear()
        foreach ($code in @(Get-InputValue $Plan 'ReturnCodes' @())) {
            $returnCode = New-Object Microsoft.UpdateServices.Administration.ReturnCode
            $returnCode.ReturnCodeValue = [int]$code.Code
            $returnCode.InstallationResult = [Microsoft.UpdateServices.Administration.InstallationResult][string]$code.Result
            $returnCode.IsRebootRequired = [bool]$code.Reboot
            $item.ReturnCodes.Add($returnCode)
        }
        # An item that can request user input is excluded from automatic
        # installation.
        if ($null -eq $item.InstallBehavior) { $item.InstallBehavior = New-Object Microsoft.UpdateServices.Administration.InstallBehavior }
        $item.InstallBehavior.CanRequestUserInput = $false
        $item.InstallBehavior.Impact = [Microsoft.UpdateServices.Administration.InstallationImpact]::Normal
        $item.InstallBehavior.RebootBehavior = [Microsoft.UpdateServices.Administration.RebootBehavior]::CanRequestReboot
    }
    $sdp.Save($Path)
}

function Invoke-WsusPublisher {
    # The package folder on the server takes the package id as its name, so
    # a later removal finds it whatever the server's default naming is.
    param([Parameter(Mandatory)]$Server, [Parameter(Mandatory)][string]$SdpPath, [Parameter(Mandatory)][string]$SourceFolder, [Parameter(Mandatory)][guid]$PackageId)
    $publisher = $Server.GetPublisher($SdpPath)
    $publisher.PublishPackage($SourceFolder, $PackageId.ToString())
}

function Read-WsusPfxPassword {
    # The password is the first line of standard input; it is never written
    # to the request file or the command line.
    $line = [Console]::In.ReadLine()
    $secure = New-Object System.Security.SecureString
    if ($null -ne $line) { foreach ($character in $line.ToCharArray()) { $secure.AppendChar($character) } }
    $secure.MakeReadOnly()
    return $secure
}

function Get-WsusUpdateRow {
    param([Parameter(Mandatory)]$Update, [hashtable]$GroupNames = @{})
    $approved = New-Object System.Collections.Generic.List[string]
    $approvalsRead = $true
    try {
        foreach ($approval in @($Update.GetUpdateApprovals())) {
            if ([string]$approval.Action -ne 'Install') { continue }
            $groupId = [string]$approval.ComputerTargetGroupId
            $approved.Add($(if ($GroupNames.ContainsKey($groupId)) { $GroupNames[$groupId] } else { $groupId }))
        }
    }
    catch { $approvalsRead = $false }
    $created = ''
    try { $created = ([datetime]$Update.CreationDate).ToUniversalTime().ToString('o') } catch { $created = '' }
    return [ordered]@{
        PackageId        = [string]$Update.Id.UpdateId
        Title            = [string]$Update.Title
        Description      = [string]$Update.Description
        Created          = $created
        ApprovedGroups   = @($approved.ToArray())
        ApprovalsRead    = $approvalsRead
        Declined         = [bool]$Update.IsDeclined
        Superseded       = [bool]$Update.IsSuperseded
        PublicationState = [string](Get-InputValue $Update 'PublicationState' '')
    }
}

function Get-WsusGroupNameMap {
    param([Parameter(Mandatory)]$Server)
    $names = @{}
    try { foreach ($group in @(Get-WsusComputerGroupObjects -Server $Server)) { $names[[string]$group.Id] = [string]$group.Name } } catch { $names = @{} }
    return $names
}

# ---------------------------------------------------------------------------
# Operations: each takes the request and returns the Data object.
# ---------------------------------------------------------------------------

function Invoke-WsusStatusOperation {
    param([Parameter(Mandatory)]$Request)
    $settings = Get-InputValue $Request 'Settings'
    $api = [ordered]@{ Available = $false; Version = ''; Location = ''; Reason = '' }
    try {
        $assembly = Import-WsusAdministrationAssembly
        $api.Available = $true; $api.Version = [string]$assembly.GetName().Version; $api.Location = [string]$assembly.Location
    }
    catch { $api.Reason = Get-WsusErrorMessage -ErrorRecord $_ }
    $data = [ordered]@{
        Api = $api; Connected = $false; ServerName = [string](Get-InputValue $settings 'ServerName' ''); Version = ''; Role = ''
        RoleError = ''; SecureConnection = $false; SigningCertificate = $null; CertificateError = ''; ConnectErrorKind = ''; ConnectError = ''
    }
    try { $server = Get-WsusServerConnection -Settings $settings }
    catch {
        $data.ConnectErrorKind = Get-WsusErrorKind -ErrorRecord $_
        $data.ConnectError = Get-WsusErrorMessage -ErrorRecord $_
        return $data
    }
    $data.Connected = $true
    $data.ServerName = [string]$server.Name
    $data.Version = [string]$server.Version
    try { $data.Role = Get-WsusUserRoleName -Server $server } catch { $data.RoleError = Get-WsusErrorMessage -ErrorRecord $_ }
    try { $data.SigningCertificate = ConvertTo-WsusCertificateInfo -Certificate (Get-WsusSigningCertificateObject -Server $server) }
    catch { $data.CertificateError = Get-WsusErrorMessage -ErrorRecord $_ }
    try { $data.SecureConnection = [bool]$server.IsConnectionSecureForApiRemoting } catch { $data.SecureConnection = $false }
    return $data
}

function Invoke-WsusCreateSelfSignedOperation {
    param([Parameter(Mandatory)]$Request)
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server
    $configuration = $server.GetConfiguration()
    try { $configuration.SetSigningCertificate() }
    catch { throw [WsusAdapterError]::new('SelfSignedRefused', (Get-WsusErrorMessage -ErrorRecord $_)) }
    $configuration.Save()
    return (Invoke-WsusStatusOperation -Request $Request)
}

function Invoke-WsusImportPfxOperation {
    param([Parameter(Mandatory)]$Request)
    $pfxPath = [string](Get-InputValue $Request 'PfxPath' '')
    if (-not (Test-Path -LiteralPath $pfxPath -PathType Leaf)) { throw [WsusAdapterError]::new('Input', ('PFX file not found: {0}' -f $pfxPath)) }
    $password = Read-WsusPfxPassword
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server
    # The PFX bytes and the password travel inside the API request.
    if (-not [bool]$server.IsConnectionSecureForApiRemoting) {
        throw [WsusAdapterError]::new('InsecureConnection', 'Importing a PFX sends its private key and password to the server inside the API call, and this connection is not secure. Connect with SSL, or run AppPackager on the WSUS server.')
    }
    $configuration = $server.GetConfiguration()
    $configuration.SetSigningCertificate($pfxPath, $password)
    $configuration.Save()
    $password.Dispose()
    return (Invoke-WsusStatusOperation -Request $Request)
}

function Invoke-WsusExportCerOperation {
    param([Parameter(Mandatory)]$Request)
    $path = [string](Get-InputValue $Request 'CerPath' '')
    if ([string]::IsNullOrWhiteSpace($path)) { throw [WsusAdapterError]::new('Input', 'No certificate file path was given.') }
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    $certificate = Get-WsusSigningCertificateObject -Server $server
    if (-not $certificate) { throw [WsusAdapterError]::new('NoCertificate', ('WSUS server {0} has no signing certificate to export.' -f [string]$server.Name)) }
    [System.IO.File]::WriteAllBytes($path, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    return [ordered]@{ CerPath = $path; Thumbprint = [string]$certificate.Thumbprint }
}

function Invoke-WsusGroupsOperation {
    param([Parameter(Mandatory)]$Request)
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    return [ordered]@{ Names = @(Get-WsusComputerGroupObjects -Server $server | ForEach-Object { [string]$_.Name } | Sort-Object -Unique) }
}

function Invoke-WsusListOperation {
    param([Parameter(Mandatory)]$Request)
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    $groupNames = Get-WsusGroupNameMap -Server $server
    return [ordered]@{ Rows = @(Get-WsusLocallyPublishedUpdateObjects -Server $server | ForEach-Object { Get-WsusUpdateRow -Update $_ -GroupNames $groupNames }) }
}

function Invoke-WsusPublishPrepare {
    # Facts the publish decision needs, read before anything is written:
    # the account role, the signing certificate, the cab limit, and the
    # locally published updates whose description names this identity.
    param([Parameter(Mandatory)]$Request)
    $identity = [string](Get-InputValue $Request 'IdentityTag' '')
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server
    $data = [ordered]@{ ServerName = [string]$server.Name; CertificateReadable = $true; CertificateError = ''; SigningCertificate = $null; CabLimitMegabytes = 0; Candidates = @() }
    try { $data.SigningCertificate = ConvertTo-WsusCertificateInfo -Certificate (Get-WsusSigningCertificateObject -Server $server) }
    catch { $data.CertificateReadable = $false; $data.CertificateError = Get-WsusErrorMessage -ErrorRecord $_ }
    try { $data.CabLimitMegabytes = [int]$server.GetConfiguration().LocalPublishingMaxCabSize } catch { $data.CabLimitMegabytes = 0 }
    $data.Candidates = @(Get-WsusLocallyPublishedUpdateObjects -Server $server |
        Where-Object { $identity -and ([string]$_.Description).Contains($identity) } |
        ForEach-Object { Get-WsusUpdateRow -Update $_ })
    return $data
}

function Invoke-WsusPublishCommit {
    # Publishes the planned package unless the request names an existing
    # update, then applies the approval and the declines. A failed approval
    # or decline is reported per item; only the publish itself throws.
    param([Parameter(Mandatory)]$Request)
    $plan = Get-InputValue $Request 'Plan'
    $existingId = [string](Get-InputValue $Request 'ExistingPackageId' '')
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server
    $packageId = if ($existingId) { [guid]$existingId } else { [guid][string]$plan.PackageId }
    $data = [ordered]@{ ServerName = [string]$server.Name; PackageId = $packageId.ToString(); Published = $false; UpdateVisible = $false; Approval = $null; Declines = @() }

    if (-not $existingId) {
        $contentFolder = [string](Get-InputValue $Request 'ContentFolder' '')
        $work = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ('apwsus-' + [guid]::NewGuid().ToString('N')))
        $source = [System.IO.Path]::Combine($work, 'content')
        [void](New-Item -ItemType Directory -Path $source -Force)
        try {
            foreach ($file in @(Get-InputValue $Request 'PayloadFiles' @())) {
                $target = Resolve-WsusPayloadPath -ContentFolder $source -RelativePath ([string]$file)
                [void](New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($target)) -Force)
                Copy-Item -LiteralPath (Resolve-WsusPayloadPath -ContentFolder $contentFolder -RelativePath ([string]$file)) -Destination $target -Force -ErrorAction Stop
            }
            $sdpPath = [System.IO.Path]::Combine($work, 'package.xml')
            New-WsusSoftwareDistributionPackageFile -Plan $plan -SourceFolder $source -Path $sdpPath
            try { Invoke-WsusPublisher -Server $server -SdpPath $sdpPath -SourceFolder $source -PackageId $packageId }
            catch { throw [WsusAdapterError]::new('PublishRejected', (Get-WsusErrorMessage -ErrorRecord $_)) }
            $data.Published = $true
        }
        finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }

    if ([bool](Get-InputValue $Request 'SkipFollowUp' $false)) { return $data }
    $update = Get-WsusUpdateObject -Server $server -PackageId $packageId
    $data.UpdateVisible = [bool]$update
    $group = [string](Get-InputValue $Request 'ApprovalGroup' '')
    if ($group) {
        $approval = [ordered]@{ Group = $group; Ok = $false; Message = '' }
        try {
            if (-not $update) { throw ('the update is not visible on {0} after publishing' -f [string]$server.Name) }
            Invoke-WsusUpdateApproval -Server $server -Update $update -GroupName $group
            $approval.Ok = $true
        }
        catch { $approval.Message = Get-WsusErrorMessage -ErrorRecord $_ }
        $data.Approval = $approval
    }
    $declines = New-Object System.Collections.Generic.List[object]
    foreach ($id in @(Get-InputValue $Request 'DeclinePackageIds' @())) {
        $entry = [ordered]@{ PackageId = [string]$id; Ok = $false; Message = '' }
        try {
            $old = Get-WsusUpdateObject -Server $server -PackageId ([guid][string]$id)
            if (-not $old) { throw ('WSUS has no update with id {0}.' -f $id) }
            Invoke-WsusUpdateDecline -Update $old
            $entry.Ok = $true
        }
        catch { $entry.Message = Get-WsusErrorMessage -ErrorRecord $_ }
        $declines.Add($entry)
    }
    $data.Declines = @($declines.ToArray())
    return $data
}

function Invoke-WsusSetStateOperation {
    # One connection serves every id. Each id succeeds or fails on its own;
    # only a connection or permission failure throws. Remove declines the
    # update, deletes it from the server database, and then deletes its
    # package folder on the server.
    param([Parameter(Mandatory)]$Request)
    $action = [string](Get-InputValue $Request 'Action' '')
    if ($action -notin @('Approve', 'Decline', 'Expire', 'Remove')) { throw [WsusAdapterError]::new('Input', ("Action '{0}' is not Approve, Decline, Expire or Remove." -f $action)) }
    $group = [string](Get-InputValue $Request 'GroupName' '')
    if ($action -eq 'Approve' -and [string]::IsNullOrWhiteSpace($group)) { throw [WsusAdapterError]::new('Input', 'Approve needs a computer group.') }
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($id in @(@(Get-InputValue $Request 'PackageIds' @()) | ForEach-Object { [string]$_ } | Select-Object -Unique)) {
        $entry = [ordered]@{ PackageId = $id; Title = ''; Found = $true; Ok = $false; Message = ''; DeclinedNow = $false; FolderError = '' }
        $update = Get-WsusUpdateObject -Server $server -PackageId ([guid]$id)
        if (-not $update) { $entry.Found = $false; $results.Add($entry); continue }
        $entry.Title = [string]$update.Title
        try {
            switch ($action) {
                'Approve' { Invoke-WsusUpdateApproval -Server $server -Update $update -GroupName $group }
                'Decline' { Invoke-WsusUpdateDecline -Update $update }
                'Expire' { Invoke-WsusUpdateExpiry -Update $update }
                'Remove' {
                    if (-not [bool]$update.IsDeclined) {
                        Invoke-WsusUpdateDecline -Update $update
                        $entry.DeclinedNow = $true
                    }
                    Invoke-WsusUpdateDeletion -Server $server -UpdateId ([guid]$id)
                    try { [void](Remove-WsusPackageFolder -Server $server -PackageId ([guid]$id)) }
                    catch { $entry.FolderError = Get-WsusErrorMessage -ErrorRecord $_ }
                }
            }
            $entry.Ok = $true
        }
        catch { $entry.Message = Get-WsusErrorMessage -ErrorRecord $_ }
        $results.Add($entry)
    }
    return [ordered]@{ Results = @($results.ToArray()) }
}

function Invoke-WsusCatalogImportOperation {
    # The server downloads the update metadata from the catalog itself; the
    # update files follow the server's Update files setting.
    param([Parameter(Mandatory)]$Request)
    $ids = @(@(Get-InputValue $Request 'UpdateIds' @()) | ForEach-Object { [string]$_ })
    $server = Get-WsusServerConnection -Settings (Get-InputValue $Request 'Settings')
    Assert-WsusAdministrator -Server $server
    $results = New-Object System.Collections.Generic.List[object]
    foreach ($id in $ids) {
        $entry = [ordered]@{ UpdateId = $id; Ok = $false; Message = '' }
        try { Invoke-WsusCatalogImport -Server $server -UpdateId ([guid]$id); $entry.Ok = $true }
        catch { $entry.Message = Get-WsusErrorMessage -ErrorRecord $_ }
        $results.Add($entry)
    }
    return [ordered]@{ Results = @($results.ToArray()) }
}

function Write-WsusAdapterResult {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Result)
    $json = ConvertTo-Json -InputObject $Result -Depth 10 -Compress
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-WsusAdapter {
    param([Parameter(Mandatory)][string]$InputPath)
    $request = [System.IO.File]::ReadAllText($InputPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $operation = [string](Get-InputValue $request 'Operation' '')
    $resultPath = [string](Get-InputValue $request 'ResultPath' '')
    if ([string]::IsNullOrWhiteSpace($resultPath)) { throw 'The request names no ResultPath.' }
    $result = [ordered]@{ Ok = $true; Operation = $operation; ErrorKind = ''; Error = ''; Data = $null }
    try {
        $result.Data = switch ($operation) {
            'Status' { Invoke-WsusStatusOperation -Request $request }
            'CreateSelfSigned' { Invoke-WsusCreateSelfSignedOperation -Request $request }
            'ImportPfx' { Invoke-WsusImportPfxOperation -Request $request }
            'ExportCer' { Invoke-WsusExportCerOperation -Request $request }
            'Groups' { Invoke-WsusGroupsOperation -Request $request }
            'PublishPrepare' { Invoke-WsusPublishPrepare -Request $request }
            'Publish' { Invoke-WsusPublishCommit -Request $request }
            'List' { Invoke-WsusListOperation -Request $request }
            'SetState' { Invoke-WsusSetStateOperation -Request $request }
            'CatalogImport' { Invoke-WsusCatalogImportOperation -Request $request }
            default { throw [WsusAdapterError]::new('Input', ("Operation '{0}' is not known." -f $operation)) }
        }
    }
    catch {
        $result.Ok = $false
        $result.ErrorKind = Get-WsusErrorKind -ErrorRecord $_
        $result.Error = Get-WsusErrorMessage -ErrorRecord $_
    }
    Write-WsusAdapterResult -Path $resultPath -Result $result
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    Invoke-WsusAdapter -InputPath $InputPath
}
