using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>Signing data the ConfigMgr script generation reads.</summary>
public sealed class MecmScriptSigningContext
{
    /// <summary>The policy in force at package time; governs requirement-script signing and the Require checks.</summary>
    public SigningPolicy Policy { get; init; } = new();
    /// <summary>The ScriptSigning record of the stage manifest; null for a stage that carries none.</summary>
    public ScriptSigningRecord? Record { get; init; }
    public ScriptSigningService? Signer { get; init; }

    public static MecmScriptSigningContext FromManifest(Models.StageManifest manifest, SigningPolicy? policy) => new()
    {
        Policy = SigningPolicy.Normalize(policy),
        Record = ScriptSigningRecord.FromJsonNode(manifest.ScriptSigning)
    };
}

/// <summary>
/// PowerShell emitted into the ConfigMgr script for script transport and
/// read-back. A cmdlet reports success without the provider having stored
/// what was sent, and a signed script can come back as readable text whose
/// signature no longer verifies, so every script-carrying write is read back:
/// byte for byte when signed, as normalized text otherwise, and a signed body
/// is then verified for integrity and signer thumbprint. Host trust is
/// recorded, never required.
/// </summary>
public static class MecmScriptSigningPs
{
    public const string DetectionReadBackFunction = "Test-ApkgStoredDetectionScript";
    public const string DetectionFileFunction = "Resolve-ApkgDetectionScriptFile";
    public const string ScriptGlobalConditionFunction = "Get-ApkgScriptGlobalCondition";

    /// <summary>
    /// Shared helpers. A signed body is stored as a plain-text preview
    /// followed by an ENCODEDSCRIPT block whose base64 payload is the imported
    /// file byte for byte; an unsigned body is stored as plain text.
    /// </summary>
    public static string ReadBackHelpers() =>
        """
        function ConvertFrom-ApkgEncodedScriptBody {
            param([AllowEmptyString()][string]$Body)
            $match = [regex]::Match($Body, '#\s*ENCODEDSCRIPT\s*#.*?script block\s*#(?<payload>[A-Za-z0-9+/=\s]+?)#\s*ENCODEDSCRIPT\s*#', 'Singleline, IgnoreCase')
            if ($match.Success) {
                $payload = ($match.Groups['payload'].Value -replace '\s', '')
                try {
                    $bytes = [Convert]::FromBase64String($payload)
                    return [pscustomobject]@{ Bytes = $bytes; Text = [System.Text.Encoding]::UTF8.GetString($bytes); Encoded = $true }
                }
                catch {
                    Write-Host "A stored script carried an encoded block that is not valid base64: $($_.Exception.Message)"
                }
            }
            return [pscustomobject]@{ Bytes = [System.Text.Encoding]::UTF8.GetBytes($Body); Text = $Body; Encoded = $false }
        }
        function Get-ApkgSdmScriptBody {
            param([AllowNull()][string]$SdmPackageXml, [AllowEmptyString()][string]$DeploymentTypeName = '')
            if ([string]::IsNullOrWhiteSpace($SdmPackageXml)) { return $null }
            try { $document = [xml]$SdmPackageXml }
            catch { Write-Host "Could not parse SDMPackageXML for a script read-back: $($_.Exception.Message)"; return $null }
            $scope = $document
            if (-not [string]::IsNullOrWhiteSpace($DeploymentTypeName)) {
                $all = @($document.SelectNodes("//*[local-name()='DeploymentType']"))
                $node = $all | Where-Object { $title = $_.SelectSingleNode("*[local-name()='Title']"); $title -and $title.InnerText -ceq $DeploymentTypeName } | Select-Object -First 1
                if (-not $node -and $all.Count -eq 1) { $node = $all[0] }
                if (-not $node) { return $null }
                $scope = $node
            }
            foreach ($xpath in @(".//*[local-name()='Arg'][@Name='ScriptBody']", ".//*[local-name()='DetectionScript']", ".//*[local-name()='ScriptBody']")) {
                $found = $scope.SelectSingleNode($xpath)
                if ($found -and -not [string]::IsNullOrWhiteSpace($found.InnerText)) { return [string]$found.InnerText }
            }
            return $null
        }
        function Compare-ApkgBytes {
            param([AllowNull()][byte[]]$Left, [AllowNull()][byte[]]$Right)
            if ($null -eq $Left -or $null -eq $Right) { return $false }
            if ($Left.Length -ne $Right.Length) { return $false }
            for ($i = 0; $i -lt $Left.Length; $i++) { if ($Left[$i] -ne $Right[$i]) { return $false } }
            return $true
        }
        function ConvertTo-ApkgComparableText {
            param([AllowNull()][string]$Text)
            return ([string]$Text).Replace("`r`n", "`n").TrimEnd("`r", "`n")
        }
        function Test-ApkgStoredScriptSignature {
            param([AllowEmptyCollection()][byte[]]$Bytes, [string]$Context, [AllowEmptyString()][string]$ExpectedThumbprint = '')
            $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('appackager-sign-' + [guid]::NewGuid().ToString('N') + '.ps1')
            try {
                [System.IO.File]::WriteAllBytes($temp, $Bytes)
                $sig = Get-AuthenticodeSignature -LiteralPath $temp
            }
            finally { [System.IO.File]::Delete($temp) }
            $thumb = ''
            if ($null -ne $sig.SignerCertificate) { $thumb = $sig.SignerCertificate.Thumbprint.ToUpperInvariant() }
            $trusted = ([string]$sig.Status -eq 'Valid')
            $intact = $trusted
            if (-not $intact -and $null -ne $sig.SignerCertificate -and [string]$sig.Status -eq 'UnknownError') {
                $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
                try {
                    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                    $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
                    $intact = [bool]$chain.Build($sig.SignerCertificate)
                }
                catch { $intact = $false }
                finally { if ($chain -is [System.IDisposable]) { $chain.Dispose() } }
            }
            if (-not $intact) {
                throw ("The signature on the stored script for {0} did not verify after read-back: {1} ({2})." -f $Context, $sig.Status, $sig.StatusMessage)
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and $thumb -ne $ExpectedThumbprint) {
                throw ("The stored script for {0} is signed by {1}, not by the certificate this build signed with ({2})." -f $Context, $thumb, $ExpectedThumbprint)
            }
            $trustState = if ($trusted) { 'chain trusted on this host' } else { "chain not trusted on this host: $($sig.Status)" }
            Write-Host ("Signature verified           : {0} ({1}); {2}; endpoint publisher trust is a separate client configuration." -f $Context, $thumb, $trustState)
        }
        """.ReplaceLineEndings("\r\n");

