using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The words of the Live tab: counts of one, the import plan and status, and the note of a folder that cannot be imported.</summary>
public sealed partial class SiteHygieneLiveTextTests
{
    private const string Folder = @"C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard";

    private static LiveSnapshot SnapshotOfOne()
    {
        var views = Enum.GetValues<LiveView>().ToDictionary(v => v, v => LiveData.Result(v, rows: 1) with
        {
            Counts = new LiveCounts(LiveMetrics.Of(v).ToDictionary(c => c, c => (double?)1, StringComparer.Ordinal))
        });
        var ended = new DateTime(2026, 10, 25, 20, 43, 43, DateTimeKind.Utc);
        return new LiveSnapshot(views, ended.AddSeconds(-5), ended);
    }

    [Fact]
    public void TheSummaryOfCountsOfOneUsesTheSingularNoun()
    {
        var summary = LiveText.Summary(SnapshotOfOne(), new DateTime(2026, 10, 25, 16, 43, 43));
        Assert.Contains("Deployments: 1 deployment, 1 with errors, compliance 1 %.", summary);
        Assert.Contains("Content: 1 content item with issues, 1 failed DP-content pair.", summary);
        Assert.Contains("Distribution Points: 1 distribution point, 1 critical, 1 warning.", summary);
        Assert.Contains("Inactive Devices: 1 device past the inactivity threshold.", summary);
        var two = LiveText.Summary(LiveData.Snapshot(rows: 2), new DateTime(2026, 10, 25, 16, 43, 43));
        Assert.Contains("Deployments: 2 deployments,", two);
    }

    [Fact]
    public void AnAlertOfOneUsesTheSingularNounAndVerb()
    {
        var evaluator = new AlertEvaluator();
        var settings = AlertSettings.Default with { Enabled = true, SiteCritical = true, DpCritical = true, ContentFailed = true };
        static LiveSnapshot Of(double value)
        {
            var s = SnapshotOfOne();
            return s with { Views = s.Views.ToDictionary(p => p.Key, p => p.Value with { Counts = new LiveCounts(p.Value.Counts.Values.Keys.ToDictionary(c => c, c => (double?)value, StringComparer.Ordinal)) }) };
        }
        Assert.Empty(evaluator.Evaluate(Of(0), settings));
        var messages = evaluator.Evaluate(Of(1), settings).Select(e => e.Message).ToList();
        Assert.Contains("Site health: 1 component or site system is critical.", messages);
        Assert.Contains("Distribution points: 1 is critical.", messages);
        Assert.Contains("Content: 1 DP-content pair failed.", messages);
        Assert.Empty(evaluator.Evaluate(Of(0), settings));
        var more = evaluator.Evaluate(Of(2), settings).Select(e => e.Message).ToList();
        Assert.Contains("Site health: 2 components or site systems are critical.", more);
        Assert.Contains("Distribution points: 2 are critical.", more);
        Assert.Contains("Content: 2 DP-content pairs failed.", more);
    }

    /// <summary>A string literal of one source line with its interpolation holes.</summary>
    [GeneratedRegex(@"\$""(?:[^""\\]|\\.)*""")]
    private static partial Regex Interpolated();

    /// <summary>
    /// A count hole followed by a plural word, with up to two words between them that are not prepositions: the text reads
    /// "1 rows" for a count of one. A word followed by "=" is a log key, not text.
    /// </summary>
    [GeneratedRegex(@"\{(?<hole>[^{}]+)\} (?<between>(?:[A-Za-z-]+ ){0,2})(?<word>[a-z][a-z-]*[a-z]s)\b(?!=)")]
    private static partial Regex HoleThenPlural();

    /// <summary>A hole that holds a count: a number format call of one argument, a collection count, a number as text, or a constant.</summary>
    [GeneratedRegex(@"^(?:(?:N|Number|Count|LiveText\.Count)\((?:[^(),]|\([^()]*\))*\)|.+\.Count|.+\.ToString\([^()]*\)|[A-Z][A-Za-z]*|_[a-z][A-Za-z]*)$")]
    private static partial Regex CountHole();

    private static readonly HashSet<string> Prepositions = new(StringComparer.Ordinal) { "with", "of", "in", "at", "to", "for", "from", "on" };

    private static readonly HashSet<string> NotPlural = new(StringComparer.Ordinal) { "progress", "status", "is", "was", "has", "this", "less", "its" };

