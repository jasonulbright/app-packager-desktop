<#
.SYNOPSIS
    Regenerates the NSIS and Inno Setup fixture files the engine tests read.

.DESCRIPTION
    Synthetic fixtures are structurally valid installers built byte by byte:
    an MZ stub, the NSIS firstheader and header block (stored, deflate, or
    solid deflate), or the Inno Setup loader table, data-version signature,
    encryption header (6.5+), block header and a CRC-chunked TSetupHeader
    record laid out per data version. The shapes and escape encodings match
    what makensis and the Inno Setup compiler emit.

    Real NSIS builds come from makensis when it is installed
    (C:\Program Files (x86)\NSIS); they cover the LZMA and bzip2 paths the
    synthetic builder does not produce. Run this only to add or change a
    fixture; the .bin files are committed.
#>
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $OutputDirectory) { $OutputDirectory = $PSScriptRoot }

Add-Type -TypeDefinition @'
using System;
public static class FixtureCrc32
{
    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    public static uint Compute(byte[] data, int offset, int length)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = offset; i < offset + length; i++) c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
'@

function New-NsisFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateSet('none', 'deflate', 'deflate-solid')][string]$Compression = 'none',
        [bool]$Unicode = $true,
        [string]$InstallDirPrefix = 'LOCALAPPDATA',
        [string]$ArpRoot = 'HKCU',
        [bool]$SetRegView64 = $false,
        [bool]$SetShellVarContextAll = $false,
        [bool]$Indirect = $false,
        [bool]$MultiMode = $false,
        [bool]$RegistryExtras = $false,
        [bool]$KeyViaVariable = $false,
        [bool]$VariantKeys = $false,
        [bool]$RegViewInInit = $false
    )
    $shell = @{ LOCALAPPDATA = 0x231C; APPDATA = 0x231A; PROGRAMFILES = 0x2081; PROGRAMFILES64 = 0x31C1 }
    $charSize = if ($Unicode) { 2 } else { 1 }
    $table = New-Object System.Collections.Generic.List[byte]
    $addUnit = {
        param($u)
        if ($Unicode) { $table.Add([byte]($u -band 0xFF)); $table.Add([byte](($u -shr 8) -band 0xFF)) }
        else { $table.Add([byte]$u) }
    }
    $addString = {
        param([object[]]$units)
        $ptr = [int]($table.Count / $charSize)
        foreach ($u in $units) { & $addUnit $u }
        & $addUnit 0
        return $ptr
    }
    $lit = { param([string]$s) ,@($s.ToCharArray() | ForEach-Object { [int]$_ }) }
    $shellRef = {
        param([string]$name)
        $v = $shell[$name]
        if ($Unicode) { ,@(2, $v) } else { ,@(2, ($v -band 0xFF), (($v -shr 8) -band 0xFF)) }
    }
    $varRef = {
        param([int]$index)
        $lo = 0x80 -bor ($index -band 0x7F); $hi = 0x80 -bor (($index -shr 7) -band 0x7F)
        if ($Unicode) { ,@(3, ($lo -bor ($hi -shl 8))) } else { ,@(3, $lo, $hi) }
    }
    & $addUnit 0
    $null = & $addString (& $lit 'ProgramFilesDir')
    $null = & $addString (& $lit 'CommonFilesDir')
    $null = & $addString (& $lit 'C:\Program Files')
    $sInstallDir = & $addString ((& $shellRef $InstallDirPrefix) + (& $lit '\TestApp'))
    $sUninst     = & $addString ((& $varRef 21) + (& $lit '\Uninstall.exe'))
    $sSubkey     = & $addString (& $lit 'Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp')
    $sDisplayName = & $addString (& $lit 'DisplayName')
    $sAppName    = & $addString (& $lit 'Test App')
    $sDisplayVer = & $addString (& $lit 'DisplayVersion')
    $sVersion    = & $addString (& $lit '1.2.3')
    $sPublisher  = & $addString (& $lit 'Publisher')
    $sVendor     = & $addString (& $lit 'Test Co')
    $sUninstStr  = & $addString (& $lit 'UninstallString')
    $sUninstVal  = & $addString ((& $lit '"') + (& $varRef 21) + (& $lit '\Uninstall.exe"'))
    $sBase       = & $addString ((& $shellRef $InstallDirPrefix) + (& $lit '\Programs'))
    $sFromVar0   = & $addString ((& $varRef 0) + (& $lit '\TestApp'))
    $sUninstVar  = & $addString ((& $varRef 21) + (& $lit '\Uninstall Test App.exe'))
    $sVar2       = & $addString (& $varRef 2)
    $sVar2Quoted = & $addString ((& $lit '"') + (& $varRef 2) + (& $lit '" /currentuser'))
    $sOne        = & $addString (& $lit '1')
    $sVal256     = & $addString (& $lit '256')
    $sBranding   = & $addString (& $lit 'Test Branding')
    $sCaption    = & $addString (& $lit 'Test App 1.2.3')
    $sName       = & $addString (& $lit 'Test App')
    $sMachineDir = & $addString ((& $shellRef 'PROGRAMFILES64') + (& $lit '\TestApp'))
    $sMain       = & $addString (& $lit 'Main')
    $sSubkey2    = & $addString (& $lit 'Software\TestCo\TestApp')
    $sFlagName   = & $addString (& $lit 'Installed')
    if ($MultiMode) {
        $null = & $addString (& $lit '/allusers')
        $null = & $addString (& $lit '/currentuser')
    }
    $sSubkeyVar = & $addString (& $varRef 0)
    $sSubkeyVariant = & $addString ((& $lit 'Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp') + (& $varRef 5))
    $strings = $table.ToArray()

    $root = switch ($ArpRoot) { 'HKLM' { -2147483646 } 'HKLM64' { -1610612734 } 'SHCTX' { 0 } default { -2147483647 } }
    $sSubkeyLiteral = $sSubkey
    if ($KeyViaVariable) { $sSubkey = $sSubkeyVar }
    $entries = New-Object System.Collections.Generic.List[int[]]
    if ($SetShellVarContextAll) { $entries.Add(@(13, 1, $sOne, 0, 0, 0, 0)) }
    if ($SetRegView64) { $entries.Add(@(13, 12, $sVal256, 0, 0, 0, 0)) }
    if ($Indirect) {
        $entries.Add(@(25, 0, $sBase, 0, 0, 0, 0))
        $entries.Add(@(25, 21, $sFromVar0, 0, 0, 0, 0))
        if ($MultiMode) { $entries.Add(@(25, 21, $sMachineDir, 0, 0, 0, 0)) }
        $entries.Add(@(25, 2, $sUninstVar, 0, 0, 0, 0))
        $entries.Add(@(62, $sVar2, 18, 784, 0, 0, 0))
    }
    else {
        $entries.Add(@(62, $sUninst, 18, 784, 0, 0, 0))
    }
    if ($KeyViaVariable) { $entries.Add(@(25, 0, $sSubkeyLiteral, 0, 0, 0, 0)) }
    if ($VariantKeys) { $entries.Add(@(51, [int]$root, $sSubkeyVariant, $sDisplayVer, $sVersion, 1, 1)) }
    $entries.Add(@(51, [int]$root, $sSubkey, $sDisplayName, $sAppName, 1, 1))
    $entries.Add(@(51, [int]$root, $sSubkey, $sDisplayVer, $sVersion, 1, 1))
    $entries.Add(@(51, [int]$root, $sSubkey, $sPublisher, $sVendor, 1, 1))
    $entries.Add(@(51, [int]$root, $sSubkey, $sUninstStr, $(if ($Indirect) { $sVar2Quoted } else { $sUninstVal }), 1, 1))
    if ($RegistryExtras) {
        $entries.Add(@(51, -1610612734, $sSubkey2, $sFlagName, $sOne, 4, 4))
        $entries.Add(@(50, 0, [int]$root, $sSubkey, $sDisplayName, 0, 0))
        $entries.Add(@(50, 0, [int]$root, $sSubkey2, 0, 3, 0))
    }
    $entries.Add(@(1, 0, 0, 0, 0, 0, 0))
    if ($RegViewInInit) { $entries.Add(@(13, 12, $sVal256, 0, 0, 0, 0)); $entries.Add(@(1, 0, 0, 0, 0, 0, 0)) }

    $sectionsOffset = 300
    $sectionCount = if ($RegistryExtras) { 1 } else { 0 }
    $entriesOffset = $sectionsOffset + ($sectionCount * 32)
    $stringsOffset = $entriesOffset + ($entries.Count * 28)
    $langOffset = $stringsOffset + $strings.Length
    $langTable = New-Object System.Collections.Generic.List[byte]
    $langTable.AddRange([byte[]](0x09, 0x04))
    $langTable.AddRange([BitConverter]::GetBytes([int]0))
    $langTable.AddRange([BitConverter]::GetBytes([int]0))
    foreach ($ptr in @($sBranding, $sCaption, $sName)) { $langTable.AddRange([BitConverter]::GetBytes([int]$ptr)) }
    $headerLength = $langOffset + $langTable.Count

    $header = New-Object byte[] $headerLength
    $put = { param($offset, $value) [Array]::Copy([BitConverter]::GetBytes([int]$value), 0, $header, $offset, 4) }
    & $put 0 0
    $blocks = @(@(0, 0), @($(if ($sectionCount) { $sectionsOffset } else { 0 }), $sectionCount), @($entriesOffset, $entries.Count), @($stringsOffset, 0), @($langOffset, 1), @($headerLength, 0), @(0, 0), @(0, 0))
    for ($b = 0; $b -lt 8; $b++) { & $put (4 + $b * 8) $blocks[$b][0]; & $put (8 + $b * 8) $blocks[$b][1] }
    & $put 68 0; & $put 72 0; & $put 76 0
    & $put 100 $langTable.Count
    & $put 280 $(if ($Indirect) { 0 } else { $sInstallDir })
    & $put 284 0
    & $put 288 -1; & $put 292 -1; & $put 296 0
    if ($sectionCount) {
        & $put $sectionsOffset $sMain; & $put ($sectionsOffset + 4) 0; & $put ($sectionsOffset + 8) 1
        & $put ($sectionsOffset + 12) 0; & $put ($sectionsOffset + 16) ($entries.Count - 1); & $put ($sectionsOffset + 20) 0
    }
    for ($e = 0; $e -lt $entries.Count; $e++) {
        for ($k = 0; $k -lt 7; $k++) { & $put ($entriesOffset + $e * 28 + $k * 4) $entries[$e][$k] }
    }
    [Array]::Copy($strings, 0, $header, $stringsOffset, $strings.Length)
    [Array]::Copy($langTable.ToArray(), 0, $header, $langOffset, $langTable.Count)

    $deflate = {
        param([byte[]]$data)
        $ms = New-Object System.IO.MemoryStream
        $ds = New-Object System.IO.Compression.DeflateStream ($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
        $ds.Write($data, 0, $data.Length); $ds.Dispose()
        return ,[byte[]]$ms.ToArray()
    }
    $data = New-Object System.Collections.Generic.List[byte]
    switch ($Compression) {
        'none' {
            $data.AddRange([BitConverter]::GetBytes([uint32]$headerLength))
            $data.AddRange([byte[]]$header)
        }
        'deflate' {
            [byte[]]$packed = & $deflate $header
            $data.AddRange([BitConverter]::GetBytes([uint32]([uint32]$packed.Length + [uint32]2147483648)))
            $data.AddRange($packed)
        }
        'deflate-solid' {
            $stream = New-Object System.Collections.Generic.List[byte]
            $stream.AddRange([BitConverter]::GetBytes([uint32]$headerLength))
            $stream.AddRange([byte[]]$header)
            $stream.AddRange([BitConverter]::GetBytes([uint32]4))
            $stream.AddRange([byte[]](1, 2, 3, 4))
            [byte[]]$packedStream = & $deflate $stream.ToArray()
            $data.AddRange($packedStream)
        }
    }

    $file = New-Object System.Collections.Generic.List[byte]
    $stub = New-Object byte[] 1024
    $stub[0] = 0x4D; $stub[1] = 0x5A
    $file.AddRange($stub)
    $file.AddRange([BitConverter]::GetBytes([uint32]0))
    $file.AddRange([byte[]](0xEF, 0xBE, 0xAD, 0xDE))
    $file.AddRange([System.Text.Encoding]::ASCII.GetBytes('NullsoftInst'))
    $file.AddRange([BitConverter]::GetBytes([uint32]$headerLength))
    $file.AddRange([BitConverter]::GetBytes([uint32](28 + $data.Count)))
    $file.AddRange($data)
    [System.IO.File]::WriteAllBytes($Path, $file.ToArray())
}

