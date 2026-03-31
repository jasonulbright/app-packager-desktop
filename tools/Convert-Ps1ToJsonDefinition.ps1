# Reads PS1 packager scripts (read-only) and generates JSON definitions for SRL Packr
# Output: c:\projects\packr\src\ApplicationPackagerEngine\Definitions\*.json

$sourceDir = "C:\projects\applicationpackager\Packagers"
$destDir = "C:\projects\packr\src\ApplicationPackagerEngine\Definitions"

if (-not (Test-Path $destDir)) { New-Item -Path $destDir -ItemType Directory -Force | Out-Null }

$httpRegexScripts = @(
    'package-7zip.ps1', 'package-adobereader.ps1', 'package-anaconda.ps1', 'package-anydesk.ps1',
    'package-aspnethostingbundle8.ps1', 'package-ccleaner.ps1', 'package-citrixworkspacecr.ps1',
    'package-citrixworkspaceltsr.ps1', 'package-cpuz.ps1', 'package-cutepdfwriter.ps1',
    'package-dotnet8.ps1', 'package-Dotnet9x64.ps1', 'package-Dotnet10x64.ps1',
    'package-edge.ps1', 'package-everything.ps1', 'package-filezilla.ps1', 'package-firefox.ps1',
    'package-foxitreader.ps1', 'package-gimp.ps1', 'package-inkscape.ps1', 'package-keepass.ps1',
    'package-libreoffice.ps1', 'package-malwarebytes.ps1', 'package-msodbcsql18.ps1',
    'package-msoledb.ps1', 'package-msvcruntimes.ps1', 'package-nodejs.ps1', 'package-opera.ps1',
    'package-pgadmin4.ps1', 'package-postgresql13.ps1', 'package-postgresql14.ps1',
    'package-postgresql15.ps1', 'package-postgresql16.ps1', 'package-postgresql17.ps1',
    'package-postman.ps1', 'package-powerbidesktop.ps1', 'package-putty.ps1', 'package-pycharm.ps1',
    'package-python.ps1', 'package-r.ps1', 'package-slack.ps1', 'package-soapui.ps1',
    'package-ssms.ps1', 'package-sysinternals.ps1', 'package-tableaudesktop.ps1',
    'package-tableauprep.ps1', 'package-tableaureader.ps1', 'package-teams.ps1',
    'package-teamviewer.ps1', 'package-teamviewerhost.ps1', 'package-thunderbird.ps1',
    'package-tortoisegit.ps1', 'package-tortoisesvn.ps1', 'package-treesizefree.ps1',
    'package-vlc.ps1', 'package-vmwareworkstation.ps1', 'package-vs2026.ps1',
    'package-vs2026community.ps1', 'package-vscode.ps1', 'package-webex.ps1',
    'package-webview2.ps1', 'package-winrar.ps1', 'package-winscp.ps1', 'package-wireshark.ps1',
    'package-wiztree.ps1', 'package-xencenter.ps1', 'package-xenservervmtools.ps1', 'package-zoom.ps1'
)

function Get-ScriptMetadata {
    param([string]$Path)
    $lines = Get-Content $Path -TotalCount 200
    $meta = @{ Vendor = ''; App = ''; CMName = ''; VendorUrl = '' }
    foreach ($line in $lines) {
        if ($line -match '^\s*(?:#\s*)?Vendor\s*:\s*(.+?)\s*$') { $meta.Vendor = $Matches[1].Trim() }
        if ($line -match '^\s*(?:#\s*)?App\s*:\s*(.+?)\s*$') { $meta.App = $Matches[1].Trim() }
        if ($line -match '^\s*(?:#\s*)?Application\s*:\s*(.+?)\s*$' -and -not $meta.App) { $meta.App = $Matches[1].Trim() }
        if ($line -match '^\s*(?:#\s*)?CMName\s*:\s*(.+?)\s*$') { $meta.CMName = $Matches[1].Trim() }
        if ($line -match '^\s*(?:#\s*)?VendorUrl\s*:\s*(.+?)\s*$') { $meta.VendorUrl = $Matches[1].Trim() }
    }
    if (-not $meta.CMName) { $meta.CMName = $meta.App }
    return $meta
}