    /// <summary>
    /// Detection transport and read-back. A finalized detection file carries
    /// the exact bytes the signature covers and is imported with -ScriptFile;
    /// the site server reads that path from the console host, so a share path
    /// is copied byte for byte to a local temporary file first. The read-back
    /// reads the application document, not the deployment type object:
    /// Get-CMDeploymentType returns the first type's SDMPackageXML for every
    /// type of an application.
    /// </summary>
    public static string DetectionFunctions() =>
        """
        function Resolve-ApkgDetectionScriptFile {
            param([string]$Path, [string]$ScriptFile, [string]$ContentLocation, [bool]$Signed)
            if (-not (Test-Path -LiteralPath ("FileSystem::" + $Path) -PathType Leaf)) {
                throw ("Detection script '{0}' was not found under the content location '{1}'; re-run the Stage phase." -f $ScriptFile, $ContentLocation)
            }
            $bytes = [System.IO.File]::ReadAllBytes($Path)
            if ($bytes.Length -gt 32768) {
                throw ("The finalized detection script is {0} bytes, above the {1}-byte limit a deployment type accepts; shorten the detector or use a native detection rule." -f $bytes.Length, 32768)
            }
            $temporary = ''
            $importPath = $Path
            if ($Path -like '\\*') {
                $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '.ps1')
                [System.IO.File]::WriteAllBytes($temporary, $bytes)
                $importPath = $temporary
            }
            Write-Host "Detection script (file)      : $importPath"
            return [pscustomobject]@{ Transport = 'File'; Path = $importPath; TempPath = $temporary; Text = [System.Text.Encoding]::UTF8.GetString($bytes); Bytes = $bytes; Signed = $Signed }
        }
        function Test-ApkgStoredDetectionScript {
            param([string]$ApplicationName, [string]$DeploymentTypeName, $Transport, [AllowEmptyString()][string]$ExpectedThumbprint = '', [bool]$RequireDetection = $false)
            try {
                $stored = $null
                try {
                    $application = @(Get-CMApplication -Name $ApplicationName -DisableWildcardHandling -ErrorAction Stop) | Select-Object -First 1
                    if ($application -and $application.PSObject.Properties['SDMPackageXML']) {
                        $body = Get-ApkgSdmScriptBody -SdmPackageXml ([string]$application.SDMPackageXML) -DeploymentTypeName $DeploymentTypeName
                        if ($null -ne $body) { $stored = ConvertFrom-ApkgEncodedScriptBody -Body $body }
                    }
                }
                catch { Write-Host "Detection read-back unavailable: $($_.Exception.Message)" }
                if ($null -eq $stored) {
                    if ($Transport.Signed -or $RequireDetection) {
                        throw ("The detection script stored on deployment type '{0}' could not be read back, so its signature cannot be verified." -f $DeploymentTypeName)
                    }
                    Write-Host ("Detection read-back          : '{0}' stored no readable script body; not compared." -f $DeploymentTypeName)
                    return
                }
                if ($stored.Encoded -or $Transport.Signed) {
                    if (-not (Compare-ApkgBytes -Left $stored.Bytes -Right $Transport.Bytes)) {
                        throw ("Deployment type '{0}' read back different detection script bytes than were sent." -f $DeploymentTypeName)
                    }
                }
                elseif ((ConvertTo-ApkgComparableText $stored.Text) -ne (ConvertTo-ApkgComparableText $Transport.Text)) {
                    throw ("Deployment type '{0}' read back different detection script content than was sent." -f $DeploymentTypeName)
                }
                if ($Transport.Signed -or $RequireDetection) {
                    Test-ApkgStoredScriptSignature -Bytes $stored.Bytes -Context ("deployment type '$DeploymentTypeName'") -ExpectedThumbprint $ExpectedThumbprint
                }
                Write-Host ("Detection read-back          : {0} verified" -f $DeploymentTypeName)
            }
            finally {
                if (-not [string]::IsNullOrWhiteSpace([string]$Transport.TempPath)) { [System.IO.File]::Delete([string]$Transport.TempPath) }
            }
        }
        """.ReplaceLineEndings("\r\n");

