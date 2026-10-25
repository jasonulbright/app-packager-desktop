Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Raw streams with UTF-8 in both directions: the console code page of a hidden console is
# the OEM page and would change non-ASCII text in requests and responses.
$sessionEncoding = New-Object System.Text.UTF8Encoding $false
$sessionInput = New-Object System.IO.StreamReader -ArgumentList ([Console]::OpenStandardInput()), $sessionEncoding
$sessionOutput = New-Object System.IO.StreamWriter -ArgumentList ([Console]::OpenStandardOutput()), $sessionEncoding
$sessionOutput.AutoFlush = $true
$script:SessionSiteCode = $null

function Get-SessionField {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Write-SessionLine {
    param([string]$Id, [string]$Type, [string]$Body = '')
    $sessionOutput.WriteLine('{"id":"' + $Id + '","t":"' + $Type + '"' + $Body + '}')
}

function ConvertTo-SessionString {
    param([AllowNull()][object]$Value)
    return (ConvertTo-Json -InputObject ([string]$Value) -Compress)
}

# ConvertTo-Json writes NaN and Infinity as bare words, which no JSON reader accepts; the
# reader drops the whole line. The pattern matches whole strings first so text inside a
# string is never changed.
$script:NonFiniteNumber = New-Object System.Text.RegularExpressions.Regex -ArgumentList '"(?:[^"\\]|\\.)*"|-?Infinity|NaN', ([System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

function Repair-SessionJson {
    param([string]$Json)
    if ($Json -cnotmatch 'NaN|Infinity') { return $Json }
    return $script:NonFiniteNumber.Replace($Json, [System.Text.RegularExpressions.MatchEvaluator] {
        param($Match)
        if ($Match.Value.StartsWith('"')) { return $Match.Value }
        return 'null'
    })
}

function ConvertTo-SessionValue {
    param([AllowNull()][object]$Item)
    if ($null -eq $Item) { return 'null' }
    $base = $Item.PSObject.BaseObject
    if ($base -is [string] -or $base -is [char] -or $base -is [guid]) { return (ConvertTo-SessionString $base) }
    if ($base -is [bool]) { if ($base) { return 'true' } else { return 'false' } }
    if ($base -is [datetime]) { return (ConvertTo-SessionString $base.ToString('o', [Globalization.CultureInfo]::InvariantCulture)) }
    if ($base -is [enum]) { return (ConvertTo-SessionString $base.ToString()) }
    if (($base -is [double] -or $base -is [single]) -and ([double]::IsNaN($base) -or [double]::IsInfinity($base))) { return 'null' }
    if ($base -is [ValueType]) { return (ConvertTo-Json -InputObject $base -Compress) }
    try {
        $json = ConvertTo-Json -InputObject $Item -Depth 3 -Compress -WarningAction SilentlyContinue
        if ($json) { return (Repair-SessionJson ($json -replace '[\r\n]+', ' ')) }
    } catch { }
    return (ConvertTo-SessionString $Item)
}

function Get-SessionErrorKind {
    # Access denied arrives as UnauthorizedAccessException, a COM or WMI 0x80070005, or a
    # provider message; anything else is the script's own failure.
    param([object]$Failure)
    $exception = if ($Failure -is [System.Management.Automation.ErrorRecord]) { $Failure.Exception } else { $Failure }
    while ($null -ne $exception) {
        if ($exception -is [System.UnauthorizedAccessException]) { return 'access' }
        $hresult = 0
        try { $hresult = $exception.HResult } catch { }
        if ($hresult -eq -2147024891) { return 'access' }
        if ([string]$exception.Message -match 'Access is denied|Access denied|0x80070005|E_ACCESSDENIED') { return 'access' }
        $exception = $exception.InnerException
    }
    return 'script'
}

function Test-SameProviderHost {
    param([string]$Left, [string]$Right)
    $a = $Left.Trim().TrimEnd('\').ToLowerInvariant()
    $b = $Right.Trim().TrimEnd('\').ToLowerInvariant()
    if ($a -eq $b) { return $true }
    # A short name and the fully qualified name of the same host.
    if ($a.Contains('.') -eq $b.Contains('.')) { return $false }
    return (($a -split '\.')[0] -eq ($b -split '\.')[0])
}

function Invoke-SessionConnect {
    param([string]$Id, [object]$Request)
    $siteCode = [string](Get-SessionField $Request 'siteCode')
    $provider = ([string](Get-SessionField $Request 'provider')).Trim()
    $modulePath = [string](Get-SessionField $Request 'modulePath')
    if (-not $provider) { $provider = $env:COMPUTERNAME }

    if (-not (Get-Command -Name Get-CMSite -ErrorAction SilentlyContinue)) {
        try {
            if ($modulePath -and (Test-Path -LiteralPath $modulePath)) {
                Import-Module -Name $modulePath -Global -ErrorAction Stop
            } elseif ($env:SMS_ADMIN_UI_PATH -and (Test-Path -LiteralPath (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'ConfigurationManager.psd1'))) {
                Import-Module -Name (Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'ConfigurationManager.psd1') -Global -ErrorAction Stop
            } else {
                Import-Module -Name ConfigurationManager -Global -ErrorAction Stop
            }
        } catch {
            Write-SessionLine $Id 'error' ',"kind":"module"'
            return
        }
        if (-not (Get-Command -Name Get-CMSite -ErrorAction SilentlyContinue)) {
            Write-SessionLine $Id 'error' ',"kind":"module"'
            return
        }
    }

    try {
        $drive = Get-PSDrive -Name $siteCode -PSProvider CMSite -ErrorAction SilentlyContinue
        if ($drive -and -not (Test-SameProviderHost -Left ([string]$drive.Root) -Right $provider)) {
            Remove-PSDrive -Name $siteCode -Force -ErrorAction Stop
            $drive = $null
        }
        if (-not $drive) {
            New-PSDrive -Name $siteCode -PSProvider CMSite -Root $provider -Scope Global -ErrorAction Stop | Out-Null
        }
        Set-Location -Path ($siteCode + ':') -ErrorAction Stop
        $site = @(Get-CMSite -SiteCode $siteCode -ErrorAction Stop)
        if ($site.Count -eq 0) {
            Set-Location -Path $env:SystemRoot
            Write-SessionLine $Id 'error' ',"kind":"drive"'
            return
        }
    } catch {
        $kind = Get-SessionErrorKind $_
        if ($kind -ne 'access') { $kind = 'drive' }
        try { Set-Location -Path $env:SystemRoot } catch { }
        Write-SessionLine $Id 'error' (',"kind":"' + $kind + '"')
        return
    }
    $script:SessionSiteCode = $siteCode
}

function Invoke-SessionRun {
    param([string]$Id, [object]$Request)
    $text = [string](Get-SessionField $Request 'script')
    $arguments = @{}
    $parameters = Get-SessionField $Request 'parameters'
    if ($null -ne $parameters) {
        foreach ($property in $parameters.PSObject.Properties) { $arguments[$property.Name] = $property.Value }
    }
    try {
        $block = [scriptblock]::Create($text)
        & $block @arguments 3>&1 6>$null | ForEach-Object {
            if ($_ -is [System.Management.Automation.WarningRecord]) {
                Write-SessionLine $Id 'warn' (',"m":' + (ConvertTo-SessionString $_.Message))
            } else {
                Write-SessionLine $Id 'out' (',"v":' + (ConvertTo-SessionValue $_))
            }
        }
    } catch {
        $kind = Get-SessionErrorKind $_
        Write-SessionLine $Id 'error' (',"kind":"' + $kind + '","m":' + (ConvertTo-SessionString $_.Exception.Message))
    } finally {
        if ($script:SessionSiteCode) {
            try { Set-Location -Path ($script:SessionSiteCode + ':') } catch { }
        }
    }
}

while ($true) {
    $line = $sessionInput.ReadLine()
    if ($null -eq $line) { break }
    if (-not $line.Trim()) { continue }
    try {
        $request = ConvertFrom-Json -InputObject $line
    } catch {
        # Without an end line the caller waits for its timeout; the id is taken from the
        # raw text because the request did not parse.
        $found = [regex]::Match($line, '"id"\s*:\s*"([A-Za-z0-9-]{1,64})"')
        if ($found.Success) {
            Write-SessionLine $found.Groups[1].Value 'error' ',"kind":"script","m":"The request could not be read."'
            Write-SessionLine $found.Groups[1].Value 'end'
        }
        continue
    }
    $id = [string](Get-SessionField $request 'id')
    if ($id -notmatch '^[A-Za-z0-9-]{1,64}$') { continue }
    switch ([string](Get-SessionField $request 'op')) {
        'connect' { Invoke-SessionConnect -Id $id -Request $request }
        'run' { Invoke-SessionRun -Id $id -Request $request }
        default { Write-SessionLine $id 'error' ',"kind":"script","m":"Unknown request."' }
    }
    Write-SessionLine $id 'end'
}
