using System.Diagnostics;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The schedule, notification, built-in collection, audit record and template rules against the
/// suite tool's own functions, run read-only in a child Windows PowerShell. Without the suite
/// tool the fixed expected values below stand in for its results; the test asserts which ran.
/// </summary>
public sealed class DeploymentParityTests
{
    private const string SourceRoot = @"C:\projects\deployment-helper";
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0);

    /// <summary>Purpose, available and deadline in minutes from now, and the suite tool's verdict (Ok, Level).</summary>
    public static readonly (string Purpose, int Available, int? Deadline, bool Ok, string Level)[] ScheduleCases =
    [
        ("Available", 60, null, true, "Info"),
        ("Available", -4, null, true, "Info"),
        ("Available", -5, null, true, "Info"),
        ("Available", -6, null, true, "Warn"),
        ("Available", -600, null, true, "Warn"),
        ("Available", 60 * 24 * 365 * 6, null, true, "Warn"),
        ("Required", 60, null, false, "Error"),
        ("Required", 60, 60, false, "Error"),
        ("Required", 60, 59, false, "Error"),
        ("Required", 60, 61, true, "Info"),
        ("Required", -120, -60, false, "Error"),
        ("Required", -120, 0, false, "Error"),
        ("Required", -120, 1, true, "Warn"),
        ("Required", -6, 600, true, "Warn")
    ];

    public static readonly (string Purpose, string Notification, bool Ok)[] NotificationCases =
    [
        ("Available", "HideAll", false), ("Available", "DisplayAll", true), ("Available", "DisplaySoftwareCenterOnly", true),
        ("Required", "HideAll", true), ("Required", "DisplayAll", true)
    ];

    public static readonly (string Id, bool BuiltIn)[] CollectionCases =
        [("SMS00001", true), ("SMSDM003", true), ("sms000us", true), ("MCM00098", false), (" SMS00004 ", true), ("", false), ("XSMS0001", false)];

    private sealed record SourceResults(bool Ran, List<JsonElement> Schedule, List<bool> Notification, List<bool> BuiltIn, string[] RecordFields,
        List<JsonElement> Templates, bool ProbeWithReader, bool AppendWithReader, bool ProbeWithSharingReader, bool AppendWithSharingReader);

    private static SourceResults? RunSource()
    {
        if (!File.Exists(Path.Combine(SourceRoot, @"Module\DeploymentHelperCommon.psm1")) || !File.Exists(Path.Combine(SourceRoot, "start-deploymenthelper.ps1"))) return null;
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dhparity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cases = JsonSerializer.Serialize(new
            {
                Now = Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Schedule = ScheduleCases.Select(c => new { c.Purpose, c.Available, c.Deadline }),
                Notification = NotificationCases.Select(c => new { c.Purpose, c.Notification }),
                Collections = CollectionCases.Select(c => c.Id)
            });
            File.WriteAllText(Path.Combine(folder, "cases.json"), cases);
            var script = Path.Combine(folder, "parity.ps1");
            File.WriteAllText(script, """
                param([string]$Source, [string]$Folder)
                $ErrorActionPreference = 'Stop'
                $cases = Get-Content -LiteralPath (Join-Path $Folder 'cases.json') -Raw | ConvertFrom-Json
                Import-Module (Join-Path $Source 'Module\DeploymentHelperCommon.psd1') -Force -DisableNameChecking 3>$null
                function global:Write-Log { param($Message, $Level) }
                $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Source 'start-deploymenthelper.ps1'), [ref]$null, [ref]$null)
                foreach ($name in 'Test-DeploymentSchedule', 'Test-DeploymentNotification') {
                    $fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true) | Select-Object -First 1
                    . ([scriptblock]::Create($fn.Extent.Text))
                }
                $now = [datetime]::ParseExact($cases.Now, 'yyyy-MM-ddTHH:mm:ss', $null)
                $schedule = @(foreach ($c in $cases.Schedule) {
                    $deadline = if ($null -ne $c.Deadline) { $now.AddMinutes([int]$c.Deadline) } else { $null }
                    $r = Test-DeploymentSchedule -Available $now.AddMinutes([int]$c.Available) -Deadline $deadline -Purpose $c.Purpose -Now $now
                    [pscustomobject]@{ Ok = [bool]$r.Ok; Level = [string]$r.Level }
                })
                $notification = @(foreach ($c in $cases.Notification) { [bool](Test-DeploymentNotification -Purpose $c.Purpose -Notification $c.Notification).Ok })
                $builtIn = @(foreach ($id in $cases.Collections) { [bool](Test-CollectionIdBuiltIn -CollectionId $id) })
                $log = Join-Path $Folder 'audit.jsonl'
                $null = Write-DeploymentLog -LogPath $log -Record @{ DeploymentType = 'Package'; ApplicationName = 'Pkg'; ApplicationVersion = 'Run'; CollectionName = 'C'; CollectionID = 'MCM00098'; MemberCount = 0; DeployPurpose = 'Available'; DeadlineDateTime = $null; DeploymentID = 'MCM20015'; Result = 'Success' }
                $fields = @((Get-Content -LiteralPath $log -TotalCount 1 | ConvertFrom-Json).PSObject.Properties.Name)
                $reader = [System.IO.File]::Open($log, 'Open', 'Read', 'Read')
                $probe = [bool](Test-DeploymentLogWritable -LogPath $log).Success
                $append = [bool](Write-DeploymentLog -LogPath $log -Record @{ Result = 'Success' }).Success
                $reader.Dispose()
                $reader = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
                $probeShared = [bool](Test-DeploymentLogWritable -LogPath $log).Success
                $appendShared = [bool](Write-DeploymentLog -LogPath $log -Record @{ Result = 'Success' }).Success
                $reader.Dispose()
                $templates = @(Get-ChildItem -LiteralPath (Join-Path $Source 'Templates') -Filter '*.json' | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
                [pscustomobject]@{ Ran = $true; Schedule = $schedule; Notification = $notification; BuiltIn = $builtIn; RecordFields = $fields; Templates = $templates
                    ProbeWithReader = $probe; AppendWithReader = $append; ProbeWithSharingReader = $probeShared; AppendWithSharingReader = $appendShared } |
                    ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath (Join-Path $Folder 'out.json') -Encoding UTF8
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

    private static DeploymentOptions Options(string purpose, int available, int? deadline, string notification = "DisplayAll") =>
        DeploymentScheduleRulesTests.Options(purpose, Now.AddMinutes(available), deadline is { } d ? Now.AddMinutes(d) : null, notification);

    [Fact]
    public void TheScheduleRulesGiveTheSuiteToolsVerdicts()
    {
        var source = Source.Value;
        for (var i = 0; i < ScheduleCases.Length; i++)
        {
            var c = ScheduleCases[i];
            var expected = source is null ? (c.Ok, c.Level) : (source.Schedule[i].GetProperty("Ok").GetBoolean(), source.Schedule[i].GetProperty("Level").GetString()!);
            Assert.Equal((c.Ok, c.Level), expected);
            var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Package, Options(c.Purpose, c.Available, c.Deadline), Now);
            var level = verdict.Blocked ? "Error" : verdict.Warnings.Count > 0 ? "Warn" : "Info";
            Assert.True((!verdict.Blocked, level) == expected, $"Case {i}: ours {(!verdict.Blocked, level)}, suite {expected}.");
        }
        AssertSourceUsed(source);
    }

    /// <summary>The suite tool ran when it is on this computer; otherwise the fixed cases above stood in for it.</summary>
    private static void AssertSourceUsed(SourceResults? source)
    {
        var present = File.Exists(Path.Combine(SourceRoot, @"Module\DeploymentHelperCommon.psm1")) && File.Exists(Path.Combine(SourceRoot, "start-deploymenthelper.ps1"));
        Assert.True(present == (source?.Ran == true), present ? "The suite tool is on this computer but its results were not used." : "The suite tool is not on this computer, so the fixed cases must be used.");
    }

    [Fact]
    public void TheNotificationAndBuiltInRulesMatchTheSuiteTool()
    {
        var source = Source.Value;
        for (var i = 0; i < NotificationCases.Length; i++)
        {
            var c = NotificationCases[i];
            if (source is not null) Assert.Equal(c.Ok, source.Notification[i]);
            var deadline = c.Purpose == "Required" ? 120 : (int?)null;
            foreach (var type in new[] { DeploymentObjectType.Application, DeploymentObjectType.SoftwareUpdateGroup })
                Assert.Equal(c.Ok, !DeploymentScheduleRules.Check(type, Options(c.Purpose, 60, deadline, c.Notification), Now).Blocked);
        }
        for (var i = 0; i < CollectionCases.Length; i++)
        {
            var c = CollectionCases[i];
            if (source is not null) Assert.Equal(c.BuiltIn, source.BuiltIn[i]);
            Assert.Equal(c.BuiltIn, c.Id.Length > 0 && DeploymentService.IsBuiltInCollection(c.Id));
        }
        AssertSourceUsed(source);
    }

    [Fact]
    public void AuditRecordsStartWithTheSuiteFieldsInTheSuiteOrder()
    {
        string[] suite = ["Timestamp", "User", "DeploymentType", "ApplicationName", "ApplicationVersion", "CollectionName", "CollectionID", "MemberCount", "DeployPurpose", "DeployAction", "DeadlineDateTime", "DeploymentID", "Result"];
        if (Source.Value is { } source) Assert.Equal(suite, source.RecordFields);
        AssertSourceUsed(Source.Value);
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dhparity-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DeploymentAuditLog(() => Path.Combine(folder, "a.jsonl"));
            log.Append(DeploymentAuditLogTests.Record());
            using var json = JsonDocument.Parse(File.ReadAllLines(Path.Combine(folder, "a.jsonl"))[0]);
            Assert.Equal(suite, json.RootElement.EnumerateObject().Select(p => p.Name).Take(suite.Length));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    // Measured: in Windows PowerShell 5.1 the suite's probe (FileShare.None) and Add-Content both fail
    // while any reader holds the log, also one that shares ReadWrite, so its probe catches the reader.
    [Fact]
    public void TheSuiteProbeAndAppendBehaveLikeTheseWithAReaderHoldingTheLog()
    {
        var source = Source.Value;
        AssertSourceUsed(source);
        if (source is not null)
        {
            Assert.Equal((false, false), (source.ProbeWithReader, source.AppendWithReader));
            Assert.Equal((false, false), (source.ProbeWithSharingReader, source.AppendWithSharingReader));
        }
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dhparity-reader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "a.jsonl");
            var log = new DeploymentAuditLog(() => path);
            Assert.True(log.Append(DeploymentAuditLogTests.Record()).Succeeded);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Equal((false, false), (log.Probe().Writable, log.Append(DeploymentAuditLogTests.Record()).Succeeded));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheSeedsEqualTheSuiteTemplateFiles()
    {
        var source = Source.Value;
        var seeds = DeploymentTemplateStore.Seeds.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        AssertSourceUsed(source);
        if (source is null)
        {
            Assert.Equal(["Server Pilot", "Server Production", "Workstation Pilot", "Workstation Production"], seeds.Select(s => s.Name));
            return;
        }
        var files = source.Templates.OrderBy(t => t.GetProperty("Name").GetString(), StringComparer.Ordinal).ToList();
        Assert.Equal(seeds.Count, files.Count);
        for (var i = 0; i < seeds.Count; i++)
        {
            var f = files[i];
            var s = seeds[i];
            Assert.Equal(
                (f.GetProperty("Name").GetString(), f.GetProperty("DeployPurpose").GetString(), f.GetProperty("UserNotification").GetString(), f.GetProperty("TimeBasedOn").GetString() == "Utc",
                 f.GetProperty("OverrideServiceWindow").GetBoolean(), f.GetProperty("RebootOutsideServiceWindow").GetBoolean(), f.GetProperty("AllowMeteredConnection").GetBoolean(),
                 f.GetProperty("AllowBoundaryFallback").GetBoolean(), f.GetProperty("AllowMicrosoftUpdate").GetBoolean(), f.GetProperty("RequirePostRebootFullScan").GetBoolean(),
                 f.GetProperty("DefaultDeadlineOffsetHours").GetInt32()),
                (s.Name, s.Purpose, s.UserNotification, s.UtcBasis, s.OverrideServiceWindow, s.RebootOutsideServiceWindow, s.AllowMetered, s.AllowBoundaryFallback,
                 s.AllowMicrosoftUpdate, s.PostRebootScan, s.DeadlineOffsetHours));
        }
    }
}
