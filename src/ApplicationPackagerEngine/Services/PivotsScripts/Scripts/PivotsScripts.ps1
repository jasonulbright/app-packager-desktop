param(
    [ValidateSet('Devices', 'Collection', 'Members', 'Scripts', 'Script', 'Check', 'Create', 'Define', 'Approval', 'Approve', 'Deny', 'Remove', 'Run', 'Status', 'Ping')][string]$Operation,
    [string]$CollectionId = '',
    [string]$ScriptGuid = '',
    [string]$ScriptName = '',
    [string]$ScriptText = '',
    [string]$Comment = '',
    [int[]]$ResourceIds = @(),
    [string[]]$ParameterNames = @(),
    [string[]]$ParameterValues = @(),
    [string]$ParamsDefinition = '',
    [long]$OperationId = 0,
    [string]$SiteHash = '',
    [string]$TextHash = '',
    [string]$ScriptVersion = ''
)

# Only values checked against a fixed pattern reach WQL text: a collection ID, a script GUID, an
# operation number and resource IDs (integers). Names, script text, comments and parameter values
# travel as parameters of this script and as cmdlet arguments, never as query text.

$CMPSSuppressFastNotUsedCheck = $true
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$TimeFormat = 'yyyy-MM-ddTHH:mm:ss'
$CollectionPattern = '^[A-Za-z0-9]{8}$'
$GuidPattern = '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'

function Get-Prop {
    param([object]$Object, [string]$Property)
    if ($null -eq $Object) { return $null }
    $found = $Object.PSObject.Properties[$Property]
    if ($null -eq $found) { return $null }
    return $found.Value
}

function Get-TimeText {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return $Value.ToUniversalTime().ToString($TimeFormat, $Invariant)
}

function Invoke-Query {
    param([string]$Query)
    @(Invoke-CMWmiQuery -Option Fast -Query $Query) | Where-Object { $null -ne $_ }
}

function Assert-Guid {
    param([string]$Value)
    if ($Value -notmatch $GuidPattern) { throw 'The script GUID is not valid.' }
    return $Value.ToUpperInvariant()
}

# ---- Targets --------------------------------------------------------------------------------

function Write-Devices {
    foreach ($d in Invoke-Query 'SELECT ResourceID, Name, IsClient, CNIsOnline FROM SMS_CombinedDeviceResources') {
        $online = Get-Prop $d 'CNIsOnline'
        [pscustomobject]@{ K = 'D'; Id = [int](Get-Prop $d 'ResourceID'); N = [string](Get-Prop $d 'Name'); C = [bool](Get-Prop $d 'IsClient'); O = $(if ($null -eq $online) { $null } else { [bool]$online }) }
    }
}

function Write-Collections {
    foreach ($c in Invoke-Query 'SELECT CollectionID, Name, MemberCount FROM SMS_Collection WHERE CollectionType = 2') {
        [pscustomobject]@{ K = 'C'; Id = [string](Get-Prop $c 'CollectionID'); N = [string](Get-Prop $c 'Name'); M = [int](Get-Prop $c 'MemberCount') }
    }
}

function Write-Members {
    param([string]$Id)
    if ($Id -notmatch $CollectionPattern) { throw 'The collection ID is not valid.' }
    $id = $Id.ToUpperInvariant()
    foreach ($m in Invoke-Query "SELECT ResourceID, Name, IsClient FROM SMS_FullCollectionMembership WHERE CollectionID = '$id'") {
        [pscustomobject]@{ K = 'M'; Id = [int](Get-Prop $m 'ResourceID'); N = [string](Get-Prop $m 'Name'); C = [bool](Get-Prop $m 'IsClient') }
    }
}

# ---- Scripts --------------------------------------------------------------------------------

