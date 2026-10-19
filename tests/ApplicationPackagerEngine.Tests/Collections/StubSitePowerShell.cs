using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// A site in a child Windows PowerShell: stub ConfigMgr cmdlets over a JSON state, as the lab
/// site behaves (rules are embedded objects of the collection with an ObjectClass, the query
/// rule cmdlets match a rule name as a wildcard pattern, a failed cmdlet throws; each listed failure happens once). It runs the
/// embedded Collections scripts under strict mode as the session host does, or a function of a
/// PowerShell module imported read-only, and returns the output, the cmdlet calls and the
/// number of query rules on each collection after every rule change.
/// </summary>
internal sealed class StubSitePowerShell : IConfigMgrSession
{
    private const string Harness = """
        param([string]$StatePath, [string]$Mode, [string]$ScriptPath, [string]$ParametersPath, [string]$ModulePath, [string]$Function, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        $global:Options = $state.Options
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Counts = [System.Collections.Generic.List[string]]::new()
        $global:Cols = [System.Collections.Generic.List[object]]::new()
        $global:Resources = @{}
        foreach ($r in @($state.Resources)) { if ($null -ne $r) { $global:Resources[[string]$r.Id] = [string]$r.Name } }
        foreach ($c in @($state.Collections)) {
            if ($null -eq $c) { continue }
            $rules = [System.Collections.Generic.List[object]]::new()
            foreach ($r in @($c.Rules)) { if ($null -ne $r) { $rules.Add([pscustomobject]@{ ObjectClass = [string]$r.ObjectClass; RuleName = [string]$r.RuleName; QueryExpression = [string]$r.QueryExpression; ResourceID = [string]$r.ResourceID; IncludeCollectionID = [string]$r.IncludeCollectionID; ExcludeCollectionID = [string]$r.ExcludeCollectionID }) } }
            $global:Cols.Add([pscustomobject]@{ CollectionID = [string]$c.CollectionID; Name = [string]$c.Name; ObjectPath = ''; LimitToCollectionID = [string]$c.LimitToCollectionID; LimitToCollectionName = ''; MemberCount = 0; RefreshType = 2; RefreshSchedule = $null; Comment = ''; ServiceWindowsCount = 0; CollectionRules = $rules })
        }
        function global:Get-Col([string]$Id) { $global:Cols | Where-Object { $_.CollectionID -eq $Id } | Select-Object -First 1 }
        function global:Write-Count([string]$Id) { $c = Get-Col $Id; $global:Counts.Add($Id + '=' + @($c.CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' }).Count) }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            $global:Log.Add('Invoke-CMWmiQuery')
            if ($Query -match "FROM SMS_Collection WHERE CollectionType = 2 AND CollectionID = '(\w+)'") { return @(Get-Col $Matches[1] | Where-Object { $null -ne $_ }) }
            if ($Query -match 'FROM SMS_Collection WHERE CollectionType = 2$') { return @($global:Cols) }
            if ($Query -match "SMS_CollectionDependencies WHERE SourceCollectionID = '(\w+)'") {
                $id = $Matches[1]
                return @(foreach ($c in $global:Cols) {
                    if ($c.LimitToCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 1 } }
                    foreach ($r in $c.CollectionRules) {
                        if ($r.IncludeCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 2 } }
                        if ($r.ExcludeCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 3 } }
                    }
                })
            }
            if ($Query -match 'FROM SMS_DeploymentSummary') { return @() }
            if ($Query -match 'SELECT CollectionID, Name FROM SMS_Collection WHERE (.+)$') {
                $ids = @([regex]::Matches($Matches[1], "CollectionID = '(\w+)'") | ForEach-Object { $_.Groups[1].Value })
                return @($global:Cols | Where-Object { $ids -contains $_.CollectionID } | ForEach-Object { [pscustomobject]@{ CollectionID = $_.CollectionID; Name = $_.Name } })
            }
            if ($Query -match 'FROM SMS_R_System WHERE ResourceId = (\d+)$') { if ($global:Resources.ContainsKey($Matches[1])) { return @([pscustomobject]@{ ResourceId = $Matches[1]; Name = $global:Resources[$Matches[1]] }) } else { return @() } }
            if ($Query -match [string]$global:Options.Reject -or $Query -notmatch '^\s*select\s') { throw 'The SMS Provider reported an error.' }
            $rows = @(); $i = 0
            foreach ($key in @($global:Resources.Keys | Sort-Object)) { $i++; $rows += [pscustomobject]@{ PropertyNames = @('Name', 'ResourceID'); Name = $global:Resources[$key]; ResourceID = $key; ResourceDomainORWorkgroup = 'CONTOSO'; Client = 1 } }
            return $rows
        }
        function global:Get-CMCollection { [CmdletBinding()] param([string]$Name, [switch]$DisableWildcardHandling) $global:Cols | Where-Object { $_.Name -eq $Name } | ForEach-Object { [pscustomobject]@{ CollectionID = $_.CollectionID; Name = $_.Name; CollectionType = 2 } } }
        function global:Convert-CMSchedule { param([object]$InputObject, [string]$ScheduleString) '' }
        function global:New-CMSchedule { param([string]$RecurInterval, [int]$RecurCount) [pscustomobject]@{} }
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @($global:Options.Fail)) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$What, [string]$Name) { if ($global:FailLeft.Remove($What + ':' + $Name)) { throw 'The SMS Provider reported an error.' } }
        function global:Get-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName)
            $global:Log.Add('Get-Query:' + $RuleName)
            @((Get-Col $CollectionId).CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' -and (-not $RuleName -or $_.RuleName -like $RuleName) })
        }
        function global:Add-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName, [string]$QueryExpression)
            $global:Log.Add('Add-Query:' + $RuleName)
            Test-Fail 'add' $RuleName
            if ($QueryExpression -match [string]$global:Options.Reject) { throw 'The SMS Provider reported an error.' }
            (Get-Col $CollectionId).CollectionRules.Add([pscustomobject]@{ ObjectClass = 'SMS_CollectionRuleQuery'; RuleName = $RuleName; QueryExpression = $QueryExpression; ResourceID = ''; IncludeCollectionID = ''; ExcludeCollectionID = '' })
            Write-Count $CollectionId
        }
        function global:Remove-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName, [switch]$Force)
            $global:Log.Add('Remove-Query:' + $RuleName)
            Test-Fail 'remove' $RuleName
            $c = Get-Col $CollectionId
            foreach ($r in @($c.CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' -and $_.RuleName -like $RuleName })) { [void]$c.CollectionRules.Remove($r) }
            Write-Count $CollectionId
        }
        function global:Add-CMDeviceCollectionDirectMembershipRule { [CmdletBinding()] param([string]$CollectionId, [long]$ResourceId) $global:Log.Add('Add-Direct'); (Get-Col $CollectionId).CollectionRules.Add([pscustomobject]@{ ObjectClass = 'SMS_CollectionRuleDirect'; RuleName = $global:Resources[[string]$ResourceId]; QueryExpression = ''; ResourceID = [string]$ResourceId; IncludeCollectionID = ''; ExcludeCollectionID = '' }) }
        function global:Remove-CMDeviceCollectionDirectMembershipRule { [CmdletBinding()] param([string]$CollectionId, [long]$ResourceId, [switch]$Force) $global:Log.Add('Remove-Direct'); $c = Get-Col $CollectionId; foreach ($r in @($c.CollectionRules | Where-Object { $_.ResourceID -eq [string]$ResourceId })) { [void]$c.CollectionRules.Remove($r) } }
        function global:Add-CMDeviceCollectionIncludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$IncludeCollectionId) $global:Log.Add('Add-Include'); (Get-Col $CollectionId).CollectionRules.Add([pscustomobject]@{ ObjectClass = 'SMS_CollectionRuleIncludeCollection'; RuleName = (Get-Col $IncludeCollectionId).Name; QueryExpression = ''; ResourceID = ''; IncludeCollectionID = $IncludeCollectionId; ExcludeCollectionID = '' }) }
        function global:Add-CMDeviceCollectionExcludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$ExcludeCollectionId) $global:Log.Add('Add-Exclude'); (Get-Col $CollectionId).CollectionRules.Add([pscustomobject]@{ ObjectClass = 'SMS_CollectionRuleExcludeCollection'; RuleName = (Get-Col $ExcludeCollectionId).Name; QueryExpression = ''; ResourceID = ''; IncludeCollectionID = ''; ExcludeCollectionID = $ExcludeCollectionId }) }
        function global:Remove-CMDeviceCollection { [CmdletBinding()] param([string]$CollectionId, [switch]$Force) $global:Log.Add('Remove-Collection'); [void]$global:Cols.Remove((Get-Col $CollectionId)) }
        function global:Invoke-CMCollectionUpdate { [CmdletBinding()] param([string]$CollectionId) $global:Log.Add('Invoke-CMCollectionUpdate') }

        $out = @()
        if ($Mode -eq 'script') {
            Set-StrictMode -Version 2.0
            $parameters = @{}
            foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $parameters[$p.Name] = $p.Value }
            $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
            $out = @(& $block @parameters 3>$null)
            Set-StrictMode -Off
        } else {
            Import-Module $ModulePath -Force -DisableNameChecking 3>$null 6>$null
            $arguments = @{}
            foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $arguments[$p.Name] = $p.Value }
            $error.Clear()
            try { $out = @(& $Function @arguments 6>$null) } catch { $out = @([pscustomobject]@{ Kind = 'Thrown'; Message = $_.Exception.Message }) }
        }
        $collections = @(foreach ($c in $global:Cols) { [pscustomobject]@{ CollectionID = $c.CollectionID; Name = $c.Name; LimitToCollectionID = $c.LimitToCollectionID; Rules = @($c.CollectionRules) } })
        $document = [pscustomobject]@{ Output = $out; Log = @($global:Log); Counts = @($global:Counts); State = [pscustomobject]@{ Collections = $collections; Resources = $state.Resources; Options = $state.Options } }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    internal sealed record RunResult(IReadOnlyList<JsonElement> Output, IReadOnlyList<string> Log, IReadOnlyList<string> Counts);

    private JsonObject _state;

    public StubSitePowerShell(JsonObject state) => _state = state;

    public List<string> Log { get; } = [];

    public List<string> Counts { get; } = [];

    public static bool WindowsPowerShellPresent => File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));

    public static JsonObject State(object options, params object[] collections) => JsonSerializer.SerializeToNode(new
    {
        Collections = collections,
        Resources = new[] { new { Id = "16777220", Name = "DC01" }, new { Id = "16777221", Name = "CLIENT01" } },
        Options = options
    })!.AsObject();

    public static object Collection(string id, string name, string limit, params object[] rules) =>
        new { CollectionID = id, Name = name, LimitToCollectionID = limit, Rules = rules };

    public static object QueryRule(string name, string expression) =>
        new { ObjectClass = "SMS_CollectionRuleQuery", RuleName = name, QueryExpression = expression, ResourceID = "", IncludeCollectionID = "", ExcludeCollectionID = "" };

    /// <summary>The query rules of a collection in the current state, as name and expression.</summary>
    public List<(string Name, string Expression)> QueryRules(string collectionId) =>
        _state["Collections"]!.AsArray().Where(c => (string?)c!["CollectionID"] == collectionId)
            .SelectMany(c => c!["Rules"] is JsonArray rules ? rules : [])
            .Where(r => (string?)r!["ObjectClass"] == "SMS_CollectionRuleQuery")
            .Select(r => ((string)r!["RuleName"]!, (string)r["QueryExpression"]!)).ToList();

    public RunResult RunModule(string modulePath, string function, object arguments) => Run("module", null, modulePath, function, JsonSerializer.Serialize(arguments));

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value);
        var run = Run("script", script.Text, null, null, JsonSerializer.Serialize(values));
        return Task.FromResult(new ConfigMgrCommandResult(run.Output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RunResult Run(string mode, string? scriptText, string? modulePath, string? function, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-collstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harnessPath = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            File.WriteAllText(harnessPath, Harness, new System.Text.UTF8Encoding(true));
            File.WriteAllText(statePath, _state.ToJsonString());
            File.WriteAllText(parametersPath, parametersJson);
            File.WriteAllText(scriptPath, scriptText ?? "", new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harnessPath, "-StatePath", statePath, "-Mode", mode,
                         "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-ModulePath", modulePath ?? "", "-Function", function ?? "", "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            var output = Elements(root.GetProperty("Output"));
            var log = Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? "").ToList();
            var counts = Elements(root.GetProperty("Counts")).Select(e => e.GetString() ?? "").ToList();
            Log.AddRange(log);
            Counts.AddRange(counts);
            return new RunResult(output, log, counts);
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
