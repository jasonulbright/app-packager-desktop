<#
.SYNOPSIS
    Makes sure the .NET Windows Desktop Runtime (x64) that App Packager Desktop needs is installed.

.DESCRIPTION
    The installer runs this script before it copies any file. App Packager Desktop is not self-contained:
    it needs Microsoft.WindowsDesktop.App at the minimum version, x64, on the computer.

    1. It reads the installed runtimes from the 64-bit registry view (HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx).
       A runtime at the minimum version or newer is enough, because the application rolls forward to a newer major version.
    2. When none is installed, it downloads the latest 10.x Windows Desktop Runtime installer from the official Microsoft
       address, over https only.
    3. It checks the download before it runs it: the Authenticode signature must be valid and carry Microsoft Corporation
       as the signer, and the file version must be at least the minimum.
    4. It runs the runtime installer with elevation (Windows asks for consent) and checks the result again.

    The functions can be loaded without a run: dot-source the file. The main part runs only when the file is run.

.PARAMETER MinimumVersion
    Lowest accepted version of Microsoft.WindowsDesktop.App. The installer passes the value of the project.

.PARAMETER CheckOnly
    Only report whether a runtime at the minimum version is installed. Exit code 0 when yes, 1 when no.

.PARAMETER Quiet
    The runtime installer shows no window (the installer's /S switch).

.PARAMETER DownloadUrl
    The runtime installer address. Https only. The default is the Microsoft redirect to the latest 10.x release.

.OUTPUTS
    Exit codes: 0 the runtime is installed (found or installed now), 1 not installed (CheckOnly), 2 the download failed,
    3 the download failed a check (address, signature or version), 4 the runtime installer was declined or failed,
    5 the runtime installer ended and the runtime is still missing, 10 installed and Windows asks for a restart.
#>
[CmdletBinding()]
param(
    [string]$MinimumVersion = '10.0.14',
    [switch]$CheckOnly,
    [switch]$Quiet,
    [string]$DownloadUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
)

$ErrorActionPreference = 'Stop'

# A version as four numbers, or $null when the text is not a plain release version.
# A pre-release such as 11.0.0-rc.1 is not accepted: the application does not run on it by choice.
function ConvertTo-RuntimeVersion {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $match = [regex]::Match($Text.Trim(), '^(\d+)\.(\d+)\.(\d+)$')
    if (-not $match.Success) { return $null }
    try { return [version]::new([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value) }
    catch { return $null }
}

# True when one of the versions is the minimum or newer.
function Test-DesktopRuntimeSatisfied {
    param([string[]]$InstalledVersions, [string]$Minimum)
    $required = ConvertTo-RuntimeVersion $Minimum
    if ($null -eq $required) { throw "The minimum version '$Minimum' is not a version." }
    foreach ($text in @($InstalledVersions)) {
        $version = ConvertTo-RuntimeVersion $text
        if ($null -ne $version -and $version -ge $required) { return $true }
    }
    return $false
}

# The folder names under <root>\shared\Microsoft.WindowsDesktop.App: the way the application host finds a runtime.
function Get-DesktopRuntimeVersionsInRoot {
    param([string]$Root)
    if ([string]::IsNullOrWhiteSpace($Root)) { return @() }
    $shared = Join-Path $Root 'shared\Microsoft.WindowsDesktop.App'
    if (-not (Test-Path -LiteralPath $shared -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $shared -Directory | ForEach-Object { $_.Name })
}

# The versions of Microsoft.WindowsDesktop.App that are installed (x64). Two sources, because not every
# installer writes the registry list: the 64-bit registry view (read whatever the bitness of this process)
# and the runtime folders under the install roots that the host searches (the registry install location,
# DOTNET_ROOT and the default folder).
function Get-InstalledDesktopRuntimeVersions {
    $names = @()
    $roots = @()
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App')
        if ($null -ne $key) {
            try { $names += @($key.GetValueNames()) } finally { $key.Dispose() }
        }
        $install = $base.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        if ($null -ne $install) {
            try { $roots += [string]$install.GetValue('InstallLocation') } finally { $install.Dispose() }
        }
    }
    finally { $base.Dispose() }
    $roots += $env:DOTNET_ROOT
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if ($programFiles) { $roots += (Join-Path $programFiles 'dotnet') }
    foreach ($root in ($roots | Where-Object { $_ } | Select-Object -Unique)) {
        $names += Get-DesktopRuntimeVersionsInRoot -Root $root
    }
    return @($names | Select-Object -Unique)
}

# True when the address is https and its host is exactly one that the runtime is served from: the aka.ms redirect
# and the two Microsoft download hosts. A host of a wider kind (a storage or CDN domain) is not accepted.
function Test-RuntimeDownloadUri {
    param([uri]$Uri)
    if ($null -eq $Uri -or $Uri.Scheme -ne 'https') { return $false }
    return ($Uri.Host -in @('aka.ms', 'builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com'))
}

# True when the product name is the Windows Desktop Runtime installer for x64, for example
# "Microsoft Windows Desktop Runtime 10.0.14 (x64)" (the 8.0 installers write "Runtime - 8.0.33 (x64)").
function Test-DesktopRuntimeProductName {
    param([string]$ProductName)
    return ($ProductName -match '^Microsoft Windows Desktop Runtime( -)? \d+\.\d+\.\d+ \(x64\)$')
}

# True when the certificate subject names Microsoft Corporation as the organization.
function Test-MicrosoftSignerSubject {
    param([string]$Subject)
    return ($Subject -match '(^|, )O=Microsoft Corporation(,|$)')
}

# Throws unless the file has a valid Authenticode signature of Microsoft Corporation.
function Assert-MicrosoftSigned {
    param([string]$Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "The signature of the download is not valid ($($signature.Status))." }
    $subject = [string]$signature.SignerCertificate.Subject
    # The .NET installers carry the product as the common name (CN=.NET) and Microsoft as the organization.
    if (-not (Test-MicrosoftSignerSubject $subject)) {
        throw "The download is signed by '$subject', not by Microsoft Corporation."
    }
}

# Throws unless the file is signed by Microsoft Corporation, is the x64 Windows Desktop Runtime installer and carries at
# least the minimum version.
function Assert-RuntimeInstallerTrusted {
    param([string]$Path, [string]$Minimum)
    Assert-MicrosoftSigned -Path $Path
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if (-not (Test-DesktopRuntimeProductName ([string]$info.ProductName))) {
        throw "The download is '$($info.ProductName)', not the x64 Windows Desktop Runtime installer."
    }
    $fileVersion = $info.ProductVersion
    $numbers = [regex]::Match([string]$fileVersion, '^\d+\.\d+\.\d+').Value
    if (-not (Test-DesktopRuntimeSatisfied -InstalledVersions @($numbers) -Minimum $Minimum)) {
        throw "The download is version '$fileVersion', below the minimum $Minimum."
    }
}

# Downloads the address to the path and returns the final address after the redirects. The final address is checked
# before a byte is written: when it is not an https address of Microsoft, nothing is saved and the result is $null.
# The response is streamed to the file; Invoke-WebRequest in Windows PowerShell 5.1 is far too slow for 60 MB.
function Save-RuntimeDownload {
    param([string]$Url, [string]$Path)
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.AllowAutoRedirect = $true
    $request.MaximumAutomaticRedirections = 8
    $request.UserAgent = 'AppPackagerDesktopSetup'
    $request.Timeout = 60000
    $request.ReadWriteTimeout = 60000
    $response = $request.GetResponse()
    try {
        $final = $response.ResponseUri
        if (-not (Test-RuntimeDownloadUri $final)) { return $null }
        $source = $response.GetResponseStream()
        try {
            $target = [IO.File]::Create($Path)
            try { $source.CopyTo($target, 1MB) } finally { $target.Dispose() }
        }
        finally { $source.Dispose() }
        return $final
    }
    finally { $response.Dispose() }
}

function Install-DesktopRuntime {
    param([string]$Minimum, [string]$Url, [switch]$Quiet)

    $uri = [uri]$Url
    if (-not (Test-RuntimeDownloadUri $uri)) { Write-Host "The download address is not an https address of Microsoft: $Url"; return 3 }

    $folder = Join-Path ([IO.Path]::GetTempPath()) ('apd-runtime-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $file = Join-Path $folder 'windowsdesktop-runtime-win-x64.exe'
    try {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
        Write-Host "Downloading the .NET Windows Desktop Runtime from $Url"
        try {
            $final = Save-RuntimeDownload -Url $Url -Path $file
        }
        catch {
            Write-Host "The download failed: $($_.Exception.Message)"
            return 2
        }
        if ($null -eq $final) { Write-Host 'The download came from an address that is not an https address of Microsoft.'; return 3 }
        try { Assert-RuntimeInstallerTrusted -Path $file -Minimum $Minimum }
        catch { Write-Host $_.Exception.Message; return 3 }

        Write-Host 'The download is signed by Microsoft Corporation. Installing the runtime. Windows asks for consent.'
        $arguments = if ($Quiet) { @('/install', '/quiet', '/norestart') } else { @('/install', '/passive', '/norestart') }
        try { $process = Start-Process -FilePath $file -ArgumentList $arguments -Verb RunAs -Wait -PassThru }
        catch { Write-Host "The runtime installer did not start: $($_.Exception.Message)"; return 4 }

        # 0 installed, 3010 installed and a restart is asked, 1638 a newer build of the same runtime is installed.
        switch ($process.ExitCode) {
            { $_ -in 0, 1638 } { break }
            3010 { Write-Host 'The runtime is installed. Windows asks for a restart.'; return 10 }
            default { Write-Host "The runtime installer ended with exit code $($process.ExitCode)."; return 4 }
        }
        if (-not (Test-DesktopRuntimeSatisfied -InstalledVersions (Get-InstalledDesktopRuntimeVersions) -Minimum $Minimum)) {
            Write-Host 'The runtime installer ended and the runtime is still not listed.'
            return 5
        }
        return 0
    }
    finally {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# The main part runs only when the file is run, not when it is dot-sourced.
if ($MyInvocation.InvocationName -ne '.') {
    $installed = Get-InstalledDesktopRuntimeVersions
    if (Test-DesktopRuntimeSatisfied -InstalledVersions $installed -Minimum $MinimumVersion) {
        Write-Host ".NET Windows Desktop Runtime $MinimumVersion or newer is installed (x64)."
        exit 0
    }
    $found = if (@($installed).Count -gt 0) { ($installed -join ', ') } else { 'none' }
    Write-Host ".NET Windows Desktop Runtime $MinimumVersion or newer (x64) is not installed. Installed: $found."
    if ($CheckOnly) { exit 1 }
    exit (Install-DesktopRuntime -Minimum $MinimumVersion -Url $DownloadUrl -Quiet:$Quiet)
}