    /// <summary>
    /// The Script-kind global condition resolver. A script global condition
    /// is a shared site object: a signed script, or one whose stored body
    /// differs from this run's, never changes the existing condition. It
    /// resolves to '&lt;name&gt; (&lt;8 hex of the final bytes' SHA-256&gt;)'
    /// instead, so other applications that reference the original keep it.
    /// The provider refuses a signed script passed as text, so a signed body
    /// is imported through -FilePath with its exact bytes.
    /// </summary>
    public static string ScriptGlobalConditionFunctions() =>
        """
        function Find-ApkgGlobalConditionByName {
            param([string]$Name, [int]$PlatformType)
            $found = @(Get-CMGlobalCondition -Name $Name)
            if ($PlatformType -gt 0 -and $found.Count -gt 1) {
                $found = @($found | Where-Object { [int]$_.PlatformType -eq $PlatformType })
            }
            if ($found.Count -gt 1) {
                throw "Global condition name '$Name' matches $($found.Count) site conditions; add a PlatformType to the template or rename to a unique condition."
            }
            if ($found.Count -eq 1) { return $found[0] }
            return $null
        }
        function Get-ApkgGlobalConditionScriptBody {
            param($GlobalCondition)
            $property = $GlobalCondition.PSObject.Properties['SDMPackageXML']
            if (-not $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                try {
                    $reloaded = Get-CMGlobalCondition -Id ([string]$GlobalCondition.CI_ID) -ErrorAction Stop
                    $property = $reloaded.PSObject.Properties['SDMPackageXML']
                }
                catch { return $null }
            }
            if (-not $property) { return $null }
            $body = Get-ApkgSdmScriptBody -SdmPackageXml ([string]$property.Value)
            if ($null -eq $body) { return $null }
            return (ConvertFrom-ApkgEncodedScriptBody -Body $body)
        }
        function New-ApkgScriptGlobalConditionVerified {
            param([string]$Name, [int]$PlatformType, [string]$DataType, [string]$Description, [string]$ScriptText, [string]$SignedBase64, [string]$ExpectedThumbprint)
            $signed = -not [string]::IsNullOrEmpty($SignedBase64)
            $signedBytes = $null
            $temporary = ''
            try {
                if ($signed) {
                    $signedBytes = [Convert]::FromBase64String($SignedBase64)
                    $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '.ps1')
                    [System.IO.File]::WriteAllBytes($temporary, $signedBytes)
                    New-CMGlobalConditionScript -Name $Name -DataType $DataType -ScriptLanguage PowerShell -FilePath $temporary -Description $Description | Out-Null
                }
                else {
                    New-CMGlobalConditionScript -Name $Name -DataType $DataType -ScriptLanguage PowerShell -ScriptText $ScriptText -Description $Description | Out-Null
                }
            }
            finally {
                if ($temporary) { [System.IO.File]::Delete($temporary) }
            }
            $created = Find-ApkgGlobalConditionByName -Name $Name -PlatformType $PlatformType
            if (-not $created) {
                throw "Global condition '$Name' is not readable after creation; the provider refused the write."
            }
            $stored = Get-ApkgGlobalConditionScriptBody -GlobalCondition $created
            if ($null -eq $stored) {
                Write-Host ("Global condition read-back   : '{0}' stored no readable script body; the site copy could not be compared." -f $Name)
                return $created
            }
            if ($signed) {
                if (-not (Compare-ApkgBytes -Left $stored.Bytes -Right $signedBytes)) {
                    throw ("Global condition '{0}' read back different script bytes than were sent; the site did not store the signed script." -f $Name)
                }
                Test-ApkgStoredScriptSignature -Bytes $stored.Bytes -Context ("global condition '$Name'") -ExpectedThumbprint $ExpectedThumbprint
            }
            elseif ((ConvertTo-ApkgComparableText $stored.Text) -ne (ConvertTo-ApkgComparableText $ScriptText)) {
                throw ("Global condition '{0}' read back different script content than was sent; the site did not store the finalized script." -f $Name)
            }
            Write-Host ("Global condition read-back   : {0} verified" -f $Name)
            return $created
        }
        function Get-ApkgScriptGlobalCondition {
            param([string]$Name, [int]$PlatformType, [string]$DataType, [string]$Description,
                  [string]$ScriptText, [string]$SignedBase64, [string]$ContentHash, [string]$ExpectedThumbprint)
            $signed = -not [string]::IsNullOrEmpty($SignedBase64)
            $base = Find-ApkgGlobalConditionByName -Name $Name -PlatformType $PlatformType
            if (-not $signed) {
                if (-not $base) {
                    Write-Host "Global condition (creating)  : $Name"
                    return (New-ApkgScriptGlobalConditionVerified -Name $Name -PlatformType $PlatformType -DataType $DataType -Description $Description -ScriptText $ScriptText -SignedBase64 '' -ExpectedThumbprint '')
                }
                $stored = Get-ApkgGlobalConditionScriptBody -GlobalCondition $base
                if ($null -eq $stored -or (ConvertTo-ApkgComparableText $stored.Text) -eq (ConvertTo-ApkgComparableText $ScriptText)) {
                    Write-Host "Global condition (existing)  : $Name"
                    return $base
                }
            }
            $versionedName = '{0} ({1})' -f $Name, $ContentHash
            $versioned = Find-ApkgGlobalConditionByName -Name $versionedName -PlatformType $PlatformType
            if ($versioned) {
                Write-Host "Global condition (existing)  : $versionedName"
                return $versioned
            }
            if ($base) {
                Write-Host ("Global condition unchanged   : '{0}' keeps its current script and its existing application references; this run uses '{1}'." -f $Name, $versionedName)
            }
            Write-Host "Global condition (creating)  : $versionedName"
            return (New-ApkgScriptGlobalConditionVerified -Name $versionedName -PlatformType $PlatformType -DataType $DataType -Description $Description -ScriptText $ScriptText -SignedBase64 $SignedBase64 -ExpectedThumbprint $ExpectedThumbprint)
        }
        """.ReplaceLineEndings("\r\n");
}
