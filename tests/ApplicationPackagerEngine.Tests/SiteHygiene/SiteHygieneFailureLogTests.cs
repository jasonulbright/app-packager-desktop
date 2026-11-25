using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// Q-136, the owner's bug report: the SUP-01 fix failed and said "the log has more details", but the log held nothing. A retired
/// application cannot be changed (the site keeps the old document and returns no error), so the fix reinstates it, changes it and
/// retires it again, as the APP-07 fix does. Every failure of a fix writes the object, the call, the exception chain and the blocker to
/// the application log; the text of the window and the audit log stay free of the provider's text.
/// </summary>
public sealed class SiteHygieneFailureLogTests
{
    private const string Scope = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E";
    private const string NewModel = Scope + "/Application_aaaa0000-0000-4000-8000-000000000001";
    private const string GoneOne = Scope + "/Application_dead0001-0000-4000-8000-000000000001";
    private const string ProviderText = "SECRET-DETAIL-1234";

    private sealed class RecordingLog : IAppLog
    {
        public List<AppLogEntry> Entries { get; } = [];
        public string? SessionFile => null;
        public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;
        public event EventHandler<AppLogEntry>? Written;
        public void RegisterSecret(string value) { }
        public void Write(AppLogLevel level, string area, string message)
        {
            var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, area, message);
            Entries.Add(entry);
            Written?.Invoke(this, entry);
        }

