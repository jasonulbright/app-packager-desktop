using System.Diagnostics;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// The embedded write script in Windows PowerShell over stub ConfigMgr cmdlets that behave as
/// the lab site does: Remove-CMMaintenanceWindow removes every window with the name in any
/// case, Set-CMMaintenanceWindow with a schedule and New-CMMaintenanceWindow write
/// ConfigMgr's own text into the description, and Get-CMCollectionSetting writes the window
/// list of the collection as a whole.
/// </summary>
public sealed class WriteMaintenanceWindowScriptTests
{
    private const string Driver = """
        param([string]$ScriptPath, [string]$StatePath, [string]$ParametersPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Windows = [System.Collections.Generic.List[object]]::new()
        foreach ($w in @($state.Windows)) { $global:Windows.Add([pscustomobject]@{ CollectionId = $w.CollectionId; Name = $w.Name; ServiceWindowID = $w.ServiceWindowID; ServiceWindowType = [int]$w.ServiceWindowType; IsEnabled = [bool]$w.IsEnabled; ServiceWindowSchedules = $w.ServiceWindowSchedules; Description = $w.Description }) }
        $global:Collections = @($state.Collections)
        $global:Options = $state.Options
        $global:Reads = 0
        function global:Add-Intruder { $global:Windows.Add([pscustomobject]@{ CollectionId = 'C1'; Name = 'Intruder'; ServiceWindowID = '{Z}'; ServiceWindowType = 1; IsEnabled = $true; ServiceWindowSchedules = '0043BE00201F2000'; Description = 'added' }) }

        function global:Get-CMDeviceCollection { param([string]$Id) if ($global:Collections -contains $Id) { [pscustomobject]@{ CollectionID = $Id } } }
        function global:Get-CMMaintenanceWindow {
            param([string]$CollectionId)
            $global:Reads++
            if ($global:Options.ChangeOnRead -eq $global:Reads) { Add-Intruder }
            @($global:Windows | Where-Object { $_.CollectionId -eq $CollectionId })
        }
        function global:New-CMSchedule { [pscustomobject]@{ DayDuration = 0; HourDuration = 0; MinuteDuration = 0 } }
        function global:New-CMMaintenanceWindow {
            param([string]$CollectionId, [string]$Name, [object]$Schedule, [string]$ApplyTo, [bool]$IsEnabled, [bool]$IsUtc)
            $global:Log.Add('New-CMMaintenanceWindow')
            if (@($global:Windows | Where-Object { $_.CollectionId -eq $CollectionId -and $_.Name -eq $Name }).Count -gt 0) { throw "The item '$Name' already exists." }
            $global:Windows.Add([pscustomobject]@{ CollectionId = $CollectionId; Name = $Name; ServiceWindowID = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'; ServiceWindowType = 1; IsEnabled = $IsEnabled; ServiceWindowSchedules = '0043BE00201F2000'; Description = 'Occurs ConfigMgr text' })
            if ($global:Options.NewThrows) { throw [System.UnauthorizedAccessException]::new('Access is denied.') }
        }
        function global:Set-CMMaintenanceWindow {
            param([string]$CollectionId, [object]$MaintenanceWindow, [object]$Schedule, [string]$ApplyTo, [bool]$IsEnabled, [bool]$IsUtc)
            $global:Log.Add('Set-CMMaintenanceWindow')
            $target = $global:Windows | Where-Object { $_.ServiceWindowID -eq $MaintenanceWindow.ServiceWindowID }
            if ($PSBoundParameters.ContainsKey('Schedule')) { $target.Description = 'Occurs ConfigMgr updated text' }
            if ($PSBoundParameters.ContainsKey('IsEnabled')) { $target.IsEnabled = $IsEnabled }
        }
        function global:Remove-CMMaintenanceWindow {
            param([string]$CollectionId, [string]$MaintenanceWindowName, [switch]$Force, [switch]$DisableWildcardHandling)
            $global:Log.Add('Remove-CMMaintenanceWindow')
            foreach ($w in @($global:Windows | Where-Object { $_.CollectionId -eq $CollectionId -and $_.Name -eq $MaintenanceWindowName })) { [void]$global:Windows.Remove($w) }
        }
        if (-not $state.DescriptionWritable) {
            # A console installed on the test computer would otherwise answer for the module.
            function global:Get-Command {
                [CmdletBinding()]
                param([string]$Name)
                if ($Name -eq 'Get-CMCollectionSetting') { return }
                Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
            }
        } else {
            function global:Get-CMCollectionSetting {
                param([string]$CollectionId)
                $settings = [pscustomobject]@{ CollectionId = $CollectionId; Items = $null }
                $settings | Add-Member -MemberType ScriptMethod -Name Get -Value { }
                $settings | Add-Member -MemberType ScriptMethod -Name GetArrayItems -Value {
                    param($name)
                    if ($global:Options.ChangeOnSettingsRead) { Add-Intruder }
                    $list =[System.Collections.Generic.List[object]]::new()
                    foreach ($w in @($global:Windows | Where-Object { $_.CollectionId -eq $this.CollectionId })) {
                        $list.Add(@{ ServiceWindowID = [pscustomobject]@{ StringValue = $w.ServiceWindowID }; Name = [pscustomobject]@{ StringValue = $w.Name }; Description = [pscustomobject]@{ StringValue = $w.Description } })
                    }
                    , $list
                }
                $settings | Add-Member -MemberType ScriptMethod -Name SetArrayItems -Value { param($name, $items) $this.Items = $items }
                $settings | Add-Member -MemberType ScriptMethod -Name Put -Value {
                    if ($global:Options.PutThrows) { throw [System.UnauthorizedAccessException]::new('Access is denied.') }
                    $global:Log.Add('Put')
                    $ids = @($this.Items | ForEach-Object { $_['ServiceWindowID'].StringValue })
                    foreach ($w in @($global:Windows | Where-Object { $_.CollectionId -eq $this.CollectionId })) {
                        if ($ids -notcontains $w.ServiceWindowID) { [void]$global:Windows.Remove($w); continue }
                        $item = @($this.Items | Where-Object { $_['ServiceWindowID'].StringValue -eq $w.ServiceWindowID })[0]
                        $w.Description = $item['Description'].StringValue
                    }
                }
                $settings
            }
        }

        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $parameters[$p.Name] = $p.Value }
        $result = @(& $ScriptPath @parameters) | Where-Object { $_.Kind -eq 'Result' } | Select-Object -Last 1
        [Console]::Out.WriteLine((@{ Result = $result; Windows = @($global:Windows); Log = @($global:Log) } | ConvertTo-Json -Depth 6 -Compress))
        """;

