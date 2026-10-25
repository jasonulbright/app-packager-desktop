Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Read only: one connection with read-only intent and one command of SELECT statements whose text
# is a constant. The caller's values travel only as typed parameters (@Cap, @Days). The view times
# are UTC, so the age compares with GETUTCDATE(). Nothing is written until both result sets are
# read, so a failure mid-read leaves no partial rows. The raw SQL message text never leaves the
# process.

$LiveSqlQuery = @'
SET NOCOUNT ON;

SELECT TOP (@Cap + 1) sys.ResourceID, sys.Name0, ch.LastEvaluationHealthy, ch.ClientActiveStatus, ch.ClientStateDescription,
       ch.LastActiveTime, ch.LastOnline, ch.LastDDR, ch.LastPolicyRequest, ch.LastHW, ch.LastHealthEvaluation,
       sys.Client_Version0, sys.Operating_System_Name_and0
FROM dbo.v_CH_ClientSummary AS ch
JOIN dbo.v_R_System AS sys ON sys.ResourceID = ch.ResourceID
WHERE sys.Client0 = 1 AND ISNULL(sys.Obsolete0, 0) = 0 AND ISNULL(sys.Decommissioned0, 0) = 0
ORDER BY sys.ResourceID;

SELECT TOP (@Cap + 1) sys.ResourceID, sys.Name0, ch.LastActiveTime, ch.LastDDR,
       DATEDIFF(day, COALESCE(ch.LastDDR, ch.LastActiveTime), GETUTCDATE()) AS DaysSinceContact,
       sys.Operating_System_Name_and0, sys.Client_Version0
FROM dbo.v_CH_ClientSummary AS ch
JOIN dbo.v_R_System AS sys ON sys.ResourceID = ch.ResourceID
WHERE sys.Client0 = 1 AND ISNULL(sys.Obsolete0, 0) = 0 AND ISNULL(sys.Decommissioned0, 0) = 0
  AND (COALESCE(ch.LastDDR, ch.LastActiveTime) IS NULL
       OR DATEDIFF(day, COALESCE(ch.LastDDR, ch.LastActiveTime), GETUTCDATE()) > @Days)
ORDER BY DaysSinceContact DESC, sys.ResourceID;
'@

$LiveSqlTestQuery = 'SELECT COUNT(*) FROM dbo.v_CH_ClientSummary;'

function ConvertTo-LiveJsonString {
    # ASCII-only output: the parent decodes standard output in the console code page.
    param([AllowNull()][object]$Value)
    if ($null -eq $Value -or $Value -is [System.DBNull]) { return '""' }
    if ($Value -is [datetime]) { $text = $Value.ToString('yyyy-MM-ddTHH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture) }
    else { $text = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0}', $Value) }
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

function Read-LiveSet {
    param($Reader, [string]$Name)
    $lines = New-Object System.Collections.Generic.List[string]
    while ($Reader.Read()) {
        $cells = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $Reader.FieldCount; $i++) {
            if ($Reader.IsDBNull($i)) { $cells.Add('""') } else { $cells.Add((ConvertTo-LiveJsonString $Reader.GetValue($i))) }
        }
        $lines.Add('{"t":"r","s":"' + $Name + '","v":[' + ($cells -join ',') + ']}')
    }
    return , $lines
}

function Write-LiveSets {
    param($Reader)
    $clients = Read-LiveSet $Reader 'C'
    if (-not $Reader.NextResult()) { throw (New-Object System.InvalidOperationException 'The query returned fewer than two result sets.') }
    $inactive = Read-LiveSet $Reader 'I'
    $out = [Console]::Out
    foreach ($line in $clients) { $out.WriteLine($line) }
    $out.WriteLine('{"t":"n","s":"C","rows":' + $clients.Count + '}')
    foreach ($line in $inactive) { $out.WriteLine($line) }
    $out.WriteLine('{"t":"n","s":"I","rows":' + $inactive.Count + '}')
}

function Write-LiveError {
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

function Invoke-LiveSql {
    param([string]$Server, [string]$Database, [int]$ConnectTimeout, [int]$CommandTimeout, [int]$Cap, [int]$Days, [bool]$Trust, [bool]$TestOnly)
    $connection = $null
    $command = $null
    $reader = $null
    try {
        # The builder is an IDictionary: keywords go through the indexer.
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder['Data Source'] = $Server
        $builder['Initial Catalog'] = $Database
        $builder['Integrated Security'] = $true
        $builder['ApplicationIntent'] = 'ReadOnly'
        $builder['Application Name'] = 'Application Packager Site Hygiene'
        $builder['Connect Timeout'] = [Math]::Max(1, $ConnectTimeout)
        if ($Trust) { $builder['TrustServerCertificate'] = $true }
        $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandType = [System.Data.CommandType]::Text
        $command.CommandTimeout = [Math]::Max(1, $CommandTimeout)
        if ($TestOnly) {
            $command.CommandText = $LiveSqlTestQuery
            $count = $command.ExecuteScalar()
            [Console]::Out.WriteLine('{"t":"test","clients":' + [int]$count + '}')
        }
        else {
            $command.CommandText = $LiveSqlQuery
            $capParameter = $command.Parameters.Add('@Cap', [System.Data.SqlDbType]::Int)
            $capParameter.Value = $Cap
            $daysParameter = $command.Parameters.Add('@Days', [System.Data.SqlDbType]::Int)
            $daysParameter.Value = $Days
            $reader = $command.ExecuteReader()
            Write-LiveSets -Reader $reader
        }
    }
    catch {
        Write-LiveError -Exception $_.Exception
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $command) { $command.Dispose() }
        if ($null -ne $connection) { $connection.Dispose() }
    }
    [Console]::Out.WriteLine('{"t":"end"}')
}

if (Get-Variable -Name LiveRun -Scope Script -ValueOnly -ErrorAction SilentlyContinue) {
    Invoke-LiveSql -Server $LiveServer -Database $LiveDatabase -ConnectTimeout $LiveConnectTimeout -CommandTimeout $LiveCommandTimeout -Cap $LiveCap -Days $LiveDays -Trust $LiveTrust -TestOnly $LiveTestOnly
}
