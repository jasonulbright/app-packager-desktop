Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# The site database views are outside ConfigMgr role-based administration. Output carries
# distinct application names, versions and inventory views. It carries no ResourceID, no machine
# name and no client count: discovery is application discovery, not asset tracking.
# The query and the way it is read are those of the PowerShell app (AppPackagerDiscovery): one
# SqlDataAdapter fills a DataSet, and the rows are read from the tables. A SqlDataReader that is
# read row by row ended in "Invalid attempt to read when no data is present" at a production site (Q-134).
$script:DiscoveryStep = 'start'
$script:DiscoveryRowCount = -1

$DiscoveryInventoryQuery = @'
SET NOCOUNT ON;

SELECT DISTINCT DisplayName0 AS DisplayName, Version0 AS Version, @Arch32 AS Architecture
FROM dbo.v_GS_ADD_REMOVE_PROGRAMS
WHERE DisplayName0 IS NOT NULL AND DisplayName0 <> N''
UNION ALL
SELECT DISTINCT DisplayName0, Version0, @Arch64
FROM dbo.v_GS_ADD_REMOVE_PROGRAMS_64
WHERE DisplayName0 IS NOT NULL AND DisplayName0 <> N'';

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
    param($Row, [int]$Index)
    $value = $Row[$Index]
    if ($null -eq $value -or $value -is [System.DBNull]) { return $null }
    return $value
}

