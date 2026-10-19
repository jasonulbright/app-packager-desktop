using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// The projector against Get-NextOccurrences of the PowerShell module at ModulePath, imported read-only in a
/// child Windows PowerShell with Get-Date fixed. The module reads only StartTime,
/// RecurrenceType and Duration of a window, so the compared schedules are the ones it can
/// express: interval 1, the weekday and week of the start date, days 1 to 28. The module
/// leaves out an occurrence that is open at the current time; the comparison drops it too.
/// </summary>
public sealed class ScheduleProjectorModuleComparisonTests
{
    private const string ModulePath = @"C:\projects\maintenance-window-manager\Module\MaintWindowMgrCommon.psd1";

    private const string Harness = """
        param([string]$CasesPath, [string]$ModulePath)
        $ErrorActionPreference = 'Stop'
        $culture = [Globalization.CultureInfo]::InvariantCulture
        function global:Get-Date {
            [CmdletBinding()]
            param([int]$Year, [int]$Month, [int]$Day, [int]$Hour, [int]$Minute, [int]$Second, [int]$Millisecond)
            if ($PSBoundParameters.Count -eq 0) { return $global:FixedNow }
            Microsoft.PowerShell.Utility\Get-Date -Date $global:FixedNow @PSBoundParameters
        }
        Import-Module $ModulePath -Force -DisableNameChecking
        foreach ($case in (Get-Content -LiteralPath $CasesPath -Raw | ConvertFrom-Json)) {
            $global:FixedNow = [datetime]::ParseExact($case.Now, 'yyyy-MM-dd HH:mm', $culture)
            $window = [pscustomobject]@{
                StartTime      = [datetime]::ParseExact($case.Start, 'yyyy-MM-dd HH:mm', $culture)
                RecurrenceType = [int]$case.Type
                Duration       = [int]$case.Minutes
            }
            $dates = @(Get-NextOccurrences -Window $window -Count 5 | ForEach-Object { $_.ToString('yyyy-MM-dd HH:mm', $culture) })
            [Console]::Out.WriteLine($case.Id + '|' + ($dates -join ';'))
        }
        """;

    public sealed record ModuleCase(string Id, int Type, string Start, int Minutes, string Now);

    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The schedule in this model that the module's RecurrenceType and StartTime describe.</summary>
    private static MaintenanceSchedule Equivalent(ModuleCase c)
    {
        var start = T(c.Start);
        var duration = TimeSpan.FromMinutes(c.Minutes);
        return c.Type switch
        {
            1 => new MaintenanceSchedule(K.OneTime, start, duration, false, 1, null, null, null, 0, null),
            2 => new MaintenanceSchedule(K.Daily, start, duration, false, 1, null, null, null, 0, null),
            3 => new MaintenanceSchedule(K.Weekly, start, duration, false, 1, start.DayOfWeek, null, null, 0, null),
            4 => new MaintenanceSchedule(K.MonthlyByWeekday, start, duration, false, 1, start.DayOfWeek, null, (start.Day + 6) / 7, 0, null),
            _ => new MaintenanceSchedule(K.MonthlyByDate, start, duration, false, 1, null, start.Day, null, 0, null)
        };
    }

    private static string[] Projected(ModuleCase c, TimeZoneInfo zone)
    {
        var now = T(c.Now);
        return new ScheduleProjector(zone).NextOccurrences(Equivalent(c), now, 6)
            .Where(d => d > now).Take(5)
            .Select(d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).ToArray();
    }