    private sealed record StubWindow(string CollectionId, string Name, string ServiceWindowID, int ServiceWindowType, bool IsEnabled, string ServiceWindowSchedules, string Description);

    private sealed record Run(JsonElement Result, List<StubWindow> Windows, List<string> Log)
    {
        public string Status => Result.GetProperty("Status").GetString()!;
    }

    private const string Token = "0043BE00201F2000";

    private static StubWindow W(string name, string id, string description = "Occurs ConfigMgr text") =>
        new("C1", name, id, 1, true, Token, description);

    private static Dictionary<string, object> Expected(StubWindow w, string operation) => new()
    {
        ["Operation"] = operation,
        ["CollectionId"] = w.CollectionId,
        ["ServiceWindowId"] = w.ServiceWindowID,
        ["ExpectedName"] = w.Name,
        ["ExpectedSchedules"] = new[] { Token },
        ["ExpectedType"] = 1,
        ["ExpectedEnabled"] = true
    };

    private static readonly Dictionary<string, object> Schedule = new()
    {
        ["ApplyTo"] = "Any", ["IsEnabled"] = true, ["Recurrence"] = "Weekly", ["Start"] = "2026-11-03T02:00",
        ["DurationHours"] = 4, ["DurationMinutes"] = 0, ["DayOfWeek"] = "Saturday", ["RecurCount"] = 1
    };

