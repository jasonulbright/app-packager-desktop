Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# The site database views are outside ConfigMgr role-based administration. Output carries
# distinct application names, versions and inventory views. It carries no ResourceID, no machine
# name and no client count: discovery is application discovery, not asset tracking.

$DiscoveryInventoryQuery = @'
SET NOCOUNT ON;

SELECT DISTINCT arp.DisplayName0 AS DisplayName, arp.Version0 AS Version, @Arch32 AS Architecture
FROM dbo.v_GS_ADD_REMOVE_PROGRAMS AS arp
WHERE arp.DisplayName0 IS NOT NULL AND arp.DisplayName0 <> N''
  AND arp.ResourceID IN (SELECT sys.ResourceID FROM dbo.v_R_System AS sys
                         WHERE ISNULL(sys.Obsolete0, 0) = 0 AND ISNULL(sys.Decommissioned0, 0) = 0)
UNION
SELECT DISTINCT arp64.DisplayName0, arp64.Version0, @Arch64
FROM dbo.v_GS_ADD_REMOVE_PROGRAMS_64 AS arp64
WHERE arp64.DisplayName0 IS NOT NULL AND arp64.DisplayName0 <> N''
  AND arp64.ResourceID IN (SELECT sys64.ResourceID FROM dbo.v_R_System AS sys64
                           WHERE ISNULL(sys64.Obsolete0, 0) = 0 AND ISNULL(sys64.Decommissioned0, 0) = 0);

SELECT DISTINCT app.DisplayName
FROM dbo.v_Applications AS app
JOIN dbo.v_ConfigurationItems AS ci ON ci.ModelId = app.ModelId AND ci.IsLatest = 1
WHERE app.ObjectTypeID = @AppObjectType AND ci.IsExpired = 0 AND ci.IsTombstoned = 0;
'@

function ConvertTo-DiscoveryJsonString {
    # ASCII-only output: the parent decodes standard output in the console code page.
    param([AllowNull()][object]$Value)
    if ($null -eq $Value -or $Value -is [System.DBNull]) { return '""' }
    $text = [string]$Value
    # \z, not $: $ also matches before a final line feed, which would pass a raw LF through.
    if ($text -cmatch '^[ !#-\[\]-~]*\z') { return '"' + $text + '"' }
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

function Get-DiscoveryCell {
    param($Reader, [int]$Index)
    if ($Reader.IsDBNull($Index)) { return $null }
    return $Reader.GetValue($Index)
}

function Write-DiscoveryInventory {
    # Reads the two result sets of the inventory query and writes one JSON object per
    # line. Nothing is written until every result set is read, so a failure mid-read
    # leaves no partial rows.
    param($Reader)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $rows = New-Object System.Collections.Generic.List[string]
    while ($Reader.Read()) {
        $name = [string](Get-DiscoveryCell $Reader 0)
        $version = [string](Get-DiscoveryCell $Reader 1)
        $architecture = [string](Get-DiscoveryCell $Reader 2)
        $key = $name + [char]0 + $version + [char]0 + $architecture
        if (-not $seen.Add($key)) { continue }
        $rows.Add('{"t":"row","n":' + (ConvertTo-DiscoveryJsonString $name) + ',"v":' + (ConvertTo-DiscoveryJsonString $version) +
                  ',"a":' + (ConvertTo-DiscoveryJsonString $architecture) + '}')
    }

    $applications = New-Object System.Collections.Generic.List[string]
    if ($Reader.NextResult()) {
        while ($Reader.Read()) {
            $title = Get-DiscoveryCell $Reader 0
            if ($null -ne $title -and ([string]$title).Trim()) { $applications.Add([string]$title) }
        }
    }
    else { throw (New-Object System.InvalidOperationException 'The inventory query returned fewer than two result sets.') }

    $out = [Console]::Out
    foreach ($row in $rows) { $out.WriteLine($row) }
    $out.WriteLine('{"t":"summary"}')
    foreach ($title in $applications) { $out.WriteLine('{"t":"app","n":' + (ConvertTo-DiscoveryJsonString $title) + '}') }
}

function Write-DiscoveryError {
    # Error numbers and two message flags leave the process; the raw message text does not.
    param([System.Exception]$Exception)
    $numbers = New-Object System.Collections.Generic.List[int]
    $sspi = $false
    $anonymous = $false
    $current = $Exception
    while ($null -ne $current) {
        $text = [string]$current.Message
        if ($text -match 'SSPI') { $sspi = $true }
        if ($text -match 'ANONYMOUS LOGON') { $anonymous = $true }
        if ($current -is [System.Data.SqlClient.SqlException]) {
            foreach ($e in $current.Errors) { $numbers.Add([int]$e.Number) }
            if ($numbers.Count -eq 0) { $numbers.Add([int]$current.Number) }
        }
        $current = $current.InnerException
    }
    $flag = { param($b) if ($b) { 'true' } else { 'false' } }
    [Console]::Out.WriteLine('{"t":"error","stage":"sql","numbers":[' + ($numbers -join ',') + '],"sspi":' + (& $flag $sspi) + ',"anonymous":' + (& $flag $anonymous) + '}')
}

function Invoke-DiscoveryInventory {
    param([string]$Server, [string]$Database, [int]$ConnectTimeout, [int]$CommandTimeout)
    $connection = $null
    $command = $null
    $reader = $null
    try {
        # The builder is an IDictionary: PowerShell resolves $builder.DataSource as a
        # dictionary key, which the builder rejects. Keywords go through the indexer.
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source'] = $Server
        $builder['Initial Catalog'] = $Database
        $builder['Integrated Security'] = $true
        $builder['ApplicationIntent'] = 'ReadOnly'
        $builder['Application Name'] = 'Application Packager Discovery'
        $builder['Connect Timeout'] = [Math]::Max(1, $ConnectTimeout)
        $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $DiscoveryInventoryQuery
        $command.CommandType = [System.Data.CommandType]::Text
        $command.CommandTimeout = [Math]::Max(1, $CommandTimeout)
        $arch32 = $command.Parameters.Add('@Arch32', [System.Data.SqlDbType]::NVarChar, 3)
        $arch32.Value = 'x86'
        $arch64 = $command.Parameters.Add('@Arch64', [System.Data.SqlDbType]::NVarChar, 3)
        $arch64.Value = 'x64'
        $objectType = $command.Parameters.Add('@AppObjectType', [System.Data.SqlDbType]::Int)
        $objectType.Value = 31
        $reader = $command.ExecuteReader()
        Write-DiscoveryInventory -Reader $reader
    }
    catch {
        Write-DiscoveryError -Exception $_.Exception
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $command) { $command.Dispose() }
        if ($null -ne $connection) { $connection.Dispose() }
    }
    [Console]::Out.WriteLine('{"t":"end"}')
}

if (Get-Variable -Name DiscoveryRun -Scope Script -ValueOnly -ErrorAction SilentlyContinue) {
    Invoke-DiscoveryInventory -Server $DiscoveryServer -Database $DiscoveryDatabase -ConnectTimeout $DiscoveryConnectTimeout -CommandTimeout $DiscoveryCommandTimeout
}