function ConvertFrom-ScriptText {
    param([string]$Encoded)
    if ([string]::IsNullOrEmpty($Encoded)) { return '' }
    # New-CMScript stores UTF-16LE with a byte order mark; scripts of other origins can be UTF-8.
    try { $bytes = [Convert]::FromBase64String($Encoded) } catch { return $null }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2) }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3) }
    $zeros = 0; for ($i = 1; $i -lt [Math]::Min($bytes.Length, 400); $i += 2) { if ($bytes[$i] -eq 0) { $zeros++ } }
    if ($bytes.Length -ge 2 -and $zeros * 4 -ge [Math]::Min($bytes.Length, 400) / 2) { return [System.Text.Encoding]::Unicode.GetString($bytes) }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function ConvertFrom-Definitions {
    param([string]$Encoded)
    if ([string]::IsNullOrWhiteSpace($Encoded)) { return @() }
    $text = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Encoded)).TrimStart([char]0xFEFF)
    $text = $text -replace '^<\?xml[^>]*\?>', ''
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create([System.IO.StringReader]::new($text), $settings)
    try { $xml = [System.Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
    foreach ($node in @($xml.SelectNodes('/ScriptParameters/ScriptParameter'))) {
        [pscustomobject]@{ Name = $node.GetAttribute('Name'); Type = $node.GetAttribute('Type'); Required = ($node.GetAttribute('IsRequired') -eq 'true'); Default = $node.GetAttribute('DefaultValue'); Description = $node.GetAttribute('Description'); HasDefault = $node.HasAttribute('DefaultValue'); Hidden = ($node.GetAttribute('IsHidden') -eq 'true') }
    }
}

function Get-UnicodeHash {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# The Run Scripts operation message (base64 of UTF-8 XML) in the form Invoke-CMScript sends: every
# visible definition sorted by name, with the typed value or the default, and the SHA-256 of the
# UTF-16 parameter XML. Values are XML-escaped.
function Get-RunMessage {
    param([string]$Guid, [string]$Version, [int]$ScriptType, [string]$Hash, [object[]]$Definitions, [hashtable]$Arguments)
    $visible = @($Definitions | Where-Object { $null -ne $_ -and -not $_.Hidden } | Sort-Object -Property @{ Expression = { ([string]$_.Name).ToUpperInvariant() } })
    $b = [System.Text.StringBuilder]::new('<ScriptParameters>')
    if ($visible.Count -gt 0) {
        $group = [guid]::NewGuid().ToString().ToUpperInvariant()
        foreach ($d in $visible) {
            $value = if ($Arguments.ContainsKey($d.Name)) { [System.Convert]::ToString($Arguments[$d.Name], $Invariant) } else { [string]$d.Default }
            $e = { param($t) [System.Security.SecurityElement]::Escape([string]$t) }
            [void]$b.Append(('<ScriptParameter ParameterGroupGuid="{0}" ParameterGroupName="PG_{0}" ParameterName="{1}" ParameterDataType="{2}" ParameterVisibility="0" ParameterType="{3}" ParameterValue="{4}"/>' -f $group, (& $e $d.Name), (& $e $d.Type), $(if ($d.Required) { '1' } else { '0' }), (& $e $value)))
        }
    }
    [void]$b.Append('</ScriptParameters>')
    $parameters = $b.ToString()
    $xml = "<ScriptContent ScriptGuid='$Guid'><ScriptVersion>$([System.Security.SecurityElement]::Escape($Version))</ScriptVersion><ScriptType>$ScriptType</ScriptType><ScriptHash ScriptHashAlg='SHA256'>$Hash</ScriptHash>$parameters<ParameterGroupHash ParameterHashAlg='SHA256'>$(Get-UnicodeHash $parameters)</ParameterGroupHash></ScriptContent>"
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($xml))
}

function Write-Script {
    param([object]$Row, [bool]$Full)
    $text = $null; $parameters = @(); $unreadable = $false
    if ($Full) {
        $text = ConvertFrom-ScriptText ([string](Get-Prop $Row 'Script'))
        try { $parameters = @(ConvertFrom-Definitions ([string](Get-Prop $Row 'ParamsDefinition'))) } catch { $unreadable = $true }
    }
    $updated = Get-Prop $Row 'LastUpdateTime'
    [pscustomobject]@{ K = 'S'; Guid = ([string](Get-Prop $Row 'ScriptGuid')).ToUpperInvariant(); Name = [string](Get-Prop $Row 'ScriptName'); Author = [string](Get-Prop $Row 'Author'); Approver = [string](Get-Prop $Row 'Approver')
        ApprovalState = [int](Get-Prop $Row 'ApprovalState'); Version = [string](Get-Prop $Row 'ScriptVersion'); ScriptType = [int](Get-Prop $Row 'ScriptType'); Feature = [int](Get-Prop $Row 'Feature')
        Comment = [string](Get-Prop $Row 'Comment'); SiteHash = [string](Get-Prop $Row 'ScriptHash'); Updated = (Get-TimeText $updated); Full = $Full; Text = $text; Parameters = $parameters; Unreadable = $unreadable }
}

function Get-FullScript {
    param([string]$Guid)
    $g = Assert-Guid $Guid
    $row = @(Invoke-CMWmiQuery -Query "SELECT * FROM SMS_Scripts WHERE ScriptGuid = '$g'") | Where-Object { $null -ne $_ } | Select-Object -First 1
    if ($null -eq $row) { return $null }
    # A query returns the lazy properties empty; Get() reads Script and ParamsDefinition.
    $row.Get()
    return $row
}

# The parameters of a script as the PowerShell 5.1 parser reads them, with the Run Scripts rules.
function Write-Check {
    param([string]$Text)
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    $list = @(); $problems = @()
    foreach ($e in @($errors)) { $list += ('Line {0}, column {1}: {2}' -f $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber, $e.Message) }
    $parameters = @()
    if ($ast.ParamBlock) {
        foreach ($p in $ast.ParamBlock.Parameters) {
            $name = $p.Name.VariablePath.UserPath
            $type = $p.StaticType
            $siteType = if ($type -eq [string] -or $type -eq [object]) { 'System.String' } elseif ($type -eq [int]) { 'System.Int32' } else { '' }
            if (-not $siteType) { $problems += ('Parameter {0} has type {1}. Run Scripts takes string and integer parameters only.' -f $name, $type.Name) }
            $mandatory = $false
            foreach ($attribute in $p.Attributes) {
                if ($attribute -isnot [System.Management.Automation.Language.AttributeAst]) { continue }
                if ($attribute.TypeName.Name -eq 'Parameter') { foreach ($named in $attribute.NamedArguments) { if ($named.ArgumentName -eq 'Mandatory') { $mandatory = $named.ExpressionOmitted -or $named.Argument.Extent.Text -match '^\$?true$' } } }
            }
            $default = $null
            if ($p.DefaultValue) {
                try { $default = [string]$p.DefaultValue.SafeGetValue() } catch { $problems += ('The default of parameter {0} is not a constant value.' -f $name) }
            }
            if ($null -ne $default -and $default.Contains("'")) { $problems += ('The default of parameter {0} holds a single quote, which Run Scripts cannot pass.' -f $name) }
            $parameters += [pscustomobject]@{ Name = $name; Type = $siteType; Required = $mandatory; Default = $default }
        }
    }
    if ($parameters.Count -gt 10) { $problems += 'A Run Scripts script takes at most 10 parameters.' }
    [pscustomobject]@{ K = 'Check'; Valid = (@($errors).Count -eq 0); Errors = $list; Parameters = $parameters; Problems = $problems }
}

function Get-Definitions {
    param([object[]]$Parameters)
    $b = [System.Text.StringBuilder]::new('<?xml version="1.0" encoding="utf-16"?><ScriptParameters SchemaVersion="1">')
    foreach ($p in $Parameters) {
        $n = [System.Security.SecurityElement]::Escape([string]$p.Name)
        $d = [System.Security.SecurityElement]::Escape([string]$p.Default)
        [void]$b.Append(('<ScriptParameter Name="{0}" FriendlyName="{0}" Type="{1}" Description="" IsRequired="{2}" IsHidden="false" DefaultValue="{3}"><Validators /></ScriptParameter>' -f $n, $p.Type, ([string][bool]$p.Required).ToLowerInvariant(), $d))
    }
    [void]$b.Append('</ScriptParameters>')
    [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($b.ToString()))
}

# ---- Runs -----------------------------------------------------------------------------------

function Get-TextHash {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text.Replace("`r`n", "`n"))))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# Only the devices in -ResourceIds are read when the caller names them: a device with a final row is not read again.
function Write-Status {
    param([long]$Op, [int[]]$Ids)
    $select = "SELECT ResourceId, DeviceName, ScriptExecutionState, ScriptExitCode, ScriptOutput, LastUpdateTime, ScriptGuid FROM SMS_ScriptsExecutionStatus WHERE ClientOperationId = $Op"
    $ids = @($Ids | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
    $queries = if ($ids.Count -eq 0) { @($select) } else { for ($i = 0; $i -lt $ids.Count; $i += 500) { $select + ' AND ResourceId IN (' + ($ids[$i..([Math]::Min($i + 500, $ids.Count) - 1)] -join ',') + ')' } }
    foreach ($query in $queries) {
        foreach ($s in Invoke-Query $query) {
            [pscustomobject]@{ K = 'R'; Id = [int](Get-Prop $s 'ResourceId'); N = [string](Get-Prop $s 'DeviceName'); State = [int](Get-Prop $s 'ScriptExecutionState'); Exit = (Get-Prop $s 'ScriptExitCode'); Output = [string](Get-Prop $s 'ScriptOutput'); Updated = (Get-TimeText (Get-Prop $s 'LastUpdateTime')) }
        }
    }
}

switch ($Operation) {
    'Ping' { [pscustomobject]@{ K = 'Ping'; Site = [string](Get-Prop (Get-CMSite -SiteCode (Get-Location).Drive.Name) 'SiteCode'); Version = [string](Get-Prop (Get-CMSite -SiteCode (Get-Location).Drive.Name) 'Version') } }
    'Devices' { Write-Devices }
    'Collection' { Write-Collections }
    'Members' { Write-Members $CollectionId; Write-Devices }
    'Scripts' {
        foreach ($s in Invoke-Query 'SELECT ScriptGuid, ScriptName, Author, Approver, ApprovalState, ScriptVersion, ScriptType, Feature, Comment, ScriptHash, LastUpdateTime FROM SMS_Scripts WHERE Feature = 0') { Write-Script $s $false }
    }
    'Script' {
        $row = Get-FullScript $ScriptGuid
        if ($null -ne $row) { Write-Script $row $true }
    }
    'Check' { Write-Check $ScriptText }
    'Approval' {
        $code = (Get-Location).Drive.Name
        $sd = @(Invoke-CMWmiQuery -Query "SELECT * FROM SMS_SCI_SiteDefinition WHERE SiteCode = '$code'") | Select-Object -First 1
        $value = $null
        if ($null -ne $sd) { foreach ($p in @($sd.Props)) { if ($p.PropertyName -eq 'TwoKeyApproval') { $value = [int]$p.Value } } }
        [pscustomobject]@{ K = 'Approval'; TwoKey = $value }
    }
    'Create' {
        $check = Write-Check $ScriptText
        if (-not $check.Valid -or @($check.Problems).Count -gt 0) { throw 'The script does not meet the Run Scripts rules. Check it first.' }
        $created = New-CMScript -ScriptName $ScriptName -ScriptText $ScriptText -Fast
        [pscustomobject]@{ K = 'Created'; Guid = ([string](Get-Prop $created 'ScriptGuid')).ToUpperInvariant() }
    }
    'Define' {
        # New-CMScript stores no parameter definitions; without them Invoke-CMScript passes no value.
        # UpdateScript takes named arguments: positional arguments bind in another order.
        $g = Assert-Guid $ScriptGuid
        $check = Write-Check $ScriptText
        $encoded = Get-Definitions @($check.Parameters)
        $drive = (Get-Location).Drive
        $cim = New-CimSession -ComputerName ([string]$drive.Root) -SessionOption (New-CimSessionOption -Protocol Dcom) -ErrorAction Stop
        try {
            $found = @(Get-CimInstance -CimSession $cim -Namespace ('root\SMS\site_' + [string]$drive.Name) -ClassName SMS_Scripts -Filter "ScriptGuid = '$g'" -ErrorAction Stop) | Select-Object -First 1
            if ($null -eq $found) { throw 'The created script was not found on the site.' }
            $instance = $found | Get-CimInstance -ErrorAction Stop
            $result = Invoke-CimMethod -InputObject $instance -MethodName UpdateScript -ErrorAction Stop -Arguments @{ ParamsDefinition = $encoded; Script = [string]$instance.Script; ScriptDescription = [string]$instance.ScriptDescription; ScriptName = [string]$instance.ScriptName; ScriptVersion = [string]$instance.ScriptVersion; Timeout = [uint32]$instance.Timeout }
        }
        finally { Remove-CimSession -CimSession $cim -ErrorAction SilentlyContinue }
        $after = Get-FullScript $g
        [pscustomobject]@{ K = 'Defined'; Guid = $g; Return = [int](Get-Prop $result 'ReturnValue'); Match = ($null -ne $after -and [string](Get-Prop $after 'ParamsDefinition') -eq $encoded) }
        if ($null -ne $after) { Write-Script $after $true }
    }
    { $_ -in 'Approve', 'Deny' } {
        $g = Assert-Guid $ScriptGuid
        # The approval call takes no hash: the text is read again as the last step before it, and the
        # caller compares the hash the site holds after it with the plan.
        $row = Get-FullScript $g
        if ($null -eq $row) { throw 'The site holds no script with this GUID.' }
        $text = ConvertFrom-ScriptText ([string](Get-Prop $row 'Script'))
        if ([string](Get-Prop $row 'ScriptHash') -ne $SiteHash -or $null -eq $text -or (Get-TextHash $text) -ne $TextHash) { throw 'The script text on the site changed after the plan was made.' }
        if ($Operation -eq 'Approve') { Approve-CMScript -ScriptGuid $g -Comment $Comment } else { Deny-CMScript -ScriptGuid $g -Comment $Comment }
        $after = $null
        $readFailed = $false
        try { $after = Get-FullScript $g } catch { $readFailed = $true }
        if ($null -ne $after) { Write-Script $after $true }
        [pscustomobject]@{ K = 'After'; Present = ($null -ne $after); ReadFailed = $readFailed }
    }
    'Remove' {
        $g = Assert-Guid $ScriptGuid
        $item = Get-CMScript -ScriptGuid $g -Fast
        if ($null -eq $item) { [pscustomobject]@{ K = 'Gone'; Guid = $g } }
        else {
            if ([int](Get-Prop $item 'Feature') -ne 0) { throw 'The script belongs to a ConfigMgr feature. It is not removed.' }
            Remove-CMScript -InputObject $item -Force
            $left = @(Invoke-Query "SELECT ScriptGuid FROM SMS_Scripts WHERE ScriptGuid = '$g'").Count
            [pscustomobject]@{ K = 'Removed'; Guid = $g; Left = $left }
        }
    }
    'Run' {
        $g = Assert-Guid $ScriptGuid
        # The hash and the version go into the operation message as text.
        if ($SiteHash -notmatch '^[0-9A-Fa-f]{64}$' -or $ScriptVersion -notmatch '^\d{1,9}$') { throw 'The script text on the site changed after the plan was made.' }
        $row = Get-FullScript $g
        if ($null -eq $row) { throw 'The site holds no script with this GUID.' }
        if ([int](Get-Prop $row 'ApprovalState') -ne 3) { throw 'The script is not approved.' }
        # The text that was approved again after the plan has another site hash and another text hash than the plan.
        if ([string](Get-Prop $row 'ScriptHash') -ne $SiteHash) { throw 'The script text on the site changed after the plan was made.' }
        $text = ConvertFrom-ScriptText ([string](Get-Prop $row 'Script'))
        if ($null -eq $text -or (Get-TextHash $text) -ne $TextHash) { throw 'The script text on the site changed after the plan was made.' }
        if ($ParameterNames.Count -ne $ParameterValues.Count) { throw 'The parameter names and values do not match.' }
        $defined = @{}
        foreach ($d in @(ConvertFrom-Definitions ([string](Get-Prop $row 'ParamsDefinition')))) { $defined[$d.Name] = $d }
        # The site drops a value whose type does not match the definition, and every value for a name it does not define.
        $arguments = @{}
        for ($i = 0; $i -lt $ParameterNames.Count; $i++) {
            $name = $ParameterNames[$i]
            if (-not $defined.ContainsKey($name)) { throw 'A parameter name is not defined on the script.' }
            switch ($defined[$name].Type) {
                'System.Int32' { $n = 0; if (-not [int]::TryParse($ParameterValues[$i], [System.Globalization.NumberStyles]::Integer, $Invariant, [ref]$n)) { throw 'A value of an integer parameter is not a whole number.' }; $arguments[$name] = $n }
                'System.String' { $arguments[$name] = [string]$ParameterValues[$i] }
                default { throw 'A parameter has a type that Run Scripts does not take.' }
            }
        }
        # A run goes to the fixed devices of the plan, also for a collection target: a device that joins the collection after the plan does not get the script.
        $ids = @($ResourceIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
        if ($ids.Count -eq 0) { throw 'No device to run on.' }
        $devices = @()
        for ($i = 0; $i -lt $ids.Count; $i += 500) {
            $part = $ids[$i..([Math]::Min($i + 500, $ids.Count) - 1)] -join ','
            $devices += @(Invoke-CMWmiQuery -Option Fast -Query "SELECT * FROM SMS_CombinedDeviceResources WHERE ResourceID IN ($part)")
        }
        $devices = @($devices | Where-Object { $null -ne $_ })
        if ($devices.Count -ne $ids.Count) { [pscustomobject]@{ K = 'Missing'; Found = $devices.Count; Wanted = $ids.Count }; return }
        # The site and the hash are read again as the last step before the send, in this invocation.
        $now = Get-FullScript $g
        if ($null -eq $now -or [int](Get-Prop $now 'ApprovalState') -ne 3) { throw 'The script is not approved.' }
        if ([string](Get-Prop $now 'ScriptHash') -ne $SiteHash -or [string](Get-Prop $now 'ScriptVersion') -ne $ScriptVersion) { throw 'The script text on the site changed after the plan was made.' }
        $nowText = ConvertFrom-ScriptText ([string](Get-Prop $now 'Script'))
        if ($null -eq $nowText -or (Get-TextHash $nowText) -ne $TextHash) { throw 'The script text on the site changed after the plan was made.' }
        # The operation message carries the planned ScriptHash: the client runs the text of that hash,
        # also when the text of the GUID changes and is approved again after this read.
        $message = Get-RunMessage -Guid $g -Version $ScriptVersion -ScriptType ([int](Get-Prop $row 'ScriptType')) -Hash $SiteHash -Definitions @($defined.Values) -Arguments $arguments
        $drive = (Get-Location).Drive
        $cim = New-CimSession -ComputerName ([string]$drive.Root) -SessionOption (New-CimSessionOption -Protocol Dcom) -ErrorAction Stop
        try {
            $started = Invoke-CimMethod -CimSession $cim -Namespace ('root\SMS\site_' + [string]$drive.Name) -ClassName SMS_ClientOperation -MethodName InitiateClientOperationEx -ErrorAction Stop `
                -Arguments @{ Type = [uint32]135; TargetResourceIDs = [uint32[]]@($ids); RandomizationWindow = [uint32]0; Param = $message }
        }
        finally { Remove-CimSession -CimSession $cim -ErrorAction SilentlyContinue }
        $op = Get-Prop $started 'OperationID'
        [pscustomobject]@{ K = 'Started'; Operation = $(if ("$op" -match '^\d+$' -and [long]$op -gt 0) { [long]$op } else { $null }); Return = (Get-Prop $started 'ReturnValue') }
        $after = $null
        $readFailed = $false
        try { $after = Get-FullScript $g } catch { $readFailed = $true }
        [pscustomobject]@{ K = 'After'; SiteHash = [string](Get-Prop $after 'ScriptHash'); Version = [string](Get-Prop $after 'ScriptVersion'); ApprovalState = [int](Get-Prop $after 'ApprovalState'); Present = ($null -ne $after); ReadFailed = $readFailed }
    }
    'Status' { Write-Status $OperationId $ResourceIds }
}