    public static IReadOnlyList<ModuleCase> ComparableCases()
    {
        var starts = new (int Type, string Start)[]
        {
            (1, "2026-11-03 22:30"), (1, "2026-10-19 04:00"), (1, "2027-03-14 01:00"), (1, "2026-10-01 00:00"), (1, "2025-12-31 23:59"), (1, "2026-12-25 12:00"),
            (2, "2026-08-13 04:00"), (2, "2026-11-03 22:30"), (2, "2020-01-01 00:00"), (2, "2026-10-19 01:00"), (2, "2026-10-18 23:00"), (2, "2026-02-01 05:15"), (2, "2027-01-01 03:00"), (2, "2026-10-19 01:38"),
            (3, "2026-08-15 06:00"), (3, "2026-11-03 22:30"), (3, "2024-02-29 02:00"), (3, "2026-10-12 01:00"), (3, "2026-10-19 23:00"), (3, "2026-01-04 00:30"), (3, "2026-10-17 18:00"), (3, "2027-05-05 05:05"),
            (4, "2026-01-03 01:00"), (4, "2026-01-13 02:00"), (4, "2026-10-22 00:00"), (4, "2026-02-28 23:00"), (4, "2026-06-01 04:00"), (4, "2026-09-15 06:30"), (4, "2025-03-21 21:00"), (4, "2026-11-08 01:30"), (4, "2026-10-19 01:00"), (4, "2027-02-14 12:00"),
            (5, "2026-01-01 02:00"), (5, "2026-01-28 03:00"), (5, "2026-10-19 01:00"), (5, "2026-10-19 02:00"), (5, "2026-02-15 22:00"), (5, "2024-02-28 04:00"), (5, "2026-12-10 00:00"), (5, "2026-07-04 07:45"), (5, "2026-03-13 13:00"), (5, "2027-01-20 20:20"),
        };
        var nows = new[] { "2026-10-19 01:37", "2026-02-27 12:00" };
        return nows.SelectMany((now, n) => starts.Select((s, i) => new ModuleCase($"c{n}-{i}", s.Type, s.Start, 240, now))).ToList();
    }

    /// <summary>
    /// Results of the module with the clock fixed, for the case that the module is not
    /// on this computer. None of them touches a daylight saving gap, so the time zone does not
    /// change them.
    /// </summary>
    public static readonly (ModuleCase Case, string[] Dates)[] RecordedModuleResults =
    [
        (new("r1", 2, "2026-08-13 04:00", 360, "2026-10-19 01:37"), ["2026-10-19 04:00", "2026-10-20 04:00", "2026-10-21 04:00", "2026-10-22 04:00", "2026-10-23 04:00"]),
        (new("r2", 3, "2026-08-15 06:00", 240, "2026-10-19 01:37"), ["2026-10-24 06:00", "2026-10-31 06:00", "2026-11-07 06:00", "2026-11-14 06:00", "2026-11-21 06:00"]),
        (new("r3", 1, "2026-11-03 22:30", 180, "2026-10-19 01:37"), ["2026-11-03 22:30"]),
        (new("r4", 1, "2026-10-01 00:00", 180, "2026-10-19 01:37"), []),
        (new("r5", 4, "2026-01-13 02:00", 240, "2026-10-19 01:37"), ["2026-11-10 02:00", "2026-12-08 02:00", "2027-01-12 02:00", "2027-02-09 02:00", "2027-03-09 02:00"]),
        (new("r6", 4, "2026-01-03 01:00", 360, "2026-10-19 01:37"), ["2026-11-07 01:00", "2026-12-05 01:00", "2027-01-02 01:00", "2027-02-06 01:00", "2027-03-06 01:00"]),
        (new("r7", 5, "2026-01-28 03:00", 240, "2026-10-19 01:37"), ["2026-10-28 03:00", "2026-11-28 03:00", "2026-12-28 03:00", "2027-01-28 03:00", "2027-02-28 03:00"]),
        (new("r8", 5, "2026-01-01 02:00", 240, "2026-02-27 12:00"), ["2026-03-01 02:00", "2026-04-01 02:00", "2026-05-01 02:00", "2026-06-01 02:00", "2026-07-01 02:00"]),
        (new("r9", 2, "2026-10-28 23:00", 120, "2026-10-30 12:00"), ["2026-10-30 23:00", "2026-10-31 23:00", "2026-11-01 23:00", "2026-11-02 23:00", "2026-11-03 23:00"]),
    ];