function New-InnoFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$DataVersion = '6.7.0',
        [string]$AppId = 'Test App',
        [string]$AppName = 'Test App',
        [string]$AppVerName = '',
        [string]$AppVersion = '1.2.3',
        [string]$Publisher = 'Test Co',
        [string]$DefaultDirName = '{autopf}\Test App',
        [string]$UninstallFilesDir = '',
        [string]$UninstallDisplayName = '',
        [string]$Uninstallable = 'yes',
        [int]$Privileges = 2,
        [int]$Overrides = 0,
        [string]$Arch64Expression = 'x64compatible',
        [int]$Arch64Flags = 4,
        [int]$EncryptionUse = 0,
        [switch]$CorruptBlockCrc
    )

    $m = [regex]::Match($DataVersion, '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?<u>\s*\(u\))?$')
    $v = [int64]$m.Groups[1].Value * 1000000000 + [int64]$m.Groups[2].Value * 1000000 + [int64]$m.Groups[3].Value * 1000 + $(if ($m.Groups[4].Success) { [int]$m.Groups[4].Value } else { 0 })
    $unicode = ($v -ge 6003000000) -or $m.Groups['u'].Success
    $enc = if ($unicode) { [System.Text.Encoding]::Unicode } else { [System.Text.Encoding]::Default }
    $VER = { param($a, $b, $c, $d = 0) [int64]$a * 1000000000 + [int64]$b * 1000000 + [int64]$c * 1000 + $d }

    $rec = New-Object System.Collections.Generic.List[byte]
    $addBytes = { param($bytes) foreach ($b in $bytes) { $rec.Add([byte]$b) } }
    $add32 = { param([int64]$n) & $addBytes ([BitConverter]::GetBytes([int32]$n)) }
    $addStr = { param([string]$s, [switch]$Ansi) [byte[]]$bytes = @(if ($Ansi) { [System.Text.Encoding]::Default.GetBytes($s) } else { $enc.GetBytes($s) }); & $add32 $bytes.Length; & $addBytes $bytes }
    $zeros = { param($n) & $addBytes (New-Object byte[] $n) }

    & $addStr $AppName
    & $addStr $AppVerName
    & $addStr $AppId
    & $addStr 'Copyright'
    & $addStr $Publisher
    & $addStr 'https://example.com/'
    if ($v -ge (& $VER 5 1 13)) { & $addStr '' }
    & $addStr 'https://example.com/support'
    & $addStr 'https://example.com/updates'
    & $addStr $AppVersion
    & $addStr $DefaultDirName
    & $addStr 'Test App'
    & $addStr 'setup'
    & $addStr $UninstallFilesDir
    & $addStr $UninstallDisplayName
    & $addStr ''
    & $addStr ''
    & $addStr ''
    & $addStr ''
    & $addStr ''
    & $addStr ''; & $addStr ''; & $addStr ''; & $addStr ''
    if ($v -ge (& $VER 5 3 8))  { & $addStr 'yes' }
    if ($v -ge (& $VER 5 3 10)) { & $addStr $Uninstallable }
    if ($v -ge (& $VER 5 5 0))  { & $addStr '' }
    if ($v -ge (& $VER 5 5 6))  { & $addStr '' }
    if ($v -ge (& $VER 5 6 1))  { & $addStr 'no'; & $addStr 'no' }
    if ($v -ge (& $VER 6 3 0))  { & $addStr 'x64compatible'; & $addStr $Arch64Expression }
    if ($v -ge (& $VER 6 4 2))  { & $addStr '' }
    if ($v -ge (& $VER 6 5 0))  { & $addStr '' }
    if ($v -ge (& $VER 6 7 0))  { & $addStr 'yes'; & $addStr 'yes'; & $addStr 'yes'; & $addStr 'yes'; & $addStr 'yes' }
    & $addStr 'LICENSE TEXT' -Ansi
    & $addStr '' -Ansi
    & $addStr '' -Ansi
    & $addStr 'COMPILED' -Ansi

    if (-not $unicode) { & $zeros 32 }
    $countCount = if ($v -ge (& $VER 6 5 0)) { 17 } else { 16 }
    & $add32 1
    for ($i = 1; $i -lt $countCount; $i++) { & $add32 0 }
    if ($v -ge (& $VER 7 0 0)) { & $add32 0 }
    & $addBytes @(0, 0, 0, 0, 0, 0, 1, 6, 0, 0)
    & $zeros 10
    if ($v -lt (& $VER 6 4 0 1)) { & $zeros 8 }
    if ($v -lt (& $VER 5 5 7)) { & $zeros 4 }
    if ($v -ge (& $VER 6 0 0) -and $v -lt (& $VER 6 6 0)) { & $addBytes @(1) }
    if ($v -ge (& $VER 6 0 0)) { & $zeros 8 }
    if ($v -ge (& $VER 6 6 0)) { & $addBytes @(2) }
    if ($v -ge (& $VER 5 5 7)) { & $addBytes @(1) }
    if ($v -ge (& $VER 6 5 2) -and $v -lt (& $VER 6 6 0)) { & $zeros 8 }
    if ($v -ge (& $VER 6 6 0) -and $v -lt (& $VER 6 7 0)) { & $zeros 16 }
    if ($v -ge (& $VER 6 6 1) -and $v -lt (& $VER 6 7 0)) { & $zeros 1 }
    if ($v -ge (& $VER 6 7 0)) { & $zeros 26; & $addBytes @(0) }
    if ($v -ge (& $VER 6 5 0)) { }
    elseif ($v -ge (& $VER 6 4 0)) { & $zeros 48 }
    elseif ($v -ge (& $VER 5 3 9)) { & $zeros 28 }
    else { & $zeros 24 }
    & $zeros 8
    & $add32 1
    & $addBytes @(1, 0, $Privileges)
    if ($v -ge (& $VER 5 7 0)) { & $addBytes @($Overrides) }
    & $addBytes @(2, 0, 3)
    if ($v -ge (& $VER 5 1 0) -and $v -lt (& $VER 6 3 0)) { & $addBytes @(6, $Arch64Flags) }
    if ($v -ge (& $VER 5 2 1) -and $v -lt (& $VER 5 3 10)) { & $zeros 8 }
    if ($v -ge (& $VER 5 3 3)) { & $addBytes @(0, 0) }
    & $zeros 8
    & $zeros $(if ($v -ge (& $VER 6 7 0)) { 8 } elseif ($v -ge (& $VER 6 4 0 1)) { 6 } elseif ($v -ge (& $VER 6 3 0)) { 7 } else { 6 })
    if ($v -ge (& $VER 6 0 0)) {
        $langStrings = if ($v -ge (& $VER 6 6 0)) { 4 } else { 6 }
        for ($i = 0; $i -lt $langStrings; $i++) { & $addStr 'default' }
        for ($i = 0; $i -lt 4; $i++) { & $addStr '' -Ansi }
        & $zeros $(if ($v -ge (& $VER 6 6 0)) { 19 } else { 21 })
    }
    $record = $rec.ToArray()

    $crc = { param($bytes, $off, $len) [FixtureCrc32]::Compute($bytes, $off, $len) }
    $out = New-Object System.Collections.Generic.List[byte]
    $put = { param($bytes) foreach ($b in $bytes) { $out.Add([byte]$b) } }

    $stub = New-Object byte[] 1024
    $stub[0] = 0x4D; $stub[1] = 0x5A
    $headerOffset = 1024
    $table = New-Object System.Collections.Generic.List[byte]
    foreach ($b in @(0x72,0x44,0x6C,0x50,0x74,0x53,0xCD,0xE6,0xD7,0x7B,0x0B,0x2A)) { $table.Add([byte]$b) }
    if ($v -ge (& $VER 6 5 0)) {
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32]2))
        $table.AddRange([byte[]][BitConverter]::GetBytes([int64]0))
        $table.AddRange([byte[]][BitConverter]::GetBytes([int64]0))
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32]0))
        $table.AddRange([byte[]][BitConverter]::GetBytes([int32]0))
        $table.AddRange([byte[]][BitConverter]::GetBytes([int64]$headerOffset))
        $table.AddRange([byte[]][BitConverter]::GetBytes([int64]0))
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32]0))
        $tb = $table.ToArray()
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32](& $crc $tb 0 60)))
    }
    else {
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32]1))
        foreach ($val in @(0, 0, 0, 0, $headerOffset, 0)) { $table.AddRange([byte[]][BitConverter]::GetBytes([uint32]$val)) }
        $tb = $table.ToArray()
        $table.AddRange([byte[]][BitConverter]::GetBytes([uint32](& $crc $tb 0 40)))
    }
    $tableBytes = $table.ToArray()
    [Array]::Copy($tableBytes, 0, $stub, 64, $tableBytes.Length)
    & $put $stub

    $id = New-Object byte[] 64
    $label = 'Inno Setup Setup Data (' + ($DataVersion -replace '\s*\(u\)$', '') + ')' + $(if ($m.Groups['u'].Success) { ' (u)' } else { '' })
    $idBytes = [System.Text.Encoding]::ASCII.GetBytes($label)
    [Array]::Copy($idBytes, 0, $id, 0, $idBytes.Length)
    & $put $id

    if ($v -ge (& $VER 6 5 0)) {
        $encHeader = New-Object byte[] 49
        $encHeader[0] = [byte]$EncryptionUse
        & $put ([BitConverter]::GetBytes([uint32](& $crc $encHeader 0 49)))
        & $put $encHeader
    }

    $chunks = New-Object System.Collections.Generic.List[byte]
    $pos = 0
    while ($pos -lt $record.Length) {
        $len = [Math]::Min(4096, $record.Length - $pos)
        $chunks.AddRange([byte[]][BitConverter]::GetBytes([uint32](& $crc $record $pos $len)))
        for ($i = 0; $i -lt $len; $i++) { $chunks.Add($record[$pos + $i]) }
        $pos += $len
    }
    $stored = $chunks.ToArray()
    $bh = New-Object System.Collections.Generic.List[byte]
    if ($v -ge (& $VER 6 7 0)) { $bh.AddRange([byte[]][BitConverter]::GetBytes([int64]$stored.Length)) }
    else { $bh.AddRange([byte[]][BitConverter]::GetBytes([uint32]$stored.Length)) }
    $bh.Add([byte]0)
    $bhBytes = $bh.ToArray()
    $bhCrc = & $crc $bhBytes 0 $bhBytes.Length
    if ($CorruptBlockCrc) { $bhCrc = $bhCrc -bxor 0xFFFFFFFF }
    & $put ([BitConverter]::GetBytes([uint32]$bhCrc))
    & $put $bhBytes
    & $put $stored
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
}