        public IReadOnlyList<AppLogEntry> Tail() => Entries.ToList();
        public string All => string.Join("\n", Entries.Select(e => $"{e.Level} {e.Area} {e.Message}"));
    }

    private sealed class ThrowingSession(IConfigMgrSession inner, Func<Dictionary<string, object?>, Exception?> throwWhen) : IConfigMgrSession
    {
        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            if (throwWhen((parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value)) is { } ex) throw ex;
            return inner.InvokeAsync(script, parameters, ct, timeout);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static JsonObject AppRow(string model, int ci, string name, bool retired) => new()
    {
        ["CI_ID"] = ci.ToString(), ["ModelID"] = (ci + 1000).ToString(), ["ModelName"] = model, ["LocalizedDisplayName"] = name, ["IsLatest"] = true,
        ["IsDeployed"] = "False", ["IsExpired"] = retired ? "True" : "False", ["IsSuperseded"] = "False", ["IsSuperseding"] = "False", ["IsEnabled"] = "True", ["HasContent"] = "True",
        ["Manufacturer"] = "Lab", ["SoftwareVersion"] = "1.0", ["DateCreated"] = "2026-10-01T00:00:00", ["NumberOfDeploymentTypes"] = "1", ["CIVersion"] = "1"
    };

    /// <summary>The CM01 rows plus one application whose definition has a supersedence rule that points at a deleted application.</summary>
    private static JsonObject BrokenState(bool retired, bool deployed = false)
    {
        var state = SiteHygieneFixtures.StubState();
        var classes = state["Classes"]!.AsObject();
        classes["SMS_Application"]!.AsArray().Add(AppRow(NewModel, 16790001, "ZZ-Superseding", retired));
        state["Supersedence"]!.AsObject()[NewModel] = new JsonArray(new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["RuleId"] = "DTRule_1", ["Ref"] = GoneOne });
        if (deployed)
        {
            var assignments = classes["SMS_ApplicationAssignment"] as JsonArray ?? (JsonArray)(classes["SMS_ApplicationAssignment"] = new JsonArray());
            assignments.Add(new JsonObject { ["AssignmentID"] = "16799001", ["AppModelID"] = "16791001", ["TargetCollectionID"] = "MCM00001" });
        }

        return state;
    }

    private static async Task<(HygieneChange Change, HygieneFinding Finding)> PrepareSup01Async(SiteHygieneHarness h)
    {
        var finding = (await h.ScanAsync("AppRelationships")).Findings.Single(f => f.CheckId == "SUP-01" && f.ObjectKey.StartsWith(NewModel + "|", StringComparison.Ordinal));
        return (await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding), finding);
    }

    private static string[] SiteCommands(SiteHygieneHarness h) => h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).ToArray();

    private static bool IsRetired(StubHygieneSite site) =>
        site.Class("SMS_Application").Any(r => (string?)r!["ModelName"] == NewModel && r["IsLatest"]!.GetValue<bool>() && (string?)r["IsExpired"] == "True");

    [Fact]
    public async Task ARetiredApplicationIsReinstatedChangedAndRetiredAgain()
    {
        using var h = new SiteHygieneHarness(BrokenState(retired: true));
        var (change, _) = await PrepareSup01Async(h);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        Assert.Contains(change.Impact, l => l.Contains("A retired application cannot be changed", StringComparison.Ordinal) && l.Contains("check APP-02", StringComparison.Ordinal));
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.Equal(["Cmd:Resume-CMApplication|" + NewModel, "Cmd:SupersedenceRemoval|" + NewModel + "|DTRule_1", "Cmd:Suspend-CMApplication|" + NewModel], SiteCommands(h));
        Assert.True(IsRetired(h.Site), "The application is retired again.");
        Assert.Empty(h.Site.State["Supersedence"]![NewModel]!.AsArray());
    }

    [Fact]
    public async Task AnActiveApplicationIsChangedWithoutReinstatingIt()
    {
        using var h = new SiteHygieneHarness(BrokenState(retired: false));
        var (change, _) = await PrepareSup01Async(h);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Equal(["Cmd:SupersedenceRemoval|" + NewModel + "|DTRule_1"], SiteCommands(h));
    }

    /// <summary>
    /// The cause of the owner's failure on a real site: the ConfigMgr session looks up a cmdlet before it imports the module, so PowerShell loads
    /// the module of bin\ConfigurationManager, one folder below the serializer in bin. The fix refused every application with the serializer check.
    /// </summary>
    [Fact]
    public async Task AModuleThatPowerShellLoadedFromTheFolderBelowTheSerializerIsTheConsole()
    {
        var state = BrokenState(retired: false);
        state["Options"]!["ModuleInSubfolder"] = "1";
        using var h = new SiteHygieneHarness(state);
        var (change, _) = await PrepareSup01Async(h);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message);
        Assert.Equal(["Cmd:SupersedenceRemoval|" + NewModel + "|DTRule_1"], SiteCommands(h));
    }

    [Fact]
    public async Task ARetiredApplicationWithADeploymentIsNotReinstatedAndTheReasonIsNamed()
    {
        var log = new RecordingLog();
        using var h = new SiteHygieneHarness(BrokenState(retired: true, deployed: true), configure: null);
        h.Service = new SiteHygieneService(h.Site, h.Audit, log, delay: (t, ct) => Task.CompletedTask, utcNow: () => h.Clock, connection: () => h.Connection);
        var (change, _) = await PrepareSup01Async(h);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Skipped, result.Result);
        Assert.Contains("retired and has a deployment", result.Message);
        Assert.Contains("check APP-02", result.Message);
        Assert.Empty(SiteCommands(h));
        Assert.True(IsRetired(h.Site));
        Assert.Contains("ZZ-Superseding", log.All);
        Assert.Contains("retired and has a deployment", log.All);
    }

    [Fact]
    public async Task AFailedWriteLogsTheObjectTheStepTheExceptionAndTheProviderText()
    {
        var log = new RecordingLog();
        using var h = new SiteHygieneHarness(BrokenState(retired: false));
        h.Service = new SiteHygieneService(h.Site, h.Audit, log, delay: (t, ct) => Task.CompletedTask, utcNow: () => h.Clock, connection: () => h.Connection);
        var (change, finding) = await PrepareSup01Async(h);
        h.Site.Fail("cmd:Put");
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, result.Result);
        Assert.Contains("The log has the details.", result.Message);
        // The window and the audit log never hold the text of the provider.
        Assert.DoesNotContain(ProviderText, result.Message);
        Assert.DoesNotContain(ProviderText, outcome.Message);
        Assert.DoesNotContain(ProviderText, File.ReadAllText(Path.Combine(h.Folder, "site-hygiene-audit.jsonl")));
        // The application log names the object, the call, the exception and the provider text.
        var line = Assert.Single(log.Entries, e => e.Level == AppLogLevel.Error && e.Message.StartsWith("ClearBrokenSupersedence failed.", StringComparison.Ordinal));
        Assert.Contains("ZZ-Superseding", line.Message);
        Assert.Contains(NewModel, line.Message);
        Assert.Contains(GoneOne, line.Message);
        Assert.Contains("Step: Write the application document of " + NewModel, line.Message);
        Assert.Contains("System.Management.Automation.RuntimeException", line.Message);
        Assert.Contains(ProviderText, line.Message);
        Assert.Contains("Result kind: error", line.Message);
        Assert.Equal("SUP-01", finding.CheckId);
    }

    [Fact]
    public async Task AFailedReadLogsTheSetTheExceptionAndTheProviderText()
    {
        var log = new RecordingLog();
        using var h = new SiteHygieneHarness(BrokenState(retired: false));
        h.Service = new SiteHygieneService(h.Site, h.Audit, log, delay: (t, ct) => Task.CompletedTask, utcNow: () => h.Clock, connection: () => h.Connection);
        h.Site.Fail("query:SMS_AppRelation_Flat");
        await h.ScanAsync("AppRelationships");
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("The read of AppRelations failed in the ConfigMgr call", StringComparison.Ordinal));
        Assert.Contains("kind error", line.Message);
        Assert.Contains(ProviderText, line.Message);
        Assert.Contains("System.Management.Automation.RuntimeException", line.Message);
    }

    [Fact]
    public async Task AnUnexpectedStopLogsEveryExceptionOfTheChainAndTheObject()
    {
        var log = new RecordingLog();
        using var h = new SiteHygieneHarness(BrokenState(retired: false));
        var session = new ThrowingSession(h.Site, p => (string?)p["Operation"] == "Apply" ? new InvalidOperationException("outer problem", new IOException("inner problem " + ProviderText)) : null);
        h.Service = new SiteHygieneService(session, h.Audit, log, delay: (t, ct) => Task.CompletedTask, utcNow: () => h.Clock, connection: () => h.Connection);
        var (change, _) = await PrepareSup01Async(h);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.False(outcome.Succeeded);
        var line = Assert.Single(log.Entries, e => e.Level == AppLogLevel.Error && e.Message.Contains("stopped at an object with an unexpected error", StringComparison.Ordinal));
        Assert.Contains("System.InvalidOperationException: outer problem", line.Message);
        Assert.Contains("Inner exception: System.IO.IOException: inner problem", line.Message);
        Assert.Contains("ZZ-Superseding", line.Message);
        Assert.Contains(NewModel, line.Message);
        Assert.Contains("SUP-01", line.Message);
        Assert.DoesNotContain(ProviderText, outcome.Message);
    }

    [Fact]
    public void TheTextOfAnExceptionHoldsTheTypeTheMessageTheInnerExceptionsAndTheSessionFailure()
    {
        var inner = new IOException("disk gone");
        var outer = new InvalidOperationException("outer", inner);
        var text = ExceptionText.Describe(outer);
        Assert.Contains("System.InvalidOperationException: outer", text);
        Assert.Contains("Inner exception: System.IO.IOException: disk gone", text);
        var session = ExceptionText.Describe(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "The command failed"));
        Assert.Contains("(failure ScriptFailed)", session);
        Assert.Contains("The command failed", session);
        var aggregate = ExceptionText.Describe(new AggregateException(new ArgumentException("first"), new TimeoutException("second")));
        Assert.Contains("System.ArgumentException: first", aggregate);
        Assert.Contains("System.TimeoutException: second", aggregate);
        Assert.Equal("(no exception)", ExceptionText.Describe(null));
        Assert.True(ExceptionText.Describe(new Exception(new string('x', 10_000))).Length < 3_000);
    }

    [Fact]
    public void ALogLineWithAnExceptionHoldsTheDetailAndTheLogMasksSecrets()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AppLog(folder);
            log.RegisterSecret(ProviderText);
            log.Warning("Site Hygiene", "A write failed.", new InvalidOperationException("outer", new IOException("inner " + ProviderText)));
            var file = File.ReadAllText(log.SessionFile!);
            Assert.Contains("A write failed.", file);
            Assert.Contains("System.InvalidOperationException: outer", file);
            Assert.Contains("Inner exception: System.IO.IOException: inner", file);
            Assert.DoesNotContain(ProviderText, file);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
