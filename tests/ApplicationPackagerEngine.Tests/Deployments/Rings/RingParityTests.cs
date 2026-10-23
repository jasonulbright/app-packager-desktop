using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>
/// The plan validation, expansion, row rules, Promote shift and threshold against the suite tool's
/// own functions (ConvertTo-RingPlan, Expand-RingPlan, Test-RingExpansion, Get-RingPromoteShift,
/// Test-RingThreshold), run read-only in a child Windows PowerShell. Without the suite tool the
/// fixed expected values below stand in for its results; the tests assert which ran. The cases
/// where the two differ on purpose are listed in <see cref="Differences"/> and asserted both ways.
/// </summary>
public sealed class RingParityTests
{
    private const string SourceRoot = @"C:\projects\deployment-helper";
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0);

    private sealed record PlanCase(string Name, object[] Rings, bool SourceLoads, bool OursLoads);

    private static object R(string name, string id = "", string purpose = "Required", double available = 0, double? deadline = 1, string notification = "DisplayAll",
        double? threshold = null, string slow = "DoNotRunProgram") =>
        new { Name = name, CollectionID = id, Purpose = purpose, AvailableOffsetDays = available, DeadlineOffsetDays = deadline, UserNotification = notification, SuccessThresholdPercent = threshold, SlowNetworkOption = slow };

    private static readonly PlanCase[] Plans =
    [
        new("valid", [R("QA", "MCM00001"), R("Pilot", "MCM00002", available: 2, deadline: 5)], true, true),
        new("seed shape", [R("QA"), R("Pilot", available: 2, deadline: 5)], true, true),
        new("built-in", [R("QA", "SMS00001")], false, false),
        new("built-in SMSDM", [R("QA", "SMSDM003")], false, false),
        new("short id", [R("QA", "MCM1")], false, false),
        new("shared", [R("QA", "MCM00001"), R("Pilot", "MCM00001", available: 2, deadline: 5)], false, false),
        new("no deadline", [R("QA", deadline: null)], false, false),
        new("deadline equals available", [R("QA", available: 2, deadline: 2)], false, false),
        new("deadline order", [R("QA", "MCM00001", deadline: 5), R("Pilot", "MCM00002", available: 1, deadline: 5)], false, false),
        new("available skipped in order", [R("QA", deadline: 5), R("Wide", purpose: "Available", available: 1, deadline: null), R("Prod", available: 6, deadline: 7)], true, true),
        new("available hide all", [R("QA", purpose: "Available", deadline: null, notification: "HideAll")], false, false),
        new("required hide all", [R("QA", notification: "HideAll")], true, true),
        new("bad purpose", [R("QA", purpose: "Optional")], false, false),
        new("bad notification", [R("QA", notification: "Loud")], false, false),
        new("threshold 101", [R("QA", threshold: 101)], false, false),
        new("threshold 0", [R("QA", threshold: 0)], true, true),
        new("negative available", [R("QA", available: -1, deadline: 1)], false, false),
        new("no rings", [], false, false),
        // Differences by design: CM01 refuses this slow network value; a fraction of a day is refused at load; the cap of 100 rings.
        new("slow run from DP", [R("QA", slow: "RunProgramFromDistributionPoint")], true, false),
        new("101 rings", [.. Enumerable.Range(1, 101).Select(i => R("R" + i, purpose: "Available", available: i, deadline: null))], true, false)
    ];

    /// <summary>Available and deadline offsets in hours from now, purpose, notification; the suite verdict (blocked, warned).</summary>
    private static readonly (string Purpose, double Available, double? Deadline, string Notification, bool Blocked, bool Warned)[] RowCases =
    [
        ("Required", 1, 24, "DisplayAll", false, false),
        ("Required", 5, 5, "DisplayAll", true, false),
        ("Required", -48, -1, "DisplayAll", true, true),
        ("Required", -48, 0, "DisplayAll", true, true),
        ("Required", -48, 1.0 / 60, "DisplayAll", false, true),
        ("Required", 1, null, "DisplayAll", true, false),
        ("Available", 1, null, "HideAll", true, false),
        ("Available", -4.0 / 60, null, "DisplayAll", false, false),
        ("Available", -0.1, null, "DisplayAll", false, true),
        ("Available", 200, null, "DisplaySoftwareCenterOnly", false, false)
    ];

    private static readonly (string Available, string? Deadline, string Now, int Minutes)[] ShiftCases =
    [
        ("2026-10-20T08:00:00", "2026-10-22T08:00:00", "2026-10-20T12:00:00", 0),
        ("2026-10-18T08:00:00", null, "2026-10-20T12:00:00", 0),
        ("2026-10-18T08:00:00", "2026-10-20T12:00:00", "2026-10-20T12:00:00", 3121),
        ("2026-10-18T08:00:00", "2026-10-19T08:00:00", "2026-10-20T12:00:30", 3121),
        ("2026-10-18T08:00:30", "2026-10-19T08:00:00", "2026-10-20T12:00:00", 3121),
        ("2026-10-20T11:00:00", "2026-10-20T11:30:00", "2026-10-20T12:00:00", 61),
        ("2026-10-20T11:59:00", "2026-10-20T11:59:30", "2026-10-20T12:00:00", 2)
    ];

    private static readonly (bool Summarized, int Targeted, int Success, int? Threshold, bool Met, double? Percent)[] ThresholdCases =
    [
        (true, 10, 9, null, true, null), (false, 0, 0, 90, false, null), (true, 0, 0, 90, false, 0), (true, 10, 8, 90, false, 80), (true, 10, 9, 90, true, 90),
        (true, 3, 2, 66, true, 66.7), (true, 7, 6, 86, false, 85.7), (true, 5, 0, 0, true, 0), (true, 8, 7, 87, true, 87.5)
    ];

    private static readonly (double Available, double? Deadline, string Start, string ExpectAvailable, string? ExpectDeadline)[] ExpandCases =
    [
        (0, 1, "2026-10-21T09:00:00", "2026-10-21T09:00:00", "2026-10-22T09:00:00"),
        (7, 10, "2026-10-21T09:00:00", "2026-10-28T09:00:00", "2026-10-31T09:00:00"),
        (10, 12, "2026-10-25T23:30:00", "2026-11-04T23:30:00", "2026-11-06T23:30:00"),
        (1, 2, "2028-02-28T06:00:00", "2028-02-29T06:00:00", "2028-03-01T06:00:00"),
        (2, null, "2026-10-21T09:00:00", "2026-10-23T09:00:00", null)
    ];

    private sealed record SourceResults(bool Ran, List<bool> PlanLoads, List<List<string>> PlanErrors, List<JsonElement> Rows, List<int> Shifts, List<JsonElement> Thresholds, List<JsonElement> Expand);

    private static SourceResults? RunSource()
    {
        if (!File.Exists(Path.Combine(SourceRoot, @"Module\DeploymentHelperCommon.psm1"))) return null;
        var folder = Path.Combine(Path.GetTempPath(), "apkg-ringparity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cases = JsonSerializer.Serialize(new
            {
                Now = Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                Plans = Plans.Select(p => new { p.Name, Rings = p.Rings }),
                Rows = RowCases.Select(c => new { c.Purpose, c.Available, c.Deadline, c.Notification }),
                Shifts = ShiftCases.Select(c => new { c.Available, c.Deadline, c.Now }),
                Thresholds = ThresholdCases.Select(c => new { c.Summarized, c.Targeted, c.Success, c.Threshold }),
                Expand = ExpandCases.Select(c => new { c.Available, c.Deadline, c.Start })
            });
            File.WriteAllText(Path.Combine(folder, "cases.json"), cases);
            var script = Path.Combine(folder, "parity.ps1");
            File.WriteAllText(script, """
                param([string]$Source, [string]$Folder)
                $ErrorActionPreference = 'Stop'
                $cases = Get-Content -LiteralPath (Join-Path $Folder 'cases.json') -Raw | ConvertFrom-Json
                Import-Module (Join-Path $Source 'Module\DeploymentHelperCommon.psd1') -Force -DisableNameChecking 3>$null
                function global:Write-Log { param($Message, $Level) }
                $fmt = 'yyyy-MM-ddTHH:mm:ss'
                $inv = [Globalization.CultureInfo]::InvariantCulture
                $now = [datetime]::ParseExact($cases.Now, $fmt, $inv)
                $loads = @(); $errors = @()
                foreach ($p in $cases.Plans) {
                    $r = ConvertTo-RingPlan -InputObject @{ Name = $p.Name; TimeBasedOn = 'LocalTime'; Rings = @($p.Rings) }
                    $loads += (@($r.Errors).Count -eq 0); $errors += ,@($r.Errors)
                }
                $rows = @(foreach ($c in $cases.Rows) {
                    $row = [pscustomobject]@{ Index = 1; Name = 'R'; CollectionID = 'MCM00001'; Purpose = $c.Purpose; UserNotification = $c.Notification
                        AvailableDateTime = $now.AddHours([double]$c.Available); DeadlineDateTime = $(if ($null -ne $c.Deadline) { $now.AddHours([double]$c.Deadline) } else { $null }) }
                    $x = Test-RingExpansion -Rings @($row) -Now $now
                    [pscustomobject]@{ Blocked = (@($x.Errors).Count -gt 0); Warned = (@($x.Warnings).Count -gt 0) }
                })
                $shifts = @(foreach ($c in $cases.Shifts) { [int](Get-RingPromoteShift -Ring ([pscustomobject]@{ AvailableDateTime = $c.Available; DeadlineDateTime = $c.Deadline }) -Now ([datetime]::ParseExact($c.Now, $fmt, $inv))) })
                $thresholds = @(foreach ($c in $cases.Thresholds) {
                    $t = Test-RingThreshold -Summary ([pscustomobject]@{ Found = $true; Summarized = [bool]$c.Summarized; Targeted = [int]$c.Targeted; Success = [int]$c.Success }) -ThresholdPercent $c.Threshold
                    [pscustomobject]@{ Met = [bool]$t.Met; Percent = $t.Percent }
                })
                $expand = @(foreach ($c in $cases.Expand) {
                    $plan = (ConvertTo-RingPlan -InputObject @{ Name = 'E'; Rings = @(@{ Name = 'R'; Purpose = $(if ($null -ne $c.Deadline) { 'Required' } else { 'Available' }); AvailableOffsetDays = $c.Available; DeadlineOffsetDays = $c.Deadline }) }).Plan
                    $e = (Expand-RingPlan -Plan $plan -Start ([datetime]::ParseExact($c.Start, $fmt, $inv)))[0]
                    [pscustomobject]@{ Available = $e.AvailableDateTime.ToString($fmt, $inv); Deadline = $(if ($e.DeadlineDateTime) { $e.DeadlineDateTime.ToString($fmt, $inv) } else { $null }) }
                })
                [pscustomobject]@{ Ran = $true; PlanLoads = $loads; PlanErrors = $errors; Rows = $rows; Shifts = $shifts; Thresholds = $thresholds; Expand = $expand } |
                    ConvertTo-Json -Depth 6 -Compress | Set-Content -LiteralPath (Join-Path $Folder 'out.json') -Encoding UTF8
                """);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Source", SourceRoot, "-Folder", folder }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The suite module did not finish.");
            var output = Path.Combine(folder, "out.json");
            Assert.True(File.Exists(output), "The suite module wrote no result: " + error.Result);
            return JsonSerializer.Deserialize<SourceResults>(File.ReadAllText(output).TrimStart('\uFEFF'))!;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static readonly Lazy<SourceResults?> Source = new(RunSource);

    private static void AssertSourceUsed(SourceResults? source)
    {
        var present = File.Exists(Path.Combine(SourceRoot, @"Module\DeploymentHelperCommon.psm1"));
        Assert.True(present == (source?.Ran == true), present ? "The suite tool is on this computer but its results were not used." : "The suite tool is not on this computer, so the fixed cases must be used.");
    }

    private static RingDefinition Ours(JsonElement r) => new(
        r.GetProperty("Name").GetString()!, r.GetProperty("CollectionID").GetString()!, "", r.GetProperty("Purpose").GetString()!,
        (int)r.GetProperty("AvailableOffsetDays").GetDouble(), r.GetProperty("DeadlineOffsetDays").ValueKind == JsonValueKind.Null ? null : (int)r.GetProperty("DeadlineOffsetDays").GetDouble(),
        r.GetProperty("UserNotification").GetString()!, false, false, false, "",
        r.GetProperty("SuccessThresholdPercent").ValueKind == JsonValueKind.Null ? null : (int)r.GetProperty("SuccessThresholdPercent").GetDouble(),
        null, r.GetProperty("SlowNetworkOption").GetString(), null);

    [Fact]
    public void PlanValidationAgreesWithTheSuiteToolExceptTheListedDifferences()
    {
        var source = Source.Value;
        AssertSourceUsed(source);
        for (var i = 0; i < Plans.Length; i++)
        {
            var c = Plans[i];
            if (source is not null) Assert.True(c.SourceLoads == source.PlanLoads[i], $"{c.Name}: the suite tool {(source.PlanLoads[i] ? "loads" : "refuses")} it: {string.Join(" | ", source.PlanErrors[i])}");
            var rings = JsonSerializer.SerializeToElement(c.Rings).EnumerateArray().Select(Ours).ToList();
            var errors = RingPlanRules.Validate(new RingPlan(c.Name, "", false, "LocalTime", rings, false));
            Assert.True(c.OursLoads == (errors.Count == 0), $"{c.Name}: {string.Join(" | ", errors)}");
            if (source is not null && !c.SourceLoads && !c.OursLoads)
                Assert.Contains(errors, e => source.PlanErrors[i].Any(s => s.Split(' ').Take(3).SequenceEqual(e.Split(' ').Take(3))));
        }
    }

    [Fact]
    public void TheRowRulesShiftThresholdAndExpansionAgreeWithTheSuiteTool()
    {
        var source = Source.Value;
        AssertSourceUsed(source);
        for (var i = 0; i < RowCases.Length; i++)
        {
            var c = RowCases[i];
            var row = new RingExpansionRow(1, "R", "MCM00001", "", 0, c.Purpose, Now.AddHours(c.Available), c.Deadline is { } d ? Now.AddHours(d) : null, "", null, [])
            { Definition = new RingDefinition("R", "MCM00001", "", c.Purpose, 0, null, c.Notification, false, false, false, "", null, null, null, null) };
            var ours = RingPlanRules.CheckRows([row], Now);
            var expected = source is null ? (c.Blocked, c.Warned) : (source.Rows[i].GetProperty("Blocked").GetBoolean(), source.Rows[i].GetProperty("Warned").GetBoolean());
            Assert.Equal((c.Blocked, c.Warned), expected);
            Assert.True((ours.Blocked, ours.Warnings.Count > 0) == expected, $"row case {i}");
        }
        for (var i = 0; i < ShiftCases.Length; i++)
        {
            var c = ShiftCases[i];
            var expected = source?.Shifts[i] ?? c.Minutes;
            Assert.Equal(c.Minutes, expected);
            Assert.Equal(expected, RingPlanRules.PromoteShift(Parse(c.Available), c.Deadline is null ? null : Parse(c.Deadline), Parse(c.Now)));
        }
        for (var i = 0; i < ThresholdCases.Length; i++)
        {
            var c = ThresholdCases[i];
            var counts = new RingLiveCounts(1, "", c.Summarized ? RingLiveStates.Summarized : RingLiveStates.Found, c.Targeted, c.Success, 0, 0, 0, c.Summarized, "");
            var ours = RingPlanRules.Threshold(counts, c.Threshold);
            var met = source is null ? c.Met : source.Thresholds[i].GetProperty("Met").GetBoolean();
            var percent = source is null ? c.Percent : source.Thresholds[i].GetProperty("Percent") is { ValueKind: JsonValueKind.Number } p ? p.GetDouble() : null;
            Assert.Equal((c.Met, c.Percent), (met, percent));
            Assert.Equal((met, percent), (ours.Met, ours.Percent));
        }
        for (var i = 0; i < ExpandCases.Length; i++)
        {
            var c = ExpandCases[i];
            var purpose = c.Deadline is null ? "Available" : "Required";
            var row = RingPlanRules.Expand(new RingPlan("E", "", false, "LocalTime",
                [new RingDefinition("R", "", "", purpose, (int)c.Available, c.Deadline is { } dl ? (int)dl : null, "DisplayAll", false, false, false, "", null, null, null, null)], false),
                Parse(c.Start), TimeZoneInfo.Utc).Single();
            var expected = source is null ? (c.ExpectAvailable, c.ExpectDeadline) : (source.Expand[i].GetProperty("Available").GetString()!, source.Expand[i].GetProperty("Deadline").GetString());
            Assert.Equal((c.ExpectAvailable, c.ExpectDeadline), expected);
            Assert.Equal(expected, (Text(row.AvailableTime), row.Deadline is { } d ? Text(d) : null));
        }
    }

    [Fact]
    public async Task EachToolReadsTheOthersRunFiles()
    {
        if (!File.Exists(Path.Combine(SourceRoot, @"Module\DeploymentHelperCommon.psm1")))
        {
            Assert.False(Directory.Exists(SourceRoot) && File.Exists(Path.Combine(SourceRoot, "start-deploymenthelper.ps1")), "The suite tool is on this computer but its module is missing.");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "apkg-ringcompat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new RingRunStore(() => folder, utcNow: () => DeploymentHarness.UtcNow, local: DeploymentHarness.Local, user: () => @"CONTOSO\Admin");
            var ours = store.Create(RingRunStoreTests.NewRun(rings: 4), "MCM0001B");
            ours = store.Save(ours with
            {
                Rings = [.. ours.Rings.Select(r => r.Index switch
                {
                    1 => r with { Status = RingStatuses.Created, DeploymentId = "MCM20040" },
                    4 => r with { Status = RingStatuses.Cancelled },
                    _ => r
                })]
            });
            var script = Path.Combine(folder, "compat.ps1");
            File.WriteAllText(script, """
                param([string]$Source, [string]$Folder, [string]$Ours)
                $ErrorActionPreference = 'Stop'
                Import-Module (Join-Path $Source 'Module\DeploymentHelperCommon.psd1') -Force -DisableNameChecking 3>$null
                function global:Write-Log { param($Message, $Level) }
                $run = Read-RingRun -Path (Join-Path $Folder $Ours)
                $next = Get-RingRunNextHeld -Run $run
                $promotable = Test-RingRunPromotable -Run $run -RingIndex 2
                $finished = Test-RingRunFinished -Run $run
                $rings = @(@{ Index = 1; Name = 'QA'; CollectionID = 'MCM00098'; CollectionName = 'DH-Test-1'; Purpose = 'Required'; AvailableOffsetDays = 0; DeadlineOffsetDays = 1; UserNotification = 'HideAll'
                    AvailableDateTime = [datetime]'2026-10-21 09:00'; DeadlineDateTime = [datetime]'2026-10-22 09:00'; DPGroup = ''; SuccessThresholdPercent = 95 } | ForEach-Object { [pscustomobject]$_ })
                $suite = New-RingRun -PlanName 'Server-Rings' -Object ([ordered]@{ Type = 'SUG'; ID = '16778893'; Name = '202609-W11-25H2'; ProgramName = $null }) -Rings $rings -RunId 'suite-run' -CreatedAt ([datetime]'2026-10-20 08:00')
                $path = New-RingRunFile -Run $suite -Folder $Folder -FileName 'Server-Rings_16778893_20261020-080000.json'
                [pscustomobject]@{ Next = $next; Promotable = [bool]$promotable.Ok; Reason = $promotable.Reason; Finished = [bool]$finished; Statuses = @($run.Rings | ForEach-Object { $_.Status }); SuiteFile = (Split-Path $path -Leaf) } |
                    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $Folder 'out.json') -Encoding UTF8
                """);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Source", SourceRoot, "-Folder", folder, "-Ours", ours.FileName }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000));
            Assert.True(File.Exists(Path.Combine(folder, "out.json")), "The suite module wrote no result: " + await error);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "out.json")).TrimStart('﻿'));
            var r = result.RootElement;
            Assert.Equal(2, r.GetProperty("Next").GetInt32());
            Assert.True(r.GetProperty("Promotable").GetBoolean(), r.GetProperty("Reason").GetString());
            Assert.False(r.GetProperty("Finished").GetBoolean());
            Assert.Equal(["Created", "Held", "Held", "Cancelled"], r.GetProperty("Statuses").EnumerateArray().Select(s => s.GetString()));

            var suite = store.Read(r.GetProperty("SuiteFile").GetString()!);
            Assert.Equal(("suite-run", DeploymentObjectType.SoftwareUpdateGroup, "16778893", true), (suite.RunId, suite.ObjectType, suite.ObjectId, suite.HoldLaterRings));
            var ring = Assert.Single(suite.Rings);
            Assert.Equal((RingStatuses.Held, new DateTime(2026, 10, 21, 9, 0, 0), (DateTime?)new DateTime(2026, 10, 22, 9, 0, 0), 95, "HideAll"),
                (ring.Status, ring.AvailableTime, ring.Deadline, ring.SuccessThresholdPercent, ring.Definition!.UserNotification));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static DateTime Parse(string text) => DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Text(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The differences from the suite tool that the plan cases above pin, with their reasons.</summary>
    public static readonly IReadOnlyList<string> Differences =
    [
        "Slow network RunProgramFromDistributionPoint: the suite tool loads it, ConfigMgr refuses it at create; refused at plan load.",
        "More than 100 rings: the suite tool has no limit; refused with the cap.",
        "A fraction of a day in an offset: the suite tool expands it; refused at plan load (whole days)."
    ];
}