$out = $OutputDirectory
New-Item -ItemType Directory -Force -Path $out | Out-Null
$p = { param($name) Join-Path $out $name }

# NSIS synthetic
New-NsisFixture -Path (& $p 'nsis-u-none.bin')
New-NsisFixture -Path (& $p 'nsis-a-pf64-hklm-view64.bin') -Unicode $false -InstallDirPrefix 'PROGRAMFILES64' -ArpRoot 'HKLM' -SetRegView64 $true
New-NsisFixture -Path (& $p 'nsis-u-pf-hklm-wow.bin') -InstallDirPrefix 'PROGRAMFILES' -ArpRoot 'HKLM'
New-NsisFixture -Path (& $p 'nsis-u-shellvar-all.bin') -SetShellVarContextAll $true
New-NsisFixture -Path (& $p 'nsis-u-deflate.bin') -Compression 'deflate'
New-NsisFixture -Path (& $p 'nsis-u-deflate-solid.bin') -Compression 'deflate-solid'
New-NsisFixture -Path (& $p 'nsis-u-indirect.bin') -Indirect $true
New-NsisFixture -Path (& $p 'nsis-u-multimode.bin') -Indirect $true -MultiMode $true -ArpRoot 'SHCTX' -SetRegView64 $true
New-NsisFixture -Path (& $p 'nsis-u-single.bin') -Indirect $true -MultiMode $false
New-NsisFixture -Path (& $p 'nsis-key-var.bin') -ArpRoot 'HKLM' -SetRegView64 $true -KeyViaVariable $true
New-NsisFixture -Path (& $p 'nsis-key-variant.bin') -ArpRoot 'HKLM' -SetRegView64 $true -VariantKeys $true
New-NsisFixture -Path (& $p 'nsis-key-root64.bin') -ArpRoot 'HKLM64'
New-NsisFixture -Path (& $p 'nsis-key-init.bin') -ArpRoot 'HKLM' -RegViewInInit $true
New-NsisFixture -Path (& $p 'nsis-key-shctx-pf.bin') -ArpRoot 'SHCTX' -SetShellVarContextAll $true -SetRegView64 $true -InstallDirPrefix 'PROGRAMFILES64'
New-NsisFixture -Path (& $p 'nsis-reg-extras.bin') -ArpRoot 'HKCU' -RegistryExtras $true

