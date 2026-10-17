Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function ConvertTo-DiscoveryJsonString {
    # ASCII-only output: the parent decodes standard output in the console code page.
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return '""' }
    $text = [string]$Value
    if ($text -cmatch '^[ !#-\[\]-~]*$') { return '"' + $text + '"' }
    $sb = New-Object System.Text.StringBuilder ($text.Length + 8)
    [void]$sb.Append('"')
    foreach ($ch in $text.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq [char]'"') { [void]$sb.Append('\"') }
        elseif ($ch -eq [char]'\') { [void]$sb.Append('\\') }
        elseif ($code -lt 32 -or $code -gt 126) { [void]$sb.Append(('\u{0:x4}' -f $code)) }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Invoke-DiscoverySiteDefinition {
    # Reads SMS_SCI_SiteDefinition over DCOM first and WinRM second. WinRM to the provider
    # requires local Administrators or Remote Management Users membership; a ConfigMgr
    # administrator without either gets "Access is denied" there and reaches the SMS
    # Provider only through DCOM. Raw CIM error text never leaves the process.
    param([string]$Provider, [string]$SiteCode)
    $line = '{"t":"error","stage":"site"}'
    foreach ($protocol in 'Dcom', 'Wsman') {
        $session = $null
        try {
            $option = New-CimSessionOption -Protocol $protocol
            $session = New-CimSession -ComputerName $Provider -SessionOption $option -OperationTimeoutSec 30 -ErrorAction Stop
            $definition = @(Get-CimInstance -CimSession $session -Namespace ('root\sms\site_' + $SiteCode) -ClassName 'SMS_SCI_SiteDefinition' -Filter ("SiteCode='" + $SiteCode + "'") -OperationTimeoutSec 30 -ErrorAction Stop) | Select-Object -First 1
            if ($null -ne $definition) {
                $line = '{"t":"site","found":true,"server":' + (ConvertTo-DiscoveryJsonString $definition.SQLServerName) + ',"database":' + (ConvertTo-DiscoveryJsonString $definition.SQLDatabaseName) + ',"via":"' + $protocol + '"}'
            }
            else {
                $line = '{"t":"site","found":false,"via":"' + $protocol + '"}'
            }
            break
        }
        catch {
            $line = '{"t":"error","stage":"site"}'
        }
        finally {
            if ($null -ne $session) { $session.Dispose() }
        }
    }
    [Console]::Out.WriteLine($line)
    [Console]::Out.WriteLine('{"t":"end"}')
}

if (Get-Variable -Name DiscoveryRun -Scope Script -ValueOnly -ErrorAction SilentlyContinue) {
    Invoke-DiscoverySiteDefinition -Provider $DiscoveryProvider -SiteCode $DiscoverySiteCode
}