    private static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerEngine"))) folder = folder.Parent;
        return folder!.FullName;
    }

    [Fact]
    public void NoLiveTextSourcePutsACountBeforeAFixedPluralNoun()
    {
        var root = Root();
        var engine = Path.Combine(root, "src", "ApplicationPackagerEngine", "Services", "SiteHygiene");
        var ui = Path.Combine(root, "src", "ApplicationPackagerUI", "ViewModels", "Pages");
        string[] files =
        [
            .. Directory.GetFiles(engine, "Live*.cs"), .. Directory.GetFiles(engine, "SiteHygieneLive*.cs"),
            Path.Combine(engine, "LegacyDashboardImport.cs"), Path.Combine(engine, "MetricsHistoryStore.cs"),
            .. Directory.GetFiles(ui, "SiteHygieneViewModel.Live*.cs"), Path.Combine(ui, "SiteHygieneViewModel.Trends.cs"), .. Directory.GetFiles(ui, "SiteHygieneLive*.cs")
        ];
        Assert.True(files.Length >= 11, $"Only {files.Length} Live text files were found.");
        var failures = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match literal in Interpolated().Matches(lines[i]))
                    foreach (Match m in HoleThenPlural().Matches(literal.Value))
                    {
                        var between = m.Groups["between"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (!CountHole().IsMatch(m.Groups["hole"].Value.Trim()) || NotPlural.Contains(m.Groups["word"].Value) || between.Any(Prepositions.Contains)) continue;
                        failures.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Value}");
                    }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheScanFindsACountBeforeAFixedPlural()
    {
        var line = "var text = $\"The read was cut at {N(MaxSqlRows)} rows, {MaxAlertRows} alerts and {rows.Count} history rows.\";";
        var found = Interpolated().Matches(line).SelectMany(l => HoleThenPlural().Matches(l.Value)).Where(m => CountHole().IsMatch(m.Groups["hole"].Value)).Select(m => m.Groups["word"].Value);
        Assert.Equal(["rows", "alerts", "rows"], found);
    }

    [Fact]
    public async Task TheImportStatusListsEachFilledSettingOnceByItsName()
    {
        var h = new SiteHygienePageHarness();
        h.Live!.Import.Next = FakeLegacyImport.Found(rows: 1, values: LiveSettingValues.Empty with { SqlServer = "CM01.contoso.com" });
        var vm = await h.OpenAsync();
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal("Filled 1 setting: SQL server. Copied 1 history row.", vm.StatusMessage);
        foreach (var key in new[] { LiveSettingKeys.SqlServer, LiveSettingKeys.TrustSqlCertificate, LiveSettingKeys.RefreshMinutes, LiveSettingKeys.InactiveDays, LiveSettingKeys.AlertsEnabled, LiveSettingKeys.CompliancePctFloor })
            Assert.DoesNotContain(key, vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealImportStatusOfOneSettingAndOneRowUsesTheSingular()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apkg-shtext-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacy = Path.Combine(dir, "site-hygiene", "legacy", "mecm-health-dashboard");
            Directory.CreateDirectory(Path.Combine(legacy, "History"));
            File.WriteAllText(Path.Combine(legacy, LegacyDashboardImport.DashboardPrefsFile), "{ \"SQLServer\": \"CM01.contoso.com\" }");
            File.WriteAllText(Path.Combine(legacy, LegacyDashboardImport.HistoryRelative),
                "\"Timestamp\",\"DeploymentTotal\"\r\n\"2026-10-20T08:15:00.0000000-04:00\",\"9\"\r\n");
            var values = LiveSettingValues.Empty;
            var store = new MetricsHistoryStore(Path.Combine(dir, "data", "metrics-history.csv"), utcNow: () => new DateTime(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc));
            var import = new LegacyDashboardImport(store, () => values, v => { values = v; return true; }, () => [Path.Combine(dir, "site-hygiene")],
                utcNow: () => new DateTime(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc));
            var result = import.Apply(import.Plan());
            Assert.Equal("Filled 1 setting: SQL server. Copied 1 history row.", result.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheImportPlanNamesTheSourceOnceAndEachLineCarriesOnlyItsItemAndCount()
    {
        var h = new SiteHygienePageHarness();
        h.Live!.Import.Next = FakeLegacyImport.Found();
        var vm = await h.OpenAsync();
        h.Dialogs.ConfirmImport = false;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        var lines = h.Dialogs.Imports[^1].Plan.Split(Environment.NewLine);
        Assert.Equal($"Folder: {Folder}", lines[0]);
        Assert.Equal(@"Files: History\metrics-history.csv, SiteHygiene.prefs.json", lines[1]);
        Assert.Equal("Metrics history: 42 rows.", lines[2]);
        Assert.Contains("Setting SQL server: CM01.contoso.com.", lines);
        Assert.Single(lines, l => l.Contains(Folder, StringComparison.OrdinalIgnoreCase));
        Assert.All(lines.Skip(1), l => Assert.True(l.Length <= 80, $"The plan line is {l.Length} characters: {l}"));
    }

    [Fact]
    public async Task AFolderThatCannotBeImportedGetsNoOfferAndANoteOnTheImportRowOnTheFirstVisit()
    {
        const string refusal = @"The settings file C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard\SiteHygiene.prefs.json is not valid JSON. Nothing was imported.";
        var h = new SiteHygienePageHarness();
        h.Live!.Import.Next = new LegacyImportPlan(Folder, [], 0, false, refusal);
        var vm = await h.OpenAsync();
        Assert.False(vm.HasImportOffer);
        Assert.True(vm.HasImportNote);
        Assert.Equal(refusal, vm.ImportNote);
        Assert.Equal(1, h.Live.Import.Plans);
        await vm.OnNavigatedFromAsync();
        await vm.OnNavigatedToAsync();
        Assert.Equal(1, h.Live.Import.Plans);

        var none = new SiteHygienePageHarness();
        var vm2 = await none.OpenAsync();
        Assert.False(vm2.HasImportNote);
        Assert.False(vm2.HasImportOffer);

        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal(refusal, vm.StatusMessage);
        Assert.False(vm.HasImportNote);
        Assert.NotEqual(VM.ImportNothing, vm.StatusMessage);
    }
}