    private static Run Invoke(IEnumerable<StubWindow> windows, Dictionary<string, object> parameters, bool descriptionWritable = true, object? options = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-mwscript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var script = ConfigMgrScript.FromResource(MaintenanceWindowService.WriteResource);
            var scriptPath = Path.Combine(folder, "WriteMaintenanceWindow.ps1");
            var driverPath = Path.Combine(folder, "driver.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            File.WriteAllText(scriptPath, script.Text, new System.Text.UTF8Encoding(true));
            File.WriteAllText(driverPath, Driver, new System.Text.UTF8Encoding(true));
            File.WriteAllText(statePath, JsonSerializer.Serialize(new { Collections = new[] { "C1" }, Windows = windows, DescriptionWritable = descriptionWritable, Options = options ?? new { } }));
            File.WriteAllText(parametersPath, JsonSerializer.Serialize(parameters));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", driverPath, "-ScriptPath", scriptPath, "-StatePath", statePath, "-ParametersPath", parametersPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), "The script did not finish.");
            Assert.True(process.ExitCode == 0, "The script failed: " + error.Result);
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            var list = root.GetProperty("Windows").ValueKind == JsonValueKind.Array
                ? root.GetProperty("Windows").Deserialize<List<StubWindow>>()!
                : [root.GetProperty("Windows").Deserialize<StubWindow>()!];
            var log = root.GetProperty("Log").ValueKind == JsonValueKind.Array ? root.GetProperty("Log").Deserialize<List<string>>()! : [];
            return new Run(root.GetProperty("Result").Clone(), list, log);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RemoveOfAWindowThatSharesItsNameRemovesOnlyThatWindowById()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target, W("PATCH", "{B}", "second"), W("Other", "{C}")], Expected(target, "Remove"));