    /// <summary>
    /// Schedules where the module and the projector differ by design, with the module's
    /// result: the module steps month by month from the start date with AddMonths, so a
    /// day 29 to 31 drifts to the shortest month's day where a ConfigMgr client skips the month; it reads a start after day 28 as a
    /// fifth weekday that can fall in the next month; it keeps a start time that a
    /// daylight saving change skips; it leaves out a window that is open now.
    /// </summary>
    public static readonly (string Why, ModuleCase Case, string[] ModuleDates, string[] ProjectorDates)[] KnownDifferences =
    [
        ("day 31 drifts to the 28th after February in the module; a ConfigMgr client skips a month without day 31", new("d1", 5, "2026-01-31 02:00", 60, "2026-10-19 01:37"),
            ["2026-10-28 02:00", "2026-11-28 02:00", "2026-12-28 02:00", "2027-01-28 02:00", "2027-02-28 02:00"],
            ["2026-10-31 02:00", "2026-12-31 02:00", "2027-01-31 02:00", "2027-03-31 02:00", "2027-05-31 02:00"]),
        ("a start on day 29 to 31 is a fifth weekday in the module and the last weekday in ConfigMgr", new("d2", 4, "2026-01-30 01:00", 60, "2026-10-19 01:37"),
            ["2026-10-30 01:00", "2026-12-04 01:00", "2027-01-29 01:00", "2027-03-05 01:00", "2027-04-30 01:00"],
            ["2026-10-30 01:00", "2026-11-27 01:00", "2026-12-25 01:00", "2027-01-29 01:00", "2027-02-26 01:00"]),
        ("a skipped start time is kept by the module and moved forward by the projector", new("d3", 2, "2026-03-01 02:30", 60, "2026-03-07 12:00"),
            ["2026-03-08 02:30", "2026-03-09 02:30", "2026-03-10 02:30", "2026-03-11 02:30", "2026-03-12 02:30"],
            ["2026-03-08 03:30", "2026-03-09 02:30", "2026-03-10 02:30", "2026-03-11 02:30", "2026-03-12 02:30"]),
    ];

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    [Fact]
    public void TheProjectorMatchesTheRecordedModuleResults()
    {
        var ran = 0;
        foreach (var (c, dates) in RecordedModuleResults)
        {
            Assert.True(dates.SequenceEqual(Projected(c, Eastern)), $"{c.Id}: {string.Join(";", Projected(c, Eastern))}");
            ran++;
        }
        Assert.Equal(9, ran);
    }

    [Fact]
    public void TheKnownDifferencesAreTheProjectorsDocumentedResults()
    {
        foreach (var (why, c, _, projector) in KnownDifferences)
            Assert.True(projector.SequenceEqual(Projected(c, Eastern)), why);
    }

    [Fact]
    public void TheProjectorMatchesTheModuleForEveryScheduleTheModuleCanExpress()
    {
        if (!File.Exists(ModulePath))
        {
            TheProjectorMatchesTheRecordedModuleResults();
            return;
        }

        var cases = ComparableCases();
        var withKnown = cases.Concat(KnownDifferences.Select(k => k.Case)).Concat(RecordedModuleResults.Select(r => r.Case)).ToList();
        var module = RunModule(withKnown);

        var compared = 0;
        foreach (var c in cases)
        {
            Assert.True(module.ContainsKey(c.Id), $"The module returned nothing for {c.Id}.");
            Assert.True(module[c.Id].SequenceEqual(Projected(c, TimeZoneInfo.Local)),
                $"{c.Id} type {c.Type} start {c.Start} now {c.Now}: module {string.Join(";", module[c.Id])}, projector {string.Join(";", Projected(c, TimeZoneInfo.Local))}");
            compared++;
        }
        Assert.True(compared >= 40, $"Only {compared} schedules were compared.");

        foreach (var (why, c, moduleDates, _) in KnownDifferences)
            Assert.True(moduleDates.SequenceEqual(module[c.Id]), $"The module result changed for '{why}': {string.Join(";", module[c.Id])}");
        foreach (var (c, dates) in RecordedModuleResults)
            Assert.True(dates.SequenceEqual(module[c.Id]), $"The recorded module result changed for {c.Id}: {string.Join(";", module[c.Id])}");
    }

    private static Dictionary<string, string[]> RunModule(IReadOnlyList<ModuleCase> cases)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-mwcompare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var casesPath = Path.Combine(folder, "cases.json");
            var harnessPath = Path.Combine(folder, "harness.ps1");
            File.WriteAllText(casesPath, JsonSerializer.Serialize(cases));
            File.WriteAllText(harnessPath, Harness, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harnessPath, "-CasesPath", casesPath, "-ModulePath", ModulePath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The module did not finish.");
            Assert.True(process.ExitCode == 0, "The module failed: " + error.Result);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('|'))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1].Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