$plain = New-Object byte[] 2048; $plain[0] = 0x4D; $plain[1] = 0x5A
[System.IO.File]::WriteAllBytes((& $p 'not-an-installer.bin'), $plain)
$markerInStub = New-Object byte[] 4096; $markerInStub[0] = 0x4D; $markerInStub[1] = 0x5A
$marker = [byte[]](0xEF, 0xBE, 0xAD, 0xDE) + [System.Text.Encoding]::ASCII.GetBytes('NullsoftInst')
[Array]::Copy($marker, 0, $markerInStub, 100, $marker.Length)
[System.IO.File]::WriteAllBytes((& $p 'nsis-marker-in-stub.bin'), $markerInStub)

# Inno synthetic
New-InnoFixture -Path (& $p 'inno-admin-670.bin')
New-InnoFixture -Path (& $p 'inno-lowest-670.bin') -Privileges 3 -Overrides 3 -AppId '{{771FD6B0-FA20-440A-A002-3B3BAC16DC50}' -DefaultDirName '{autopf}\Code'
New-InnoFixture -Path (& $p 'inno-lowest-modes.bin') -Privileges 3 -Overrides 1 -AppId 'Tree' -DefaultDirName '{autopf}\Tree'
New-InnoFixture -Path (& $p 'inno-admin-modes.bin') -Privileges 2 -Overrides 3 -AppId 'winscp3' -DefaultDirName '{autopf}\WinSCP' -Arch64Expression ''
New-InnoFixture -Path (& $p 'inno-dialog-only.bin') -Privileges 2 -Overrides 2
New-InnoFixture -Path (& $p 'inno-admin-modes-x64.bin') -Privileges 2 -Overrides 1 -AppId 'Tree64' -DefaultDirName '{autopf}\Tree'
New-InnoFixture -Path (& $p 'inno-x86-6401.bin') -DataVersion '6.4.0.1' -Arch64Expression '' -AppId 'winscp3' -DefaultDirName '{autopf}\WinSCP'
New-InnoFixture -Path (& $p 'inno-pu-650.bin') -DataVersion '6.5.0' -Privileges 1
New-InnoFixture -Path (& $p 'inno-enc-650.bin') -DataVersion '6.5.0' -EncryptionUse 2
New-InnoFixture -Path (& $p 'inno-admin-661.bin') -DataVersion '6.6.1' -AppId 'Ultravnc2'
New-InnoFixture -Path (& $p 'inno-admin-7003.bin') -DataVersion '7.0.0.3'
New-InnoFixture -Path (& $p 'inno-u-557.bin') -DataVersion '5.5.7 (u)' -AppId '{{37771A20-7167-44C0-B322-FD3E54C56156}' -Arch64Flags 4
New-InnoFixture -Path (& $p 'inno-a-557.bin') -DataVersion '5.5.7' -Arch64Flags 0 -AppId 'AnsiApp'
New-InnoFixture -Path (& $p 'inno-code-610.bin') -DataVersion '6.1.0 (u)' -DefaultDirName '{code:UserPF}\R\R-4.6.0' -AppId 'R for Windows 4.6.0' -Privileges 0 -Overrides 1
New-InnoFixture -Path (& $p 'inno-names.bin') -AppVerName 'Test App 1.2.3' -UninstallDisplayName 'Test App (User)'
New-InnoFixture -Path (& $p 'inno-names2.bin') -AppVerName 'Test App 1.2.3'
New-InnoFixture -Path (& $p 'inno-ufd.bin') -UninstallFilesDir '{app}\uninst'
New-InnoFixture -Path (& $p 'inno-badcrc.bin') -CorruptBlockCrc
New-InnoFixture -Path (& $p 'inno-no-unins.bin') -Uninstallable 'no'
foreach ($ver in '6.0.0', '6.2.2', '6.3.3', '6.4.3', '6.5.4', '6.6.0') {
    New-InnoFixture -Path (& $p ('inno-admin-' + ($ver -replace '\.', '') + '.bin')) -DataVersion $ver
}