function Get-ScriptVariables {
    param([string]$Path)
    $content = Get-Content $Path -Raw
    $vars = @{}

    # Extract common variable patterns
    if ($content -match '\$(?:DownloadUrl|MsiUrl|InstallerUrl|ExeUrl)\s*=\s*[''"](.+?)[''"]') { $vars.DownloadUrl = $Matches[1] }
    if ($content -match '\$(?:VersionUrl|VersionApiUrl|ReleasesIndexUrl|DownloadPageUrl)\s*=\s*[''"](.+?)[''"]') { $vars.VersionUrl = $Matches[1] }
    if ($content -match '\$Architecture\s*=\s*[''"](.+?)[''"]') { $vars.Architecture = $Matches[1] }

    # Detection info
    if ($content -match '\$DetectionExe\s*=\s*[''"](.+?)[''"]') { $vars.DetectionExe = $Matches[1] }
    if ($content -match '\$DetectionPath\s*=\s*[''"](.+?)[''"]') { $vars.DetectionPath = $Matches[1] }
    if ($content -match '\$ProductId\s*=\s*[''"](.+?)[''"]') { $vars.ProductId = $Matches[1] }

    # Installer type
    if ($content -match 'New-MsiWrapperContent|\.msi[''"]|msiexec') { $vars.InstallerType = 'MSI' }
    elseif ($content -match 'New-ExeWrapperContent|\.exe[''"]') { $vars.InstallerType = 'EXE' }
    else { $vars.InstallerType = 'Unknown' }

    # Install/uninstall args for EXE
    if ($content -match "InstallArgs\s*=\s*['""](.+?)['""]") { $vars.InstallArgs = $Matches[1] }
    if ($content -match "UninstallCommand\s*=\s*['""](.+?)['""]") { $vars.UninstallCommand = $Matches[1] }
    if ($content -match "UninstallArgs\s*=\s*['""](.+?)['""]") { $vars.UninstallArgs = $Matches[1] }

    return $vars
}

$count = 0
$skipped = @()

foreach ($script in $httpRegexScripts) {
    $path = Join-Path $sourceDir $script
    if (-not (Test-Path $path)) { $skipped += $script; continue }

    $meta = Get-ScriptMetadata -Path $path
    $vars = Get-ScriptVariables -Path $path

    if (-not $meta.App) { $skipped += "$script (no App metadata)"; continue }

    # Generate UID
    $uid = [guid]::NewGuid().ToString('N').Substring(0, 16)

    # Determine architecture
    $arch = 'x64'
    if ($meta.App -match '\(x86\)' -or $script -match 'x86') { $arch = 'x86' }

    # Build base definition
    $def = [ordered]@{
        Schema = 1
        Id = $uid
        Vendor = $meta.Vendor
        Application = $meta.App
        CMName = $meta.CMName
        Architecture = $arch
        VendorUrl = $meta.VendorUrl
        InstallerType = $vars.InstallerType
        Acquisition = [ordered]@{}
        VersionScraper = [ordered]@{
            Type = 'HttpRegex'
            Url = if ($vars.VersionUrl) { $vars.VersionUrl } else { '# TODO: extract version URL from PS1' }
            Pattern = '# TODO: extract regex pattern from PS1'
        }
        DownloadUrl = if ($vars.DownloadUrl) { $vars.DownloadUrl } else { '# TODO: extract download URL from PS1' }
        InstallArgs = ''
        UninstallArgs = ''
        Detection = [ordered]@{
            Type = 'RegistryKeyValue'
            RegistryHive = 'HKLM'
            RegistryKeyTemplate = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}'
            ValueName = 'DisplayVersion'
            Operator = 'IsEquals'
            Is64Bit = ($arch -eq 'x64')
        }
        RunningProcess = @()
    }

    # Adjust for file detection
    if ($vars.DetectionExe) {
        $def.Detection = [ordered]@{
            Type = 'File'
            FilePath = if ($vars.DetectionPath) { $vars.DetectionPath } else { '# TODO' }
            FileName = $vars.DetectionExe
            PropertyType = 'Existence'
            Is64Bit = ($arch -eq 'x64')
        }
    }

    # MSI-specific
    if ($vars.InstallerType -eq 'MSI') {
        $def.InstallArgs = '/qn /norestart'
        $def.UninstallArgs = '/x {ProductCode} /qn /norestart'
    }
    elseif ($vars.InstallArgs) {
        $def.InstallArgs = $vars.InstallArgs
        $def.UninstallArgs = $vars.UninstallArgs
    }

    # Output filename
    $baseName = $script -replace '^package-', '' -replace '\.ps1$', ''
    $jsonPath = Join-Path $destDir "$baseName.json"

    $def | ConvertTo-Json -Depth 5 | Set-Content $jsonPath -Encoding UTF8
    $count++
    Write-Host "  $baseName.json ($($meta.App))"
}

Write-Host ""
Write-Host "Generated: $count definitions"
if ($skipped.Count -gt 0) {
    Write-Host "Skipped: $($skipped.Count)"
    $skipped | ForEach-Object { Write-Host "  $_" }
}