function Write-DiscoveryInventory {
    # Reads the two tables of the inventory DataSet and writes one JSON object per line.
    # Nothing is written until every table is read, so a failure mid-read leaves no partial rows.
    param([System.Data.DataSet]$DataSet)
    if ($DataSet.Tables.Count -lt 2) {
        throw (New-Object System.InvalidOperationException ('The inventory query returned ' + $DataSet.Tables.Count + ' result set(s) instead of two.'))
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $rows = New-Object System.Collections.Generic.List[string]
    $script:DiscoveryRowCount = 0
    foreach ($row in $DataSet.Tables[0].Rows) {
        $script:DiscoveryRowCount++
        $name = [string](Get-DiscoveryCell $row 0)
        $version = [string](Get-DiscoveryCell $row 1)
        $architecture = [string](Get-DiscoveryCell $row 2)
        $key = $name + [char]0 + $version + [char]0 + $architecture
        if (-not $seen.Add($key)) { continue }
        $rows.Add('{"t":"row","n":' + (ConvertTo-DiscoveryJsonString $name) + ',"v":' + (ConvertTo-DiscoveryJsonString $version) +
                  ',"a":' + (ConvertTo-DiscoveryJsonString $architecture) + '}')
    }

    $script:DiscoveryStep = 'read the application names'
    $applications = New-Object System.Collections.Generic.List[string]
    foreach ($row in $DataSet.Tables[1].Rows) {
        $title = Get-DiscoveryCell $row 0
        if ($null -ne $title -and ([string]$title).Trim()) { $applications.Add([string]$title) }
    }

    $out = [Console]::Out
    foreach ($row in $rows) { $out.WriteLine($row) }
    $out.WriteLine('{"t":"summary"}')
    foreach ($title in $applications) { $out.WriteLine('{"t":"app","n":' + (ConvertTo-DiscoveryJsonString $title) + '}') }
}

function ConvertTo-DiscoveryDetail {
    # One line, ASCII only, at most 300 characters: the text of an exception for the status line and the log.
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    $clean = ($Text -replace '[
	]+', ' ') -replace '[^ -~]', '?'
    $clean = $clean.Trim()
    if ($clean.Length -gt 300) { $clean = $clean.Substring(0, 300) }
    return $clean
}

function Write-DiscoveryError {
    # The error numbers, two message flags, the exception type names and the text of the innermost exception leave the process.
    # The text names the cause (for example the certificate chain of the SQL Server is not trusted); it never holds inventory.
    param([System.Exception]$Exception, [string]$Step = '', [int]$RowCount = -1)
    $numbers = New-Object System.Collections.Generic.List[int]
    $types = New-Object System.Collections.Generic.List[string]
    $sspi = $false
    $anonymous = $false
    $detail = ''
    $current = $Exception
    while ($null -ne $current) {
        $text = [string]$current.Message
        if ($text -match 'SSPI') { $sspi = $true }
        if ($text -match 'ANONYMOUS LOGON') { $anonymous = $true }
        $types.Add($current.GetType().Name)
        if ($text.Trim()) { $detail = $text }
        if ($current -is [System.Data.SqlClient.SqlException]) {
            foreach ($e in $current.Errors) { $numbers.Add([int]$e.Number) }
            if ($numbers.Count -eq 0) { $numbers.Add([int]$current.Number) }
        }
        $current = $current.InnerException
    }
    $flag = { param($b) if ($b) { 'true' } else { 'false' } }
    $typeJson = ($types | ForEach-Object { ConvertTo-DiscoveryJsonString $_ }) -join ','
    [Console]::Out.WriteLine('{"t":"error","stage":"sql","numbers":[' + ($numbers -join ',') + '],"sspi":' + (& $flag $sspi) + ',"anonymous":' + (& $flag $anonymous) +
        ',"types":[' + $typeJson + '],"detail":' + (ConvertTo-DiscoveryJsonString (ConvertTo-DiscoveryDetail $detail)) +
        ',"step":' + (ConvertTo-DiscoveryJsonString $Step) + ',"rows":' + $RowCount + '}')
}

function Invoke-DiscoveryInventory {
    param([string]$Server, [string]$Database, [int]$ConnectTimeout, [int]$CommandTimeout, [bool]$Encrypt = $false, [bool]$Trust = $false, [bool]$TestOnly = $false)
    $connection = $null
    $command = $null
    $adapter = $null
    try {
        $script:DiscoveryStep = 'build the connection'
        # The builder is an IDictionary: PowerShell resolves $builder.DataSource as a
        # dictionary key, which the builder rejects. Keywords go through the indexer.
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source'] = $Server
        $builder['Initial Catalog'] = $Database
        $builder['Integrated Security'] = $true
        $builder['ApplicationIntent'] = 'ReadOnly'
        $builder['Application Name'] = 'Application Packager Discovery'
        $builder['Connect Timeout'] = [Math]::Max(1, $ConnectTimeout)
        # Only a chosen option is written; the client library keeps its own default otherwise.
        if ($Encrypt) { $builder['Encrypt'] = $true }
        if ($Trust) { $builder['TrustServerCertificate'] = $true }
        $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
        $script:DiscoveryStep = 'open the connection'
        $connection.Open()
        $script:DiscoveryStep = 'prepare the command'
        $command = $connection.CreateCommand()
        $command.CommandText = if ($TestOnly) { 'SELECT TOP (1) DisplayName0 FROM dbo.v_GS_ADD_REMOVE_PROGRAMS' } else { $DiscoveryInventoryQuery }
        $command.CommandType = [System.Data.CommandType]::Text
        $command.CommandTimeout = [Math]::Max(1, $CommandTimeout)
        if ($TestOnly) {
            $command.CommandTimeout = [Math]::Max(1, [Math]::Min($CommandTimeout, 60))
            $script:DiscoveryStep = 'test the connection'
            [void]$command.ExecuteScalar()
            [Console]::Out.WriteLine('{"t":"summary"}')
        }
        else {
            $arch32 = $command.Parameters.Add('@Arch32', [System.Data.SqlDbType]::NVarChar, 3)
            $arch32.Value = 'x86'
            $arch64 = $command.Parameters.Add('@Arch64', [System.Data.SqlDbType]::NVarChar, 3)
            $arch64.Value = 'x64'
            $objectType = $command.Parameters.Add('@AppObjectType', [System.Data.SqlDbType]::Int)
            $objectType.Value = 31
            $script:DiscoveryStep = 'run the query'
            $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
            $dataSet = New-Object System.Data.DataSet
            [void]$adapter.Fill($dataSet)
            $script:DiscoveryStep = 'read the inventory rows'
            Write-DiscoveryInventory -DataSet $dataSet
        }
    }
    catch {
        Write-DiscoveryError -Exception $_.Exception -Step $script:DiscoveryStep -RowCount $script:DiscoveryRowCount
    }
    finally {
        if ($null -ne $adapter) { $adapter.Dispose() }
        if ($null -ne $command) { $command.Dispose() }
        if ($null -ne $connection) { $connection.Dispose() }
    }
    [Console]::Out.WriteLine('{"t":"end"}')
}

if (Get-Variable -Name DiscoveryRun -Scope Script -ValueOnly -ErrorAction SilentlyContinue) {
    Invoke-DiscoveryInventory -Server $DiscoveryServer -Database $DiscoveryDatabase -ConnectTimeout $DiscoveryConnectTimeout -CommandTimeout $DiscoveryCommandTimeout -Encrypt $DiscoveryEncrypt -Trust $DiscoveryTrust -TestOnly $DiscoveryTestOnly
}