# NSIS real builds
$makensis = 'C:\Program Files (x86)\NSIS\makensis.exe'
if (Test-Path -LiteralPath $makensis) {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ('nsis-fixtures-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    Set-Content -LiteralPath (Join-Path $work 'payload.txt') -Value 'payload'
    $nsi = Join-Path $work 'probe.nsi'
    @'
Unicode ${UNI}
SetCompressor ${SOLIDFLAG} ${COMP}
RequestExecutionLevel user
Name "Probe App"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\ProbeApp"
Section "Main"
  SetOutPath "$INSTDIR"
  File "payload.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProbeApp" "DisplayName" "Probe App"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProbeApp" "DisplayVersion" "1.2.3"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProbeApp" "UninstallString" '"$INSTDIR\Uninstall.exe"'
SectionEnd
Section "Uninstall"
  Delete "$INSTDIR\payload.txt"
SectionEnd
'@ | Set-Content -LiteralPath $nsi -Encoding ASCII
    $builds = @(
        @{ Name = 'nsis-mk-lzma-solid-unicode.bin'; Uni = 'true';  Comp = 'lzma';  Solid = '/SOLID' },
        @{ Name = 'nsis-mk-lzma-ansi.bin';          Uni = 'false'; Comp = 'lzma';  Solid = '' },
        @{ Name = 'nsis-mk-bzip2-solid.bin';        Uni = 'true';  Comp = 'bzip2'; Solid = '/SOLID' },
        @{ Name = 'nsis-mk-zlib-solid.bin';         Uni = 'true';  Comp = 'zlib';  Solid = '/SOLID' }
    )
    foreach ($b in $builds) {
        $target = Join-Path $work ($b.Name -replace '\.bin$', '.exe')
        $argList = @('/V1', "/DUNI=$($b.Uni)", "/DCOMP=$($b.Comp)", "/DSOLIDFLAG=$($b.Solid)", "/DOUTFILE=$target", $nsi)
        $null = & $makensis @argList 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target)) { throw "makensis failed for $($b.Name)" }
        Copy-Item -LiteralPath $target -Destination (& $p $b.Name) -Force
    }
    Remove-Item -LiteralPath $work -Recurse -Force
}
else {
    Write-Warning 'makensis not found; the real NSIS builds were not regenerated.'
}

Get-ChildItem -LiteralPath $out -Filter *.bin | ForEach-Object { '{0,8}  {1}' -f $_.Length, $_.Name }