        Assert.Equal("ok", run.Status);
        Assert.Equal(["{B}", "{C}"], run.Windows.Select(w => w.ServiceWindowID).ToArray());
        Assert.DoesNotContain("Remove-CMMaintenanceWindow", run.Log);
    }

    [Fact]
    public void RemoveOfAWindowThatSharesItsNameIsRefusedWhenItCannotBeRemovedById()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target, W("Patch", "{B}")], Expected(target, "Remove"), descriptionWritable: false);

        Assert.Equal("duplicate-name", run.Status);
        Assert.Equal(2, run.Windows.Count);
        Assert.Empty(run.Log);
    }

    [Fact]
    public void RemoveOfAWindowWithAUniqueNameUsesTheCmdlet()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target, W("Other", "{C}")], Expected(target, "Remove"), descriptionWritable: false);

        Assert.Equal("ok", run.Status);
        Assert.Equal(["{C}"], run.Windows.Select(w => w.ServiceWindowID).ToArray());
        Assert.Equal(["Remove-CMMaintenanceWindow"], run.Log);
    }

    [Fact]
    public void CreateWritesTheDescriptionAfterConfigMgrWroteItsOwnText()
    {
        var parameters = new Dictionary<string, object>(Schedule) { ["Operation"] = "Create", ["CollectionId"] = "C1", ["Name"] = "New", ["Description"] = "Owned by the patch team" };
        var run = Invoke([], parameters);

        Assert.Equal("ok", run.Status);
        Assert.Equal("Owned by the patch team", Assert.Single(run.Windows).Description);
        Assert.Equal("Owned by the patch team", run.Result.GetProperty("Window").GetProperty("Description").GetString());
    }

    [Fact]
    public void UpdateWritesTheDescriptionBackAfterTheScheduleChange()
    {
        var target = W("Patch", "{A}", "Owned by the patch team");
        var parameters = new Dictionary<string, object>(Expected(target, "Update")) { ["Description"] = "Owned by the patch team" };
        foreach (var (k, v) in Schedule) parameters[k] = v;
        var run = Invoke([target], parameters);

        Assert.Equal("ok", run.Status);
        Assert.Equal("Owned by the patch team", Assert.Single(run.Windows).Description);
    }

    [Fact]
    public void ADescriptionThatCannotBeWrittenStopsTheChangeBeforeAnyWrite()
    {
        var create = new Dictionary<string, object>(Schedule) { ["Operation"] = "Create", ["CollectionId"] = "C1", ["Name"] = "New", ["Description"] = "Mine", ["RequireDescription"] = true };
        var run = Invoke([], create, descriptionWritable: false);
        Assert.Equal("description-unsupported", run.Status);
        Assert.Empty(run.Windows);
        Assert.Empty(run.Log);

        var target = W("Patch", "{A}", "Mine");
        var remove = new Dictionary<string, object>(Expected(target, "Remove")) { ["RequireDescription"] = true };
        run = Invoke([target], remove, descriptionWritable: false);
        Assert.Equal("description-unsupported", run.Status);
        Assert.Single(run.Windows);
        Assert.Empty(run.Log);
    }

    [Fact]
    public void CheckReadsTheStateAndWritesNothing()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target], Expected(target, "Check"));
        Assert.Equal("ok", run.Status);
        Assert.Equal("{A}", run.Result.GetProperty("Window").GetProperty("ServiceWindowId").GetString());
        Assert.Empty(run.Log);

        run = Invoke([target with { IsEnabled = false }], Expected(target, "Check"));
        Assert.Equal("changed", run.Status);

        run = Invoke([], Expected(target, "Check"));
        Assert.Equal("window-missing", run.Status);
    }

    private static Dictionary<string, object> CreateWithDescription() =>
        new(Schedule) { ["Operation"] = "Create", ["CollectionId"] = "C1", ["Name"] = "New", ["Description"] = "Owned by the patch team" };

    [Fact]
    public void AnAccessDeniedOnTheDescriptionAfterTheCreateReportsTheNewWindow()
    {
        var run = Invoke([], CreateWithDescription(), options: new { PutThrows = true });

        Assert.Equal("description-failed", run.Status);
        var created = Assert.Single(run.Windows);
        Assert.Equal(created.ServiceWindowID, run.Result.GetProperty("Window").GetProperty("ServiceWindowId").GetString());
    }

    [Fact]
    public void AnErrorAfterTheCmdletMadeTheChangeIsReportedAsPartialWithTheWindow()
    {
        var run = Invoke([], CreateWithDescription(), options: new { NewThrows = true });

        Assert.Equal("partial", run.Status);
        Assert.Equal("New", run.Result.GetProperty("Window").GetProperty("Name").GetString());
        Assert.Single(run.Windows);
    }

    [Fact]
    public void APartialWritePreservesWhetherTheFailureWasAccessDenied()
    {
        var run = Invoke([], CreateWithDescription(), options: new { NewThrows = true });

        Assert.Equal("partial", run.Status);
        Assert.True(run.Result.GetProperty("AccessDenied").GetBoolean());
    }

    [Fact]
    public void TheGuardComparesTheDescriptionWhenItIsGiven()
    {
        var target = W("Patch", "{A}", "Mine");
        var check = new Dictionary<string, object>(Expected(target, "Check")) { ["ExpectedDescription"] = "Mine" };
        Assert.Equal("ok", Invoke([target], check).Status);

        check["ExpectedDescription"] = "Other";
        Assert.Equal("changed", Invoke([target], check).Status);
    }

    [Fact]
    public void ARemoveByNameStopsWhenTheWindowsChangedBeforeTheCmdlet()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target], Expected(target, "Remove"), descriptionWritable: false, options: new { ChangeOnRead = 2 });

        Assert.Equal("changed", run.Status);
        Assert.DoesNotContain("Remove-CMMaintenanceWindow", run.Log);
        Assert.Contains(run.Windows, w => w.ServiceWindowID == "{A}");
    }

    [Fact]
    public void ARemoveByIdStopsWhenTheWindowListChangedBeforeTheWrite()
    {
        var target = W("Patch", "{A}");
        var run = Invoke([target, W("PATCH", "{B}")], Expected(target, "Remove"), options: new { ChangeOnSettingsRead = true });

        Assert.Equal("changed", run.Status);
        Assert.DoesNotContain("Put", run.Log);
        Assert.Contains(run.Windows, w => w.ServiceWindowID == "{A}");
    }

    [Fact]
    public void ADescriptionWriteStopsWhenTheWindowListChangedBeforeTheWrite()
    {
        var run = Invoke([], CreateWithDescription(), options: new { ChangeOnSettingsRead = true });

        Assert.Equal("description-failed", run.Status);
        Assert.DoesNotContain("Put", run.Log);
        Assert.Equal("Occurs ConfigMgr text", run.Windows.Single(w => w.Name == "New").Description);
    }
}
