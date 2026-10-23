using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// A ConfigMgr site in a child Windows PowerShell: stub cmdlets and WMI queries over a JSON state
/// that store what the lab site stores (CM01, 5.2603): times as the wall-clock text given, an
/// application deadline of local or unspecified kind with a UTC basis shifted by four hours,
/// the package and task sequence flags in AdvertFlags and RemoteClientFlags, an expiration enabled
/// by -UseUtcForExpireSchedule, a task sequence -DeploymentOption RunFromDistributionPoint ignored,
/// the rerun behavior, the maintenance-window settings, metered and restart suppression ignored for
/// Available, an application deployment refused without content or when the application is already
/// deployed to the collection, the slow network value RunProgramFromDistributionPoint refused, and
/// a second deployment of a package, task sequence or update group to one collection allowed.
/// A query the stub does not know throws, as an unknown WQL class would. Each listed failure
/// ("create:Type", "after-create:Type", "remove:Id", "after-remove:Id", "distribute:GroupName",
/// "query:Class") happens once, also across runs. Option "CreateTwice:Type" makes a create store
/// two deployments; option "HideNew" hides new deployments from queries. A new advertisement
/// raises status message 30006 with its ID and account unless option "NoStatus" is set; option
/// "CreatedBy" sets the account a create records (default: the account running the tests);
/// option "ForeignOnReadBack:Type" adds a deployment of another admin to the same object and
/// collection when the create reads its new deployment back. The create cmdlets return the
/// deployment they made, as on CM01, unless option "NoReturn" is set. Option "ForeignOnCreate:Type"
/// adds a second deployment by the same account right after the create (a script that runs at
/// the same time), "ForeignOffsetSeconds" moves its creation time. Option "CreatedNow" records the
/// real time of each create instead of "CreatedUtc". "Revisions" holds earlier application revisions,
/// each with its own CI_ID, CIVersion and DateLastModified and the DateCreated of the model, which every
/// revision shares; an application deployment holds the CI_ID of the latest revision of its model in
/// AssignedCIs and keeps its CreationTime and LastModificationTime when a revision is added, as on CM01.
/// </summary>
internal sealed class StubDeploymentSite : IConfigMgrSession
{
    private const string Harness = """
        param([string]$StatePath, [string]$ScriptPath, [string]$ParametersPath, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        function global:V([object]$Object, [string]$Name, [object]$Default) { if ($null -eq $Object) { return $Default }; $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p -or $null -eq $p.Value) { return $Default }; return $p.Value }
        function global:L([object]$List) { $r = [System.Collections.Generic.List[object]]::new(); foreach ($x in @($List)) { if ($null -ne $x) { $r.Add($x) } }; return ,$r }
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Opt = @{}
        foreach ($p in $state.Options.PSObject.Properties) { $global:Opt[$p.Name] = $p.Value }
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @(V $state.Options 'Fail' @())) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$Key) { if ($global:FailLeft.Remove($Key)) { throw "The SMS Provider reported an error: SECRET-DETAIL-1234 ($Key)." } }
        $global:S = @{}
        foreach ($k in 'Applications','Packages','TaskSequences','Groups','Collections','DpGroups','DpGroupContent','DpStatus','AppAssignments','Adverts','UgAssignments','StatusMessages','Revisions','Summaries') { $global:S[$k] = L (V $state $k @()) }
        $global:NextAssignment = [long](V $state 'NextAssignment' 16777400)
        $global:NextAdvert = [int](V $state 'NextAdvert' 32)
        $global:NewKeys = [System.Collections.Generic.List[string]]::new()
        $global:NextRecord = [long](V $state 'NextRecord' 72057594038098400)
        $global:CreatedBy = [string](V $state.Options 'CreatedBy' ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name))
        function global:Start-Sleep { param([int]$Seconds) $global:Log.Add('Sleep') }
        function global:Add-Foreign([string]$List, [string]$Key, [object]$Row) {
            $f = $Row.PSObject.Copy()
            $f.$Key = if ($Key -eq 'AdvertisementID') { 'MCM2' + ('{0:X4}' -f $global:NextAdvert) } else { '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}' }
            if ($Key -eq 'AdvertisementID') { $global:NextAdvert++ }
            if ($null -ne $f.PSObject.Properties['LastModifiedBy']) { $f.LastModifiedBy = 'CONTOSO\OtherAdmin' }
            $global:S[$List].Add($f)
        }
        function global:Foreign-OnReadBack([string]$List, [string]$Key, [string]$Id, [string]$Type) {
            if (-not $global:Opt.ContainsKey('ForeignOnReadBack:' + $Type) -or -not $global:NewKeys.Contains($Id)) { return }
            $global:Opt.Remove('ForeignOnReadBack:' + $Type)
            $row = $global:S[$List] | Where-Object { $_.$Key -eq $Id } | Select-Object -First 1
            if ($null -ne $row) { Add-Foreign $List $Key $row }
        }
        $global:Fmt = 'yyyy-MM-ddTHH:mm:ss'
        $global:Created = [datetime]::SpecifyKind([datetime]::Parse([string](V $state.Options 'CreatedUtc' '2026-10-20T12:00:30'), [Globalization.CultureInfo]::InvariantCulture), 'Utc').ToLocalTime()
        function global:Dt([string]$Text) { if (-not $Text) { return $null }; [datetime]::ParseExact($Text, $global:Fmt, [Globalization.CultureInfo]::InvariantCulture) }
        function global:Tx([object]$Value) { if ($Value -isnot [datetime]) { return '' }; $Value.ToString($global:Fmt, [Globalization.CultureInfo]::InvariantCulture) }
        function global:Copy-Row([object]$O) {
            $c = $O.PSObject.Copy()
            foreach ($n in 'StartTime','EnforcementDeadline','PresentTime','CreationTime','LastModificationTime','Time','DateLastModified','DateCreated','SummarizationTime') { if ($null -ne $c.PSObject.Properties[$n] -and $c.$n -is [string]) { $c.$n = Dt $c.$n } }
            if ($null -ne $c.PSObject.Properties['AssignedSchedule']) { $c.AssignedSchedule = @(@($c.AssignedSchedule) | Where-Object { $null -ne $_ } | ForEach-Object { [pscustomobject]@{ StartTime = (Dt ([string]$_.StartTime)); IsGMT = [bool]$_.IsGMT } }) }
            if ($null -ne $c.PSObject.Properties['AppModelID']) { $latest = $global:S.Applications | Where-Object { [string]$_.ModelID -eq [string]$c.AppModelID } | Select-Object -First 1; if ($null -ne $latest) { $c.AssignedCIs = @([long]$latest.CI_ID) } }
            return $c
        }
        function global:Hidden([object]$O, [string]$Key) { return $global:Opt.ContainsKey('HideNew') -and $global:NewKeys.Contains([string]$O.$Key) }
        function global:Ids([string]$Where, [string]$Name) { @([regex]::Matches($Where, "$Name = '([^']+)'") | ForEach-Object { $_.Groups[1].Value }) }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            $global:Log.Add('Query')
            if ($Query -match 'FROM (\w+)') { Test-Fail ('query:' + $Matches[1]) }
            switch -Regex ($Query) {
                "FROM SMS_ApplicationAssignment WHERE AssignmentUniqueID = '[^']+' OR " { $ids = Ids $Query 'AssignmentUniqueID'; return @($global:S.AppAssignments | Where-Object { $ids -contains $_.AssignmentUniqueID -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_UpdateGroupAssignment WHERE AssignmentUniqueID = '[^']+' OR " { $ids = Ids $Query 'AssignmentUniqueID'; return @($global:S.UgAssignments | Where-Object { $ids -contains $_.AssignmentUniqueID -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_Advertisement WHERE AdvertisementID = '[^']+' OR " { $ids = Ids $Query 'AdvertisementID'; return @($global:S.Adverts | Where-Object { $ids -contains $_.AdvertisementID -and -not (Hidden $_ 'AdvertisementID') } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_DeploymentSummary WHERE (.+)$' { $w = $Matches[1]; $ids = Ids $w 'DeploymentID'; $nums = @([regex]::Matches($w, 'AssignmentID = (\d+)') | ForEach-Object { $_.Groups[1].Value }); return @($global:S.Summaries | Where-Object { $ids -contains [string]$_.DeploymentID -or $nums -contains [string]$_.AssignmentID } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_Application WHERE IsLatest = 1$' { return @($global:S.Applications | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_Application WHERE CI_ID = (\d+)$' { $id = $Matches[1]; return @($global:S.Applications | Where-Object { [string]$_.CI_ID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_Application WHERE ModelID = (\d+) AND IsLatest = 1$' { $id = $Matches[1]; return @($global:S.Applications | Where-Object { [string]$_.ModelID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_Application WHERE ModelID = (\d+)$' { $id = $Matches[1]; return @(@($global:S.Applications) + @($global:S.Revisions) | Where-Object { [string]$_.ModelID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_Package WHERE PackageType = 0$' { return @($global:S.Packages | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_Package WHERE PackageID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.Packages | Where-Object { $_.PackageID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_Program WHERE PackageID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.Packages | Where-Object { $_.PackageID -eq $id } | ForEach-Object { foreach ($n in @($_.Programs)) { [pscustomobject]@{ PackageID = $id; ProgramName = [string]$n } } }) }
                'FROM SMS_TaskSequencePackage$' { return @($global:S.TaskSequences | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_TaskSequencePackage WHERE PackageID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.TaskSequences | Where-Object { $_.PackageID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_AuthorizationList$' { return @($global:S.Groups | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_AuthorizationList WHERE CI_ID = (\d+)$' { $id = $Matches[1]; return @($global:S.Groups | Where-Object { [string]$_.CI_ID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_ObjectContentExtraInfo WHERE ObjectID = '([^']+)'$" { $id = $Matches[1]; return @($global:S.Applications | Where-Object { $_.ModelName -eq $id -and $null -ne $_.PSObject.Properties['ContentPackageID'] } | ForEach-Object { $c = V $_ 'Content' $null; [pscustomobject]@{ PackageID = $_.ContentPackageID; Targeted = [int](V $c 'Targeted' 0); NumberSuccess = [int](V $c 'Success' 0); NumberInProgress = [int](V $c 'InProgress' 0); NumberErrors = [int](V $c 'Errors' 0) } }) }
                "FROM SMS_ObjectContentExtraInfo WHERE PackageID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.Packages | Where-Object { $_.PackageID -eq $id -and $null -ne $_.PSObject.Properties['Content'] } | ForEach-Object { $c = $_.Content; [pscustomobject]@{ PackageID = $id; Targeted = [int](V $c 'Targeted' 0); NumberSuccess = [int](V $c 'Success' 0); NumberInProgress = [int](V $c 'InProgress' 0); NumberErrors = [int](V $c 'Errors' 0) } }) }
                "FROM SMS_TaskSequencePackageReference WHERE PackageID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.TaskSequences | Where-Object { $_.PackageID -eq $id } | ForEach-Object { foreach ($r in @(V $_ 'References' @())) { [pscustomobject]@{ RefPackageID = $r.RefPackageID; ObjectName = $r.ObjectName; Targeted = [int]$r.Targeted; NumberSuccess = [int]$r.Success; NumberInProgress = [int](V $r 'InProgress' 0); NumberErrors = [int](V $r 'Errors' 0) } } }) }
                "SELECT CollectionID, Name, CollectionType, MemberCount FROM SMS_Collection WHERE CollectionID = '(\w+)'$" { $id = $Matches[1]; return @($global:S.Collections | Where-Object { $_.CollectionID -eq $id } | ForEach-Object { Copy-Row $_ }) }
                'SELECT CollectionID, Name FROM SMS_Collection WHERE (.+)$' { $ids = Ids $Matches[1] 'CollectionID'; return @($global:S.Collections | Where-Object { $ids -contains $_.CollectionID } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_ApplicationAssignment WHERE AppModelID = (\d+)( AND TargetCollectionID = '(\w+)')?$" { $m = $Matches[1]; $c = $Matches[3]; return @($global:S.AppAssignments | Where-Object { [string]$_.AppModelID -eq $m -and (-not $c -or $_.TargetCollectionID -eq $c) -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_ApplicationAssignment WHERE AssignmentUniqueID = '([^']+)'$" { $id = $Matches[1]; Foreign-OnReadBack 'AppAssignments' 'AssignmentUniqueID' $id 'Application'; return @($global:S.AppAssignments | Where-Object { $_.AssignmentUniqueID -eq $id -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_UpdateGroupAssignment WHERE AssignedUpdateGroup = (\d+)( AND TargetCollectionID = '(\w+)')?$" { $m = $Matches[1]; $c = $Matches[3]; return @($global:S.UgAssignments | Where-Object { [string]$_.AssignedUpdateGroup -eq $m -and (-not $c -or $_.TargetCollectionID -eq $c) -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_UpdateGroupAssignment WHERE AssignmentUniqueID = '([^']+)'$" { $id = $Matches[1]; Foreign-OnReadBack 'UgAssignments' 'AssignmentUniqueID' $id 'SoftwareUpdateGroup'; return @($global:S.UgAssignments | Where-Object { $_.AssignmentUniqueID -eq $id -and -not (Hidden $_ 'AssignmentUniqueID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_Advertisement WHERE PackageID = '(\w+)'( AND CollectionID = '(\w+)')?$" { $p = $Matches[1]; $c = $Matches[3]; return @($global:S.Adverts | Where-Object { $_.PackageID -eq $p -and (-not $c -or $_.CollectionID -eq $c) -and -not (Hidden $_ 'AdvertisementID') } | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_Advertisement WHERE AdvertisementID = '(\w+)'$" { $id = $Matches[1]; $a = $global:S.Adverts | Where-Object { $_.AdvertisementID -eq $id } | Select-Object -First 1; if ($null -ne $a) { Foreign-OnReadBack 'Adverts' 'AdvertisementID' $id $(if ($a.ProgramName -eq '*') { 'TaskSequence' } else { 'Package' }) }; return @($global:S.Adverts | Where-Object { $_.AdvertisementID -eq $id -and -not (Hidden $_ 'AdvertisementID') } | ForEach-Object { Copy-Row $_ }) }
                "SELECT RecordID, AttributeTime FROM SMS_StatMsgAttributes WHERE AttributeID = 401 AND AttributeValue = '(\w+)'$" { $id = $Matches[1]; return @($global:S.StatusMessages | Where-Object { $_.AdvertisementID -eq $id } | ForEach-Object { [pscustomobject]@{ RecordID = $_.RecordID; AttributeTime = (Dt $_.Time) } }) }
                'FROM SMS_StatusMessage WHERE RecordID = (\d+) AND MessageID = 30006$' { $id = $Matches[1]; return @($global:S.StatusMessages | Where-Object { [string]$_.RecordID -eq $id -and [int]$_.MessageID -eq 30006 } | ForEach-Object { Copy-Row $_ }) }
                'SELECT RecordID, AttributeValue FROM SMS_StatMsgAttributes WHERE RecordID = (\d+) AND AttributeID = 403$' { $id = $Matches[1]; return @($global:S.StatusMessages | Where-Object { [string]$_.RecordID -eq $id } | ForEach-Object { [pscustomobject]@{ RecordID = $_.RecordID; AttributeValue = $_.User } }) }
                'FROM SMS_DistributionPointGroup$' { return @($global:S.DpGroups | ForEach-Object { Copy-Row $_ }) }
                "FROM SMS_DPGroupContentInfo WHERE GroupID = '([^']+)' AND \((.+)\)$" { $g = $Matches[1]; $ids = Ids $Matches[2] 'PackageID'; return @($global:S.DpGroupContent | Where-Object { $_.GroupID -eq $g -and $ids -contains $_.PackageID } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_DPGroupContentInfo WHERE (.+)$' { $ids = Ids $Matches[1] 'PackageID'; return @($global:S.DpGroupContent | Where-Object { $ids -contains $_.PackageID } | ForEach-Object { Copy-Row $_ }) }
                'FROM SMS_DPGroupDistributionStatusDetails WHERE (.+)$' { $ids = Ids $Matches[1] 'PackageID'; return @($global:S.DpStatus | Where-Object { $ids -contains $_.PackageID } | ForEach-Object { Copy-Row $_ }) }
            }
            throw "Unknown query: $Query"
        }
        function global:Stamp { if ($global:Opt.ContainsKey('CreatedNow')) { return [datetime]::Now }; return $global:Created }
        function global:Add-Twin([string]$List, [string]$Key, [string]$Id) {
            $row = $global:S[$List] | Where-Object { $_.$Key -eq $Id } | Select-Object -First 1
            $f = $row.PSObject.Copy()
            $time = (Stamp).AddSeconds([int]$(if ($global:Opt.ContainsKey('ForeignOffsetSeconds')) { $global:Opt['ForeignOffsetSeconds'] } else { 0 }))
            if ($Key -eq 'AdvertisementID') {
                $f.AdvertisementID = 'MCM2' + ('{0:X4}' -f $global:NextAdvert); $global:NextAdvert++
                $global:S.StatusMessages.Add([pscustomobject]@{ RecordID = $global:NextRecord; MessageID = 30006; Time = (Tx $time.ToUniversalTime()); AdvertisementID = $f.AdvertisementID; User = $global:CreatedBy }); $global:NextRecord++
            } else {
                $f.AssignmentUniqueID = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'; $f.AssignmentID = $global:NextAssignment; $global:NextAssignment++
                $f.CreationTime = Tx $time
            }
            $global:S[$List].Add($f)
        }
        function global:Made-Since([string]$List, [string]$Key, [int]$Start, [string]$Type) {
            $first = if ($global:NewKeys.Count -gt $Start) { $global:NewKeys[$Start] } else { $null }
            if ($null -ne $first -and $global:Opt.ContainsKey('ForeignOnCreate:' + $Type)) { Add-Twin $List $Key $first }
            Test-Fail ('after-create:' + $Type)
            if ($null -ne $first -and -not $global:Opt.ContainsKey('NoReturn')) { $global:S[$List] | Where-Object { $_.$Key -eq $first } | Select-Object -First 1 | ForEach-Object { Copy-Row $_ } }
        }
        function global:New-CMSchedule { [CmdletBinding()] param([datetime]$Start, [switch]$Nonrecurring, [switch]$IsUtc) [pscustomobject]@{ StartTime = (Tx $Start); IsGMT = [bool]$IsUtc } }
        function global:Content-Ok([object]$App) { $c = V $App 'Content' $null; return [int](V $c 'Targeted' 0) -gt 0 }
        function global:Make-Many([string]$Type, [scriptblock]$Make) { $times = if ($global:Opt.ContainsKey('CreateTwice:' + $Type)) { 2 } else { 1 }; for ($k = 0; $k -lt $times; $k++) { & $Make } }
        function global:Notify-Fields([string]$Notification) { switch ($Notification) { 'DisplayAll' { @{ UI = $true; Notify = $true } } 'DisplaySoftwareCenterOnly' { @{ UI = $true; Notify = $false } } default { @{ UI = $false; Notify = $false } } } }
        function global:New-CMApplicationDeployment {
            [CmdletBinding()] param([int]$Id, [string]$CollectionId, [string]$DeployAction, [string]$DeployPurpose, [datetime]$AvailableDateTime, [object]$DeadlineDateTime, [string]$TimeBaseOn, [string]$UserNotification, [bool]$OverrideServiceWindow, [bool]$RebootOutsideServiceWindow, [bool]$UseMeteredNetwork)
            $global:Log.Add('New-CMApplicationDeployment:' + $DeployPurpose)
            Test-Fail 'create:Application'
            $n0 = $global:NewKeys.Count
            $app = $global:S.Applications | Where-Object { [string]$_.CI_ID -eq [string]$Id } | Select-Object -First 1
            if ($null -eq $app) { throw 'No object corresponds to the specified parameters.' }
            if (-not (Content-Ok $app)) { throw 'There are no distribution points or distribution point groups in this application, please add one to this application before deploying it.' }
            if (@($global:S.AppAssignments | Where-Object { [string]$_.AppModelID -eq [string]$app.ModelID -and $_.TargetCollectionID -eq $CollectionId }).Count -gt 0) { throw "The application '$($app.LocalizedDisplayName)' has already been deployed to the collection." }
            $required = $DeployPurpose -eq 'Required'
            $deadline = ''
            if ($required) { $d = [datetime]$DeadlineDateTime; if ($TimeBaseOn -eq 'Utc' -and $d.Kind -ne 'Utc') { $d = $d.AddHours(4) }; $deadline = Tx $d }
            $n = Notify-Fields $UserNotification
            Make-Many 'Application' {
                $uid = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
                $global:S.AppAssignments.Add([pscustomobject]@{ AssignmentID = $global:NextAssignment; AssignmentUniqueID = $uid; AssignedCIs = @([long]$app.CI_ID); AppModelID = [long]$app.ModelID; ApplicationName = $app.LocalizedDisplayName
                    TargetCollectionID = $CollectionId; OfferTypeID = $(if ($required) { 0 } else { 2 }); StartTime = (Tx $AvailableDateTime); EnforcementDeadline = $(if ($required) { $deadline } else { $null }); UseGMTTimes = ($TimeBaseOn -eq 'Utc')
                    UserUIExperience = $n.UI; NotifyUser = $n.Notify; OverrideServiceWindows = ($required -and $OverrideServiceWindow); RebootOutsideOfServiceWindows = ($required -and $RebootOutsideServiceWindow)
                    DPLocality = (0x50 -bor $(if ($required -and $UseMeteredNetwork) { 0x80000 } else { 0 })); SuppressReboot = 0; CreationTime = (Tx (Stamp)); LastModificationTime = (Tx (Stamp)); LastModifiedBy = $global:CreatedBy })
                Add-Summary $uid $global:NextAssignment $CollectionId 1
                $global:NewKeys.Add($uid); $global:NextAssignment++
            }
            Made-Since 'AppAssignments' 'AssignmentUniqueID' $n0 'Application'
        }
        function global:Add-Summary([string]$Key, [long]$Assignment, [string]$Collection, [int]$Feature) {
            if ($global:Opt.ContainsKey('NoSummary')) { return }
            $members = [int](V ($global:S.Collections | Where-Object { $_.CollectionID -eq $Collection } | Select-Object -First 1) 'MemberCount' 0)
            $global:S.Summaries.Add([pscustomobject]@{ DeploymentID = $Key; AssignmentID = $Assignment; FeatureType = $Feature; CollectionID = $Collection; NumberTargeted = $members; NumberSuccess = 0
                NumberErrors = 0; NumberInProgress = 0; NumberUnknown = 0; NumberOther = 0; SummarizationTime = (Tx (Stamp)) })
        }
        function global:New-Advert([string]$PackageId, [string]$ProgramName, [string]$CollectionId, [bool]$Required, [datetime]$Available, [bool]$UtcAvailable, [bool]$UtcExpire, [object[]]$Schedule, [long]$Advert, [long]$Remote) {
            $id = 'MCM2' + ('{0:X4}' -f $global:NextAdvert); $global:NextAdvert++
            $global:S.Adverts.Add([pscustomobject]@{ AdvertisementID = $id; PackageID = $PackageId; ProgramName = $ProgramName; CollectionID = $CollectionId; OfferType = $(if ($Required) { 0 } else { 2 })
                PresentTime = (Tx $Available); PresentTimeIsGMT = $UtcAvailable; ExpirationTimeEnabled = $UtcExpire; AssignedScheduleEnabled = $Required; AssignedSchedule = @($(if ($Required) { $Schedule } else { @() }))
                AdvertFlags = $Advert; RemoteClientFlags = $Remote })
            if (-not $global:Opt.ContainsKey('NoStatus')) { $global:S.StatusMessages.Add([pscustomobject]@{ RecordID = $global:NextRecord; MessageID = 30006; Time = (Tx (Stamp).ToUniversalTime()); AdvertisementID = $id; User = $global:CreatedBy }); $global:NextRecord++ }
            Add-Summary $id 0 $CollectionId $(if ($ProgramName -eq '*') { 7 } else { 2 })
            $global:NewKeys.Add($id)
        }
        function global:Rerun-Bit([string]$Rerun, [bool]$Required) { if (-not $Required) { return 0x800 }; switch ($Rerun) { 'NeverRerunDeployedProgram' { 0x1000 } 'RerunIfFailedPreviousAttempt' { 0x2000 } 'RerunIfSucceededOnPreviousAttempt' { 0x4000 } default { 0x800 } } }
        function global:New-CMPackageDeployment {
            [CmdletBinding()] param([switch]$StandardProgram, [string]$PackageId, [string]$ProgramName, [string]$CollectionId, [string]$DeployPurpose, [datetime]$AvailableDateTime, [string]$FastNetworkOption, [string]$SlowNetworkOption, [bool]$AllowFallback, [bool]$UseUtcForAvailableSchedule, [bool]$UseUtcForExpireSchedule, [object[]]$Schedule, [string]$RerunBehavior, [bool]$SoftwareInstallation, [bool]$SystemRestart, [bool]$UseMeteredNetwork)
            $global:Log.Add('New-CMPackageDeployment:' + $DeployPurpose)
            Test-Fail 'create:Package'
            $n0 = $global:NewKeys.Count
            if ($SlowNetworkOption -eq 'RunProgramFromDistributionPoint') { throw "The value 'RunProgramFromDistributionPoint' is not recognized for the parameter 'SlowNetworkOption'." }
            $required = $DeployPurpose -eq 'Required'
            $advert = 0x10000 -bor $(if ($AllowFallback) { 0 } else { 0x20000 }) -bor $(if ($required) { 0x2000000 } else { 0 }) -bor $(if ($required -and $SoftwareInstallation) { 0x100000 } else { 0 }) -bor $(if ($required -and $SystemRestart) { 0x200000 } else { 0 })
            $remote = 0x8000 -bor $(if ($FastNetworkOption -eq 'RunProgramFromDistributionPoint') { 0x8 } else { 0x10 }) -bor $(if ($SlowNetworkOption -eq 'DownloadContentFromDistributionPointAndLocally') { 0x40 } else { 0x20 }) -bor (Rerun-Bit $RerunBehavior $required) -bor $(if ($required -and $UseMeteredNetwork) { 0x40000 } else { 0 })
            Make-Many 'Package' { New-Advert $PackageId $ProgramName $CollectionId $required $AvailableDateTime $UseUtcForAvailableSchedule $UseUtcForExpireSchedule $Schedule $advert $remote }
            Made-Since 'Adverts' 'AdvertisementID' $n0 'Package'
        }
        function global:New-CMTaskSequenceDeployment {
            [CmdletBinding()] param([string]$TaskSequencePackageId, [string]$CollectionId, [string]$DeployPurpose, [datetime]$AvailableDateTime, [string]$Availability, [bool]$ShowTaskSequenceProgress, [bool]$AllowFallback, [string]$DeploymentOption, [bool]$UseUtcForAvailableSchedule, [bool]$UseUtcForExpireSchedule, [object[]]$Schedule, [string]$RerunBehavior, [bool]$SoftwareInstallation, [bool]$SystemRestart, [bool]$UseMeteredNetwork)
            $global:Log.Add('New-CMTaskSequenceDeployment:' + $DeployPurpose)
            Test-Fail 'create:TaskSequence'
            $n0 = $global:NewKeys.Count
            if ($DeploymentOption -eq 'DownloadAllContentLocallyBeforeStartingTaskSequence' -and $Availability -ne 'Clients') { throw 'Please make sure the deployment is available to client only since the deployment works with the option DownloadAllContentLocallyBeforeStartingTaskSequence.' }
            $required = $DeployPurpose -eq 'Required'
            $availabilityBits = switch ($Availability) { 'ClientsMediaAndPxe' { 0x40000 } 'MediaAndPxe' { 0x10040000 } 'MediaAndPxeHidden' { 0x30040000 } default { 0 } }
            $advert = 0x90000 -bor $availabilityBits -bor $(if ($ShowTaskSequenceProgress) { 0x800000 } else { 0 }) -bor $(if ($AllowFallback) { 0 } else { 0x20000 }) -bor $(if ($required) { 0x2000000 } else { 0 }) -bor $(if ($required -and $SoftwareInstallation) { 0x100000 } else { 0 }) -bor $(if ($required -and $SystemRestart) { 0x200000 } else { 0 })
            $option = switch ($DeploymentOption) { 'DownloadAllContentLocallyBeforeStartingTaskSequence' { 0x50 } default { 0x300 } }
            $remote = 0x8000 -bor $option -bor (Rerun-Bit $RerunBehavior $required) -bor $(if ($required -and $UseMeteredNetwork) { 0x40000 } else { 0 })
            Make-Many 'TaskSequence' { New-Advert $TaskSequencePackageId '*' $CollectionId $required $AvailableDateTime $UseUtcForAvailableSchedule $UseUtcForExpireSchedule $Schedule $advert $remote }
            Made-Since 'Adverts' 'AdvertisementID' $n0 'TaskSequence'
        }
        function global:New-CMSoftwareUpdateDeployment {
            [CmdletBinding()] param([string]$SoftwareUpdateGroupId, [string]$CollectionId, [string]$DeploymentType, [datetime]$AvailableDateTime, [object]$DeadlineDateTime, [string]$TimeBasedOn, [string]$UserNotification, [bool]$RequirePostRebootFullScan, [switch]$AcceptEula, [bool]$SoftwareInstallation, [bool]$AllowRestart, [bool]$UseMeteredNetwork, [bool]$RestartServer, [bool]$RestartWorkstation, [string]$ProtectedType, [string]$UnprotectedType, [bool]$DownloadFromMicrosoftUpdate)
            $global:Log.Add('New-CMSoftwareUpdateDeployment:' + $DeploymentType)
            Test-Fail 'create:SoftwareUpdateGroup'
            $n0 = $global:NewKeys.Count
            $required = $DeploymentType -eq 'Required'
            $n = Notify-Fields $UserNotification
            $locality = if ($required) { 0x50 -bor $(if ($UnprotectedType -eq 'NoInstall') { 0x20000 } else { 0 }) -bor $(if ($UseMeteredNetwork) { 0x80000 } else { 0 }) -bor $(if ($DownloadFromMicrosoftUpdate) { 0x40000 } else { 0 }) } else { 0x10 }
            Make-Many 'SoftwareUpdateGroup' {
                $uid = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
                $global:S.UgAssignments.Add([pscustomobject]@{ AssignmentID = $global:NextAssignment; AssignmentUniqueID = $uid; AssignedUpdateGroup = [long]$SoftwareUpdateGroupId; TargetCollectionID = $CollectionId
                    StartTime = (Tx $AvailableDateTime); EnforcementDeadline = $(if ($required) { Tx ([datetime]$DeadlineDateTime) } else { $null }); UseGMTTimes = ($TimeBasedOn -eq 'Utc')
                    UserUIExperience = $n.UI; NotifyUser = $n.Notify; OverrideServiceWindows = ($required -and $SoftwareInstallation); RebootOutsideOfServiceWindows = ($required -and $AllowRestart)
                    SuppressReboot = $(if ($required) { $(if ($RestartServer) { 2 } else { 0 }) -bor $(if ($RestartWorkstation) { 1 } else { 0 }) } else { 0 }); RequirePostRebootFullScan = $RequirePostRebootFullScan
                    DPLocality = $locality; CreationTime = (Tx (Stamp)); LastModifiedBy = $global:CreatedBy })
                Add-Summary $uid $global:NextAssignment $CollectionId 5
                $global:NewKeys.Add($uid); $global:NextAssignment++
            }
            Made-Since 'UgAssignments' 'AssignmentUniqueID' $n0 'SoftwareUpdateGroup'
        }
        function global:Remove-Deployment([string]$List, [string]$Key, [string]$Id) {
            $global:Log.Add('Remove:' + $Id)
            Test-Fail ('remove:' + $Id)
            $found = @($global:S[$List] | Where-Object { $_.$Key -eq $Id })
            if ($found.Count -eq 0) { throw 'No object corresponds to the specified parameters.' }
            foreach ($f in $found) { [void]$global:S[$List].Remove($f) }
            Test-Fail ('after-remove:' + $Id)
        }
        function global:Remove-CMApplicationDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Force) Remove-Deployment 'AppAssignments' 'AssignmentUniqueID' $DeploymentId }
        function global:Remove-CMSoftwareUpdateDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Force) Remove-Deployment 'UgAssignments' 'AssignmentUniqueID' $DeploymentId }
        function global:Remove-CMPackageDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Force) Remove-Deployment 'Adverts' 'AdvertisementID' $DeploymentId }
        function global:Remove-CMTaskSequenceDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Force) Remove-Deployment 'Adverts' 'AdvertisementID' $DeploymentId }
        function global:Start-CMContentDistribution {
            [CmdletBinding()] param([string]$ApplicationId, [string]$PackageId, [string]$TaskSequenceId, [string]$DistributionPointGroupName)
            $global:Log.Add('Start-CMContentDistribution:' + $DistributionPointGroupName)
            Test-Fail ('distribute:' + $DistributionPointGroupName)
            $group = $global:S.DpGroups | Where-Object { $_.Name -eq $DistributionPointGroupName } | Select-Object -First 1
            if ($null -eq $group) { throw 'No object corresponds to the specified parameters.' }
            $ids = @()
            if ($ApplicationId) { $ids = @($global:S.Applications | Where-Object { [string]$_.CI_ID -eq $ApplicationId } | ForEach-Object { $_.ContentPackageID }) }
            if ($PackageId) { $ids = @($PackageId) }
            if ($TaskSequenceId) { $ids = @($global:S.TaskSequences | Where-Object { $_.PackageID -eq $TaskSequenceId } | ForEach-Object { @(V $_ 'References' @()) | ForEach-Object { $_.RefPackageID } }) }
            $new = @($ids | Where-Object { $id = $_; @($global:S.DpGroupContent | Where-Object { $_.GroupID -eq $group.GroupID -and $_.PackageID -eq $id }).Count -eq 0 })
            if ($new.Count -eq 0) { throw 'The content has already been targeted to this distribution point group.' }
            foreach ($id in $new) { $global:S.DpGroupContent.Add([pscustomobject]@{ GroupID = $group.GroupID; PackageID = $id }); $global:S.DpStatus.Add([pscustomobject]@{ GroupID = $group.GroupID; PackageID = $id; DPName = 'DP01'; MessageState = 2 }) }
        }

        Set-StrictMode -Version 2.0
        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $parameters[$p.Name] = $p.Value }
        $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
        $out = @(); $thrown = ''
        try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
        Set-StrictMode -Off
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $next = [pscustomobject]@{ Options = $state.Options; NextAssignment = $global:NextAssignment; NextAdvert = $global:NextAdvert; NextRecord = $global:NextRecord }
        foreach ($k in $global:S.Keys) { $next | Add-Member -NotePropertyName $k -NotePropertyValue @($global:S[$k]) }
        $document = [pscustomobject]@{ Output = $out; Thrown = $thrown; Log = @($global:Log); State = $next }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private JsonObject _state;

    public StubDeploymentSite(JsonObject state)
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            "Windows PowerShell 5.1 is not on this computer, so the Deployments script cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    public List<(string Script, Dictionary<string, object?> Parameters)> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<Dictionary<string, object?>, StubDeploymentSite>? BeforeRun { get; set; }

    /// <summary>Called after each script run; a returned exception is thrown although the run's changes stay made.</summary>
    public Func<Dictionary<string, object?>, Exception?>? AfterRun { get; set; }

    public JsonObject State => _state;

    public JsonArray List(string name) => _state[name] as JsonArray ?? (JsonArray)(_state[name] = new JsonArray());

    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public void SetOption(string name, JsonNode? value) => _state["Options"]!.AsObject()[name] = value;

    /// <summary>
    /// A site with one application with content (CI 16779607, model 16778866), one without
    /// (16779604), a package MCM0001B with two programs and content, a task sequence MCM00030 with
    /// one distributed reference, a software update group 16778893, the empty device collection
    /// MCM00098, a user collection MCM00099, the built-in SMS00001 and the DP group All DPs.
    /// </summary>
    public static JsonObject Lab() => JsonNode.Parse("""
        {
          "Options": {},
          "Applications": [
            { "CI_ID": 16779607, "ModelID": 16778866, "ModelName": "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_422a4a5c-3d5d-46b0-9986-7254de924002", "LocalizedDisplayName": "7-Zip (x64 edition) - 26.03", "SoftwareVersion": "26.03", "IsExpired": false, "CIVersion": 13, "DateLastModified": "2026-10-01T09:00:00", "DateCreated": "2026-09-15T13:40:47", "ContentPackageID": "MCM00035", "Content": { "Targeted": 1, "Success": 1 } },
            { "CI_ID": 16779604, "ModelID": 16778870, "ModelName": "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_b802345b-cfc1-4259-95ac-3e7f2c4bdbf1", "LocalizedDisplayName": "7-Zip 26.03 (x64 edition)", "SoftwareVersion": "26.03", "IsExpired": false, "CIVersion": 11, "DateLastModified": "2026-10-01T09:00:00", "DateCreated": "2026-09-15T13:42:20", "ContentPackageID": "MCM00036", "Content": { "Targeted": 0, "Success": 0 } }
          ],
          "Packages": [ { "PackageID": "MCM0001B", "Name": "CMCO-CANARY-Package", "Version": "1", "Programs": [ "CMCO-CANARY-Run", "CMCO-CANARY-RunAvailable" ], "Content": { "Targeted": 1, "Success": 1 } } ],
          "TaskSequences": [ { "PackageID": "MCM00030", "Name": "DH Test TS", "References": [ { "RefPackageID": "MCM0001B", "ObjectName": "CMCO-CANARY-Package", "Targeted": 1, "Success": 1 } ] } ],
          "Groups": [ { "CI_ID": 16778893, "LocalizedDisplayName": "202609-W11-25H2", "NumberOfUpdates": 2 } ],
          "Collections": [
            { "CollectionID": "MCM00098", "Name": "DH-Test-1", "CollectionType": 2, "MemberCount": 0 },
            { "CollectionID": "MCM00099", "Name": "Users", "CollectionType": 1, "MemberCount": 3 },
            { "CollectionID": "SMS00001", "Name": "All Systems", "CollectionType": 2, "MemberCount": 8 }
          ],
          "DpGroups": [ { "GroupID": "{D921E77B-3F7D-4555-83D8-522461F5388F}", "Name": "All DPs", "MemberCount": 1 } ],
          "DpGroupContent": [ { "GroupID": "{D921E77B-3F7D-4555-83D8-522461F5388F}", "PackageID": "MCM00035" } ],
          "DpStatus": [ { "GroupID": "{D921E77B-3F7D-4555-83D8-522461F5388F}", "PackageID": "MCM00035", "DPName": "CM01.CONTOSO.COM", "MessageState": 1 } ],
          "AppAssignments": [], "Adverts": [], "UgAssignments": []
        }
        """)!.AsObject();

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value);
        Runs.Add((script.Name, values));
        BeforeRun?.Invoke(values, this);
        var (output, thrown) = Run(script.Text, JsonSerializer.Serialize(values));
        if (AfterRun?.Invoke(values) is { } after) return Task.FromException<ConfigMgrCommandResult>(after);
        if (thrown.Length > 0)
            return Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, $"The ConfigMgr command {script.Name} failed: {thrown}"));
        return Task.FromResult(new ConfigMgrCommandResult(output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (List<JsonElement> Output, string Thrown) Run(string scriptText, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dhstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harness = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            File.WriteAllText(harness, Harness, new System.Text.UTF8Encoding(true));
            File.WriteAllText(statePath, _state.ToJsonString(), new System.Text.UTF8Encoding(true));
            File.WriteAllText(parametersPath, parametersJson, new System.Text.UTF8Encoding(true));
            File.WriteAllText(scriptPath, scriptText, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-StatePath", statePath, "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            Log.AddRange(Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? ""));
            return (Elements(root.GetProperty("Output")), root.GetProperty("Thrown").GetString() ?? "");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static List<JsonElement> Elements(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Select(e => e.Clone()).ToList(),
        JsonValueKind.Null or JsonValueKind.Undefined => [],
        _ => [value.Clone()]
    };
}
