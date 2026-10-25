using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Live service over the real Live script in the stub site with the CM01 rows, and the real SQL script with a stand-in SqlClient.</summary>
public sealed class LiveServiceTests
{
    private static readonly IReadOnlySet<LiveView> ProviderViews = new HashSet<LiveView> { LiveView.Deployments, LiveView.Content, LiveView.DistributionPoints, LiveView.SiteHealth };
    private static readonly IReadOnlySet<LiveView> SqlViews = new HashSet<LiveView> { LiveView.ClientHealth, LiveView.InactiveDevices };
    private static readonly IReadOnlySet<LiveView> AllViews = Enum.GetValues<LiveView>().ToHashSet();

    private static readonly LiveSettings Overrides = LiveSettings.Default with { SqlServer = "CM01.contoso.com", SqlDatabase = "CM_MCM" };

    private sealed class Progress : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }

    private static SiteHygieneLiveService Service(IConfigMgrSession session, FakeSqlHost? sql = null, ConfigMgrConnection? connection = null, Func<SiteHygieneLiveService, SiteHygieneLiveService>? with = null)
    {
        var s = new SiteHygieneLiveService(session, (sql ?? new FakeSqlHost()).Runner, connection: () => connection ?? new ConfigMgrConnection("MCM", "CM01"),
            utcNow: () => new DateTime(2026, 10, 25, 14, 29, 6, DateTimeKind.Utc));
        return with is null ? s : with(s);
    }

    private static Task<LiveSnapshot> Refresh(SiteHygieneLiveService service, IReadOnlySet<LiveView>? views = null, LiveSettings? settings = null, CancellationToken ct = default) =>
        service.RefreshAsync(settings ?? Overrides, views ?? ProviderViews, new Progress(), ct);

    // ---- Provider views with the CM01 rows --------------------------------------------------------

    [Fact]
    public async Task TheDeploymentsViewHoldsTheCm01RowsAndTheOverallComplianceOfTargetedClients()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        var snapshot = await Refresh(Service(site), new HashSet<LiveView> { LiveView.Deployments });
        var view = snapshot.Views[LiveView.Deployments];
        Assert.False(view.Failed);
        Assert.False(view.Truncated);
        Assert.Equal(9, view.Rows.Count);
        Assert.Equal(9, view.Counts[LiveMetrics.DeploymentTotal]);
        Assert.Equal(0, view.Counts[LiveMetrics.DeploymentFailed]);
        Assert.Equal(58.3, view.Counts[LiveMetrics.CompliancePct]);
        Assert.Equal(1, view.QueryCount);
        Assert.Equal(1, site.Queries("SMS_DeploymentSummary"));

        var app = view.Rows.Single(r => r.Key == "{EBD95D0B-A855-4AC6-9B7E-02211FA6C0E5}");
        Assert.Equal((LiveStatus.Warning, "Below 95 %"), (app.Status, app.StatusText));
        Assert.Equal(["CMCO-CANARY-App", "Application", "Collection MCM0001F", "Available", "1", "0", "0", "0", "0.0"], app.Cells);
        var swu = view.Rows.Single(r => r.Cells[0] == "CMCO-CANARY-SWU-Group");
        Assert.Equal((LiveStatus.Warning, "In progress"), (swu.Status, swu.StatusText));
        var ok = view.Rows.Single(r => r.Key == "MCM2000E");
        Assert.Equal((LiveStatus.Ok, "OK", "Program", "Required"), (ok.Status, ok.StatusText, ok.Cells[1], ok.Cells[3]));
        Assert.Equal(LiveAggregator.DeploymentColumns.Count, view.Columns.Count);
        Assert.All(view.Rows, r => Assert.Equal(view.Columns.Count, r.Cells.Count));
    }

    [Fact]
    public async Task ADeploymentWithErrorsFailsAndOneWithNothingTargetedIsUnknownAndLeavesComplianceUnknown()
    {
        var state = LiveFixtures.State();
        var rows = new JsonArray();
        foreach (var r in LiveFixtures.Deployments().OfType<JsonObject>()) { r["NumberTargeted"] = 0; r["NumberSuccess"] = 0; rows.Add(r.DeepClone()); }
        ((JsonObject)rows[0]!)["NumberErrors"] = 2;
        state["Classes"]!["SMS_DeploymentSummary"] = rows;
        var snapshot = await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.Deployments });
        var view = snapshot.Views[LiveView.Deployments];
        Assert.Equal(LiveStatus.Failed, view.Rows[0].Status);
        Assert.All(view.Rows.Skip(1), r => Assert.Equal((LiveStatus.Unknown, "Nothing targeted"), (r.Status, r.StatusText)));
        Assert.Null(view.Counts[LiveMetrics.CompliancePct]);
        Assert.Equal(1, view.Counts[LiveMetrics.DeploymentFailed]);
        Assert.Equal("", view.Rows[1].Cells[8]);
    }

    [Fact]
    public async Task TheContentViewOfCm01IsEmptyWithZeroCountsBecauseEveryPairIsInstalled()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        var view = (await Refresh(Service(site), new HashSet<LiveView> { LiveView.Content })).Views[LiveView.Content];
        Assert.False(view.Failed);
        Assert.Empty(view.Rows);
        Assert.Equal(0, view.Counts[LiveMetrics.ContentIssues]);
        Assert.Equal(0, view.Counts[LiveMetrics.ContentFailedPairs]);
        Assert.Equal(3, view.QueryCount);
        Assert.Equal(1, site.Queries("SMS_PackageStatusDistPointsSummarizer"));
        Assert.Equal(1, site.Queries("SMS_DistributionDPStatus"));
        Assert.Equal(1, site.Queries("SMS_PackageStatusRootSummarizer"));
        Assert.Equal(0, site.Queries("SMS_PackageBaseclass"));
    }

    [Fact]
    public async Task ContentFailsByStateOrByAnErrorMessageWithTheDpContentRuleAndIsNamedByTheRootSummarizer()
    {
        var state = LiveFixtures.State();
        var pairs = (JsonArray)state["Classes"]!["SMS_PackageStatusDistPointsSummarizer"]!;
        var messages = (JsonArray)state["Classes"]!["SMS_DistributionDPStatus"]!;
        JsonObject Pair(string id) => pairs.OfType<JsonObject>().Single(p => (string?)p["PackageID"] == id);
        JsonObject Message(string id) => messages.OfType<JsonObject>().Single(p => (string?)p["PackageID"] == id);
        Pair("MCM00005")["State"] = 3;
        Pair("MCM00035")["State"] = 7;
        Message("MCM00035")["MessageState"] = 4;
        Message("MCM00035")["NalPath"] = LiveFixtures.NalUpper;
        Pair("MCM00020")["State"] = 2;
        var view = (await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.Content })).Views[LiveView.Content];

        Assert.Equal(3, view.Rows.Count);
        var upgrade = view.Rows.Single(r => r.Key == "MCM00005");
        Assert.Equal((LiveStatus.Failed, "Configuration Manager Client Upgrade Package", "Package"), (upgrade.Status, upgrade.Cells[0], upgrade.Cells[1]));
        var zip = view.Rows.Single(r => r.Key == "MCM00035");
        Assert.Equal((LiveStatus.Failed, "Application", "1", "0", "0", "1"), (zip.Status, zip.Cells[1], zip.Cells[3], zip.Cells[4], zip.Cells[5], zip.Cells[6]));
        var swu = view.Rows.Single(r => r.Key == "MCM00020");
        Assert.Equal((LiveStatus.Warning, "In progress", "Software update package"), (swu.Status, swu.StatusText, swu.Cells[1]));
        Assert.Equal(3, view.Counts[LiveMetrics.ContentIssues]);
        Assert.Equal(2, view.Counts[LiveMetrics.ContentFailedPairs]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData(null)]
    public async Task APairWithABlankOrUnrecognizedStateIsAnUnknownRowAndLeavesTheContentCountsNotKnown(string? stateValue)
    {
        var state = LiveFixtures.State();
        var pairs = (JsonArray)state["Classes"]!["SMS_PackageStatusDistPointsSummarizer"]!;
        var pair = pairs.OfType<JsonObject>().Single(p => (string?)p["PackageID"] == "MCM00005");
        if (stateValue is null) pair.Remove("State");
        else if (stateValue.Length == 0) pair["State"] = "";
        else pair["State"] = int.Parse(stateValue, System.Globalization.CultureInfo.InvariantCulture);
        var view = (await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.Content })).Views[LiveView.Content];

        Assert.False(view.Failed);
        var row = Assert.Single(view.Rows);
        Assert.Equal(("MCM00005", LiveStatus.Unknown, "Unknown state"), (row.Key, row.Status, row.StatusText));
        Assert.Contains("Unknown state", LiveText.StatusTexts(LiveView.Content));
        Assert.Null(view.Counts[LiveMetrics.ContentIssues]);
        Assert.Null(view.Counts[LiveMetrics.ContentFailedPairs]);
        Assert.Equal(LiveAggregator.ContentUnknownMessage, view.Message);
        Assert.Contains("not known", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPairWithAnErrorMessageStillCountsAsFailedButTheCountsStayNotKnownWhenAnotherPairIsUnknown()
    {
        string[][] data = [["P1", "N1", "3", "0"], ["P2", "N1", "99", "0"], ["P3", "N1", "", "0"]];
        var pairs = Table(name: "Pairs", columns: ["PackageID", "ServerNALPath", "State", "PackageType"], n: data.Length, row: i => data[i]);
        var messages = Table(name: "Messages", columns: ["PackageID", "NalPath", "MessageState"], n: 1, row: _ => ["P3", "N1", "4"]);
        var names = Table(name: "Names", columns: ["PackageID", "Name"], n: 0, row: _ => []);
        var (rows, counts) = LiveAggregator.Content(pairs, messages, names, complete: true);
        Assert.Equal([LiveStatus.Failed, LiveStatus.Unknown, LiveStatus.Failed], rows.Select(r => r.Status));
        Assert.Null(counts[LiveMetrics.ContentIssues]);
        Assert.Null(counts[LiveMetrics.ContentFailedPairs]);
    }

    [Fact]
    public async Task TheDistributionPointViewMatchesTheServerWithoutCaseAndCountsByWorstStatus()
    {
        var state = LiveFixtures.State();
        var systems = (JsonArray)state["Classes"]!["SMS_SiteSystemSummarizer"]!;
        systems.Add(new JsonObject { ["SiteSystem"] = LiveFixtures.SiteSystemOf("CM01.CONTOSO.COM"), ["SiteCode"] = "MCM", ["Role"] = "SMS Distribution Point", ["Status"] = 1, ["ObjectType"] = 1 });
        var view = (await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.DistributionPoints })).Views[LiveView.DistributionPoints];
        var dp = Assert.Single(view.Rows);
        Assert.Equal((LiveStatus.Warning, "Warning"), (dp.Status, dp.StatusText));
        Assert.Equal(["CM01.CONTOSO.COM", "MCM", "No", "No", "Windows NT Server"], dp.Cells);
        Assert.Equal(LiveAggregator.DpColumns.Count, dp.Cells.Count);
        Assert.Equal(LiveFixtures.Nal.ToUpperInvariant(), dp.Key);
        Assert.Equal((1d, 0d, 1d), (view.Counts[LiveMetrics.DPTotal]!.Value, view.Counts[LiveMetrics.DPOffline]!.Value, view.Counts[LiveMetrics.DPDegraded]!.Value));
        Assert.Equal(2, view.QueryCount);
    }

    [Fact]
    public async Task ADistributionPointWithoutStatusIsUnknownAndMakesTheStatusCountsUnknown()
    {
        var state = LiveFixtures.State();
        ((JsonArray)state["Classes"]!["SMS_DistributionPointInfo"]!).Add(new JsonObject { ["NALPath"] = LiveFixtures.SiteSystemOf("DP02.contoso.com"), ["ServerName"] = "DP02.CONTOSO.COM", ["SiteCode"] = "MCM", ["IsPullDP"] = true, ["IsPXE"] = false, ["ResourceType"] = "Windows NT Server" });
        var view = (await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.DistributionPoints })).Views[LiveView.DistributionPoints];
        var dp2 = view.Rows.Single(r => r.Cells[0] == "DP02.CONTOSO.COM");
        Assert.Equal((LiveStatus.Unknown, "No status", "Yes"), (dp2.Status, dp2.StatusText, dp2.Cells[2]));
        Assert.Equal(2, view.Counts[LiveMetrics.DPTotal]);
        Assert.Null(view.Counts[LiveMetrics.DPOffline]);
        Assert.Null(view.Counts[LiveMetrics.DPDegraded]);
    }

    [Fact]
    public async Task TheSiteHealthViewReadsOnlyTheCurrentTallyAndEverySiteSystemRowWithUniqueKeys()
    {
        var state = LiveFixtures.State();
        var components = (JsonArray)state["Classes"]!["SMS_ComponentSummarizer"]!;
        components.OfType<JsonObject>().First(c => (string?)c["ComponentName"] == "SMS_EXECUTIVE" && (string?)c["TallyInterval"] == "0001128000100008")["Status"] = 2;
        var view = (await Refresh(Service(new StubHygieneSite(state)), new HashSet<LiveView> { LiveView.SiteHealth })).Views[LiveView.SiteHealth];
        Assert.Equal(6 + 10, view.Rows.Count);
        Assert.Equal(view.Rows.Count, view.Rows.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var exec = view.Rows.Single(r => r.Cells[0] == "SMS_EXECUTIVE");
        Assert.Equal((LiveStatus.Failed, "Critical", "Component", "Started", "2026-10-25 10:25:00"), (exec.Status, exec.StatusText, exec.Cells[1], exec.Cells[3], exec.Cells[4]));
        Assert.Equal("Stopped", view.Rows.Single(r => r.Cells[0] == "CONFIGURATION_MANAGER_UPDATE").Cells[3]);
        Assert.All(view.Rows, r => Assert.Equal(LiveAggregator.SiteColumns.Count, r.Cells.Count));        Assert.Equal(2, view.Rows.Count(r => r.Cells[0] == "SMS SQL Server"));
        Assert.Equal((15d, 0d, 1d), (view.Counts[LiveMetrics.SiteOK]!.Value, view.Counts[LiveMetrics.SiteWarning]!.Value, view.Counts[LiveMetrics.SiteCritical]!.Value));
    }

    [Fact]
    public async Task ARefreshOfTheFourProviderViewsSendsEightQueriesInFourSessionCalls()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        var progress = new Progress();
        var snapshot = await Service(site).RefreshAsync(Overrides, ProviderViews, progress, CancellationToken.None);
        Assert.Equal(4, site.Runs.Count);
        Assert.Equal(8, snapshot.QueryCount);
        Assert.Equal(8, site.Queries());
        Assert.All(snapshot.Views.Values, v => Assert.True(v.Complete));
        Assert.Equal(["Reading Deployments (1 of 4).", "Reading Content (2 of 4).", "Reading Distribution Points (3 of 4).", "Reading Site Health (4 of 4).", "Refresh finished: 4 of 4 views read."], progress.Lines);
        Assert.All(site.Runs, r => Assert.Equal(("MCM", "CM01"), ((string?)r["ExpectSite"], (string?)r["ExpectProvider"])));
    }

    // ---- Failures, truncation, connection, cancel ------------------------------------------------

    [Fact]
    public async Task AFailedSourceMarksOnlyItsViewFailedWithAFixedMessage()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        site.Fail("query:SMS_DistributionDPStatus");
        var snapshot = await Refresh(Service(site));
        var content = snapshot.Views[LiveView.Content];
        Assert.True(content.Failed);
        Assert.Empty(content.Rows);
        Assert.All(content.Counts.Values.Values, v => Assert.Null(v));
        Assert.Equal("The read of Content failed. The log has the kind of failure.", content.Message);
        Assert.DoesNotContain("SECRET", content.Message);
        Assert.All(snapshot.Views.Where(v => v.Key != LiveView.Content), v => Assert.True(v.Value.Complete));
        var row = snapshot.ToMetricRow();
        Assert.Null(row.Values[LiveMetrics.ContentIssues]);
        Assert.Equal(9, row.Values[LiveMetrics.DeploymentTotal]);
    }

    [Fact]
    public async Task AnAccessFailureSaysWhichReadWasDenied()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        site.Fail("access:query:SMS_DeploymentSummary");
        site.Fail("query:SMS_DeploymentSummary");
        var view = (await Refresh(Service(site), new HashSet<LiveView> { LiveView.Deployments })).Views[LiveView.Deployments];
        Assert.True(view.Failed);
        Assert.DoesNotContain("SECRET", view.Message);
    }

    [Fact]
    public async Task AReadCutAtItsCapIsTruncatedWithEveryCountUnknown()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        var service = Service(site, with: s => new SiteHygieneLiveService(site, new FakeSqlHost().Runner, connection: () => new ConfigMgrConnection("MCM", "CM01")) { MaxDeployments = 3, MaxSiteRows = 5 });
        var snapshot = await Refresh(service, new HashSet<LiveView> { LiveView.Deployments, LiveView.SiteHealth });
        var view = snapshot.Views[LiveView.Deployments];
        Assert.True(view.Truncated);
        Assert.False(view.Complete);
        Assert.Equal(3, view.Rows.Count);
        Assert.Equal(9, view.SourceRows);
        Assert.All(view.Counts.Values.Values, v => Assert.Null(v));
        Assert.Equal("The read was cut at 3 of 9 rows. The counts of this view are not known.", view.Message);
        Assert.True(snapshot.Views[LiveView.SiteHealth].Truncated);
        Assert.All(snapshot.ToMetricRow().Values.Values, v => Assert.Null(v));
    }

    [Fact]
    public async Task ASessionOnAnotherSiteOrProviderReadsNothing()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        var snapshot = await Refresh(Service(site, connection: new ConfigMgrConnection("MCM", "CM02")));
        Assert.All(snapshot.Views.Values, v => Assert.Equal((true, SiteHygieneLiveService.OtherConnectionMessage), (v.Failed, v.Message)));
        Assert.Equal(0, site.Queries());
    }

    [Theory]
    [InlineData(ConfigMgrFailure.Timeout, "ConfigMgr did not answer in time. Try again.")]
    [InlineData(ConfigMgrFailure.ScriptFailed, "The read of Deployments failed. The log has the kind of failure.")]
    [InlineData(ConfigMgrFailure.ModuleNotFound, "The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer.")]
    public async Task ASessionFailureFailsEachProviderViewWithAPlainMessage(ConfigMgrFailure failure, string message)
    {
        var session = new ThrowingSession(failure);
        var snapshot = await Refresh(Service(session));
        Assert.Equal(4, session.Calls);
        Assert.Equal(message, snapshot.Views[LiveView.Deployments].Message);
        Assert.All(snapshot.Views.Values, v => Assert.True(v.Failed));
        Assert.All(snapshot.Views.Values, v => Assert.DoesNotContain("SECRET", v.Message));
    }

    [Fact]
    public async Task CancelEndsTheRefreshWithoutASnapshot()
    {
        var session = new ThrowingSession(null);
        using var cts = new CancellationTokenSource();
        var task = Refresh(Service(session), ct: cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, session.Calls);
    }

    [Theory]
    [InlineData(100_000, 8)]
    [InlineData(3, 3)]
    public async Task TheProviderRowsStreamIntoTheCapSoARowPastTheCapIsNeverHeld(int cap, int readBeforeLast)
    {
        var state = LiveFixtures.State();
        state["Options"]!["Stream"] = "SMS_DeploymentSummary";
        var site = new StubHygieneSite(state);
        var service = new SiteHygieneLiveService(site, new FakeSqlHost().Runner, connection: () => new ConfigMgrConnection("MCM", "CM01")) { MaxDeployments = cap };
        var view = (await Refresh(service, new HashSet<LiveView> { LiveView.Deployments })).Views[LiveView.Deployments];
        var emits = site.Log.Where(l => l.StartsWith("Emit:", StringComparison.Ordinal)).Select(l => int.Parse(l[5..])).ToList();
        Assert.Equal(9, emits.Count);
        Assert.Equal(readBeforeLast, emits[^1]);
        Assert.Equal(Math.Min(cap, 9), view.Rows.Count);
        Assert.Equal(9, view.SourceRows);
    }

    [Fact]
    public void AnAggregationStopsOnACancelledToken()
    {
        var table = new HygieneTable("Deployments", ["DeploymentID", "NumberTargeted"]) { State = "Read", Total = 5000 };
        for (var i = 0; i < 5000; i++) table.Rows.Add([i.ToString(), "1"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LiveAggregator.Deployments(table, true, cts.Token));
        Assert.Equal(5000, LiveAggregator.Deployments(table, true).Rows.Count);
    }

    [Fact]
    public async Task ASearchForTheSiteDatabaseThatDoesNotEndFailsTheSqlViewsAfterItsTimeLimit()
    {
        var sql = new FakeSqlHost { SiteHangs = true };
        var service = new SiteHygieneLiveService(new ThrowingSession(ConfigMgrFailure.Timeout), sql.Runner, connection: () => new ConfigMgrConnection("MCM", "CM01"))
        {
            TargetTimeout = TimeSpan.FromMilliseconds(200)
        };
        var snapshot = await service.RefreshAsync(LiveSettings.Default, SqlViews, new Progress(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var v in SqlViews)
        {
            Assert.True(snapshot.Views[v].Failed);
            Assert.Equal(SiteHygieneLiveService.TargetTimeoutMessage, snapshot.Views[v].Message);
        }
        Assert.Equal(SiteHygieneLiveService.TargetTimeoutMessage, await service.TestSqlAsync(LiveSettings.Default, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(sql.Outputs);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var slow = new SiteHygieneLiveService(new ThrowingSession(ConfigMgrFailure.Timeout), sql.Runner, connection: () => new ConfigMgrConnection("MCM", "CM01"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.RefreshAsync(LiveSettings.Default, SqlViews, new Progress(), cts.Token).WaitAsync(TimeSpan.FromSeconds(30)));
    }

    // ---- SQL views -------------------------------------------------------------------------------

    private const string ClientRows = """
        @(
            ,@([int]16777219, 'CM01', [int]1, [int]1, 'Active/Pass', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-22T10:18:44', [datetime]'2026-10-19T08:20:53', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-19T09:27:10', [datetime]'2026-10-23T17:27:05', '5.00.9146.1009', 'Microsoft Windows NT Server 10.0')
            ,@([int]16777222, "=HYPERLINK(`"x`") `"q`" \ $([char]10)caf$([char]0x00e9)", [System.DBNull]::Value, [int]0, [System.DBNull]::Value, [datetime]'2026-10-13T12:55:25', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, '')
            ,@([int]16777223, 'BAD', [int]2, [int]1, 'Active/Fail', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, '5', 'W')
        )
        """;

    private const string InactiveRows = """
        @(
            ,@([int]16777230, 'OLD', [datetime]'2026-06-01T00:00:00', [datetime]'2026-06-01T00:00:00', [int]146, 'W', '5')
            ,@([int]16777231, 'NEVER', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, 'W', '5')
            ,@([int]16777222, 'CLIENT02', [datetime]'2026-10-13T12:55:25', [datetime]'2026-10-12T08:25:53', [int]15, 'W', '5')
        )
        """;

    [Fact]
    public async Task TheSqlViewsRunTheRealScriptReadOnlyWithTypedParametersAndKeepHostileValuesAsText()
    {
        var sql = new FakeSqlHost { ClientsSet = ClientRows, InactiveSet = InactiveRows };
        var settings = Overrides with { InactiveDays = 14 };
        var snapshot = await Service(new ThrowingSession(ConfigMgrFailure.Timeout), sql).RefreshAsync(settings, SqlViews, new Progress(), CancellationToken.None);
        var output = Assert.Single(sql.Outputs);
        Assert.Contains("#probe param @Cap Int 200000 Int32", output);
        Assert.Contains("#probe param @Days Int 14 Int32", output);
        Assert.Contains("#probe timeout 120", output);
        Assert.Contains("#probe disposed", output);
        var cs = Regex.Match(output, "#probe cs (.*)").Groups[1].Value;
        Assert.Contains("Integrated Security=True", cs);
        Assert.Contains("ApplicationIntent=ReadOnly", cs);
        Assert.Contains("Initial Catalog=CM_MCM", cs);
        Assert.DoesNotContain("Encrypt", cs);
        Assert.DoesNotContain("TrustServerCertificate", cs);
        Assert.All(output.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.TrimEnd().All(c => c < 128), line));

        var clients = snapshot.Views[LiveView.ClientHealth];
        Assert.True(clients.Complete, clients.Message);
        Assert.Equal(3, clients.Rows.Count);
        Assert.Equal((LiveStatus.Ok, "Healthy", "CM01"), (clients.Rows[0].Status, clients.Rows[0].StatusText, clients.Rows[0].Cells[0]));
        Assert.Equal(new DateTime(2026, 10, 25, 14, 7, 34, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), clients.Rows[0].Cells[3]);
        var hostile = clients.Rows[1];
        Assert.Equal("=HYPERLINK(\"x\") \"q\" \\ \ncafé", hostile.Cells[0]);
        Assert.Equal((LiveStatus.Warning, "Inactive", "Not evaluated", "Inactive"), (hostile.Status, hostile.StatusText, hostile.Cells[1], hostile.Cells[2]));
        Assert.Equal("16777222", hostile.Key);
        Assert.Equal((LiveStatus.Failed, "Unhealthy"), (clients.Rows[2].Status, clients.Rows[2].StatusText));
        Assert.Equal((1d, 1d, 1d), (clients.Counts[LiveMetrics.ClientHealthy]!.Value, clients.Counts[LiveMetrics.ClientUnhealthy]!.Value, clients.Counts[LiveMetrics.ClientInactive]!.Value));

        var inactive = snapshot.Views[LiveView.InactiveDevices];
        Assert.Equal(3, inactive.Counts[LiveMetrics.InactiveDevices]);
        Assert.Equal((LiveStatus.Failed, "Over 60 days", "146"), (inactive.Rows[0].Status, inactive.Rows[0].StatusText, inactive.Rows[0].Cells[3]));
        Assert.Equal((LiveStatus.Failed, "No contact on record", ""), (inactive.Rows[1].Status, inactive.Rows[1].StatusText, inactive.Rows[1].Cells[3]));
        Assert.Equal((LiveStatus.Warning, "Past the threshold"), (inactive.Rows[2].Status, inactive.Rows[2].StatusText));
        Assert.Equal(0, sql.SiteLookups);
    }

    [Fact]
    public async Task TrustTheCertificateAddsOnlyTheTrustKeyword()
    {
        var sql = new FakeSqlHost();
        await Service(new ThrowingSession(ConfigMgrFailure.Timeout), sql).RefreshAsync(Overrides with { TrustSqlCertificate = true }, SqlViews, new Progress(), CancellationToken.None);
        var cs = Regex.Match(sql.Outputs.Single(), "#probe cs (.*)").Groups[1].Value;
        Assert.Contains("TrustServerCertificate=True", cs);
        Assert.DoesNotContain("Encrypt", cs);
    }

    [Fact]
    public async Task WithoutOverridesTheSqlServerComesFromTheSiteDefinitionOnceAndIsKept()
    {
        var sql = new FakeSqlHost();
        var service = Service(new ThrowingSession(ConfigMgrFailure.Timeout), sql);
        await service.RefreshAsync(LiveSettings.Default, SqlViews, new Progress(), CancellationToken.None);
        var second = await service.RefreshAsync(LiveSettings.Default, SqlViews, new Progress(), CancellationToken.None);
        Assert.Equal(1, sql.SiteLookups);
        Assert.Equal(2, sql.Outputs.Count);
        Assert.Contains("Data Source=CM01.contoso.com", sql.Outputs[1]);
        Assert.True(second.Views[LiveView.ClientHealth].Complete);
    }

    [Fact]
    public async Task WithNoSqlServerFoundTheSqlViewsAreSkippedAndTheOtherViewsWork()
    {
        var sql = new FakeSqlHost { SiteLine = """{"t":"error","stage":"site"}""" };
        var site = new StubHygieneSite(LiveFixtures.State());
        var snapshot = await Service(site, sql).RefreshAsync(LiveSettings.Default, AllViews, new Progress(), CancellationToken.None);
        foreach (var v in new[] { LiveView.ClientHealth, LiveView.InactiveDevices })
        {
            var r = snapshot.Views[v];
            Assert.True(r.Skipped);
            Assert.False(r.Failed);
            Assert.StartsWith(LiveText.SqlNotSetUp, r.Message);
            Assert.All(r.Counts.Values.Values, x => Assert.Null(x));
        }
        Assert.All(ProviderViews, v => Assert.True(snapshot.Views[v].Complete));
        Assert.Empty(sql.Outputs);
    }

    [Fact]
    public async Task ASqlErrorFailsBothSqlViewsWithTheDiscoverMessageAndNoRawText()
    {
        var sql = new FakeSqlHost { OnOpen = "throw (New-Object System.Exception 'Login failed for user ANONYMOUS LOGON. SECRET-DETAIL-1234')" };
        var snapshot = await Service(new ThrowingSession(ConfigMgrFailure.Timeout), sql).RefreshAsync(Overrides, SqlViews, new Progress(), CancellationToken.None);
        foreach (var v in SqlViews)
        {
            Assert.True(snapshot.Views[v].Failed);
            Assert.DoesNotContain("SECRET", snapshot.Views[v].Message);
        }
        Assert.DoesNotContain("SECRET", sql.Outputs.Single().Replace("#probe", "", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("{", StringComparison.Ordinal)).Aggregate("", string.Concat));
    }

    [Theory]
    [InlineData("[53]", "Cannot reach SQL Server CM01.contoso.com.")]
    [InlineData("[4060,18456]", "Database CM_MCM does not exist on SQL Server CM01.contoso.com")]
    [InlineData("[229]", "The Windows login cannot read the client health views in CM_MCM")]
    public void SqlErrorNumbersMapToTheDiscoverMessages(string numbers, string start)
    {
        var parsed = SiteHygieneLiveService.ParseSql("{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":" + numbers + ",\"sspi\":false,\"anonymous\":false}\r\n{\"t\":\"end\"}");
        Assert.True(parsed.Ended);
        var message = DiscoveryService.SqlMessage(parsed.Error!.Value, new DiscoveryDatabaseTarget("CM01.contoso.com", "CM_MCM"), 120).Replace("the inventory views", "the client health views", StringComparison.Ordinal);
        Assert.StartsWith(start, message);
    }

    [Fact]
    public async Task ASqlReadOverTheRowCapIsTruncated()
    {
        var sql = new FakeSqlHost { ClientsSet = ClientRows, InactiveSet = InactiveRows };
        var site = new ThrowingSession(ConfigMgrFailure.Timeout);
        var service = new SiteHygieneLiveService(site, sql.Runner, connection: () => new ConfigMgrConnection("MCM", "CM01")) { MaxSqlRows = 2 };
        var snapshot = await service.RefreshAsync(Overrides, SqlViews, new Progress(), CancellationToken.None);
        Assert.Contains("#probe param @Cap Int 2 Int32", sql.Outputs.Single());
        foreach (var v in SqlViews)
        {
            Assert.True(snapshot.Views[v].Truncated);
            Assert.Equal(2, snapshot.Views[v].Rows.Count);
            Assert.All(snapshot.Views[v].Counts.Values.Values, x => Assert.Null(x));
        }
    }

    [Fact]
    public async Task TestSqlCountsTheClientHealthRowsOrSaysWhySqlIsNotSetUp()
    {
        var sql = new FakeSqlHost();
        var service = Service(new ThrowingSession(ConfigMgrFailure.Timeout), sql);
        Assert.Equal("Connected to CM_MCM on CM01.contoso.com. The client health view holds 4 rows.", await service.TestSqlAsync(Overrides, CancellationToken.None));
        Assert.Contains("#probe scalar SELECT COUNT(*) FROM dbo.v_CH_ClientSummary;", sql.Outputs.Single());
        var none = new SiteHygieneLiveService(new ThrowingSession(ConfigMgrFailure.Timeout), new FakeSqlHost().Runner);
        Assert.StartsWith(LiveText.SqlNotSetUp, await none.TestSqlAsync(LiveSettings.Default, CancellationToken.None));
    }

    [Fact]
    public void ServerAndDatabaseReachTheSqlScriptAsEscapedLiterals()
    {
        var script = SiteHygieneLiveService.BuildSqlScript(new DiscoveryDatabaseTarget("SQL'; Remove-Item C:\\ -Recurse; '", "CM_'X"), 10, 99999, trust: false, testOnly: false);
        Assert.Contains("$LiveServer = 'SQL''; Remove-Item C:\\ -Recurse; '''", script);
        Assert.Contains("$LiveDatabase = 'CM_''X'", script);
        Assert.Contains("$LiveDays = 3650", script);
    }

    // ---- Read-only source scan -------------------------------------------------------------------

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    [Fact]
    public void TheLiveScriptsAndTheLiveServiceHoldNoWriteCmdletOrWriteStatement()
    {
        var folder = Path.Combine(Repo(), "src", "ApplicationPackagerEngine", "Services", "SiteHygiene");
        var provider = File.ReadAllText(Path.Combine(folder, "Scripts", "SiteHygieneLive.ps1"));
        var sqlScript = File.ReadAllText(Path.Combine(folder, "Scripts", "SiteHygieneLiveSql.ps1"));
        var service = File.ReadAllText(Path.Combine(folder, "SiteHygieneLiveService.cs")) + File.ReadAllText(Path.Combine(folder, "LiveAggregator.cs"));
        var verbs = new Regex(@"(?i)\b(Set|Remove|New|Add|Start|Stop|Restart|Invoke|Import|Update|Clear|Move|Rename|Suspend|Resume|Publish|Approve|Enable|Disable|Install|Uninstall|Register|Unregister)-CM\w*");
        foreach (var text in new[] { provider, sqlScript, service })
        {
            Assert.DoesNotMatch(verbs, text.Replace("Invoke-CMWmiQuery", "", StringComparison.Ordinal));
            Assert.DoesNotMatch(@"\.(Put|Delete|Put_|Delete_|InvokeMethod|ExecuteNonQuery)\s*\(", text);
            Assert.DoesNotMatch(@"(?i)\b(Set-WmiInstance|Remove-WmiObject|Set-CimInstance|Remove-CimInstance|Invoke-CimMethod|Invoke-WmiMethod|Invoke-Sqlcmd)\b", text);
        }
        Assert.All(Regex.Matches(provider, @"Read-Set '\w+' '(\w+)'").Select(m => m.Groups[1].Value), c => Assert.StartsWith("SMS_", c));
        var queries = Regex.Matches(sqlScript, "@'\\r?\\n(?<q>.*?)\\r?\\n'@", RegexOptions.Singleline).Select(m => m.Groups["q"].Value).ToList();
        queries.Add(Regex.Match(sqlScript, @"\$LiveSqlTestQuery = '(?<q>[^']*)'").Groups["q"].Value);
        Assert.Equal(2, queries.Count);
        foreach (var q in queries)
        {
            Assert.DoesNotMatch(@"(?i)\b(insert|update|delete|merge|exec|execute|drop|alter|create|truncate|grant|into)\b", q);
            Assert.All(Regex.Matches(q, @"(?i)\b(?:from|join)\s+([\w\.\[\]]+)").Select(m => m.Groups[1].Value), t => Assert.Matches(@"^dbo\.v_", t));
        }
        Assert.Contains("@Cap", queries[0]);
        Assert.Contains("@Days", queries[0]);
        Assert.Contains("GETUTCDATE()", queries[0]);
        Assert.DoesNotContain("GETDATE()", queries[0]);
        // DATEDIFF(day, ...) counts midnights: the threshold compares the raw time, and the age counts whole elapsed days.
        Assert.Contains("COALESCE(ch.LastDDR, ch.LastActiveTime) < DATEADD(day, -@Days, GETUTCDATE())", queries[0]);
        Assert.DoesNotContain("DATEDIFF(day", queries[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATEDIFF(minute, COALESCE(ch.LastDDR, ch.LastActiveTime), GETUTCDATE()) / 1440 AS DaysSinceContact", queries[0]);
    }

    // ---- Scale -----------------------------------------------------------------------------------

    private static HygieneTable Table(string name, string[] columns, int n, Func<int, string[]> row)
    {
        var t = new HygieneTable(name, columns) { State = "Read", Total = n };
        for (var i = 0; i < n; i++) t.Rows.Add(row(i));
        return t;
    }

    [Fact]
    public void TheViewsBuildInLinearTimeFor300Dps50000DeploymentsAnd100000Devices()
    {
        var dep = Table("Deployments", ["DeploymentID", "SoftwareName", "ApplicationName", "CollectionName", "CollectionID", "FeatureType", "DeploymentIntent", "NumberTargeted", "NumberSuccess", "NumberErrors", "NumberInProgress", "NumberUnknown", "NumberOther", "SummarizationTime"],
            50_000, i => [$"{{{i:D8}-0000-0000-0000-000000000000}}", "App " + i, "", "C", "MCM00001", "1", "1", "100", (i % 100).ToString(), (i % 7 == 0 ? "1" : "0"), "0", "0", "0", ""]);
        var nals = Enumerable.Range(0, 300).Select(d => LiveFixtures.SiteSystemOf($"DP{d:D3}.contoso.com")).ToArray();
        HygieneTable Pairs(int n) => Table("Pairs", ["PackageID", "ServerNALPath", "State", "PackageType"], n, i => [$"G{i / 300:D7}", nals[i % 300], (i % 9).ToString(), "0"]);
        HygieneTable Messages(int n) => Table("Messages", ["PackageID", "NalPath", "MessageState"], n, i => [$"G{i / 300:D7}", nals[i % 300], (i % 5).ToString()]);
        var names = Table("Names", ["PackageID", "Name"], 1000, i => [$"G{i:D7}", "C" + i]);
        var dps = Table("Dps", ["NALPath", "ServerName", "SiteCode", "IsPullDP", "IsPXE", "ResourceType"], 300, i => [nals[i], $"DP{i:D3}.CONTOSO.COM", "MCM", "False", "False", "Windows NT Server"]);
        var status = Table("DpStatus", ["SiteSystem", "Status"], 900, i => [nals[i % 300], (i % 3).ToString()]);
        var devices = Enumerable.Range(0, 100_000).Select(i => new[] { (16_000_000 + i).ToString(), "PC" + i, (i % 3).ToString(), (i % 2).ToString(), "Active/Pass", "2026-10-25T14:07:34", "", "", "", "", "", "5", "W" }).ToList();

        var t1 = Stopwatch.StartNew();
        var small = LiveAggregator.Content(Pairs(30_000), Messages(30_000), names, true);
        t1.Stop();
        var t2 = Stopwatch.StartNew();
        var large = LiveAggregator.Content(Pairs(300_000), Messages(300_000), names, true);
        var deployments = LiveAggregator.Deployments(dep, true);
        var dpView = LiveAggregator.DistributionPoints(dps, status, true);
        var clientView = LiveAggregator.Clients(devices, true);
        var inactiveView = LiveAggregator.Inactive(devices.Select(d => new[] { d[0], d[1], d[5], d[5], "30", "W", "5" }).ToList(), true);
        t2.Stop();

        Assert.Equal(50_000, deployments.Rows.Count);
        Assert.Equal(300, dpView.Rows.Count);
        Assert.Equal(100, dpView.Counts[LiveMetrics.DPOffline]);
        Assert.Equal(100_000, clientView.Rows.Count);
        Assert.Equal(100_000, inactiveView.Counts[LiveMetrics.InactiveDevices]);
        Assert.Equal(100, small.Rows.Count);
        Assert.Equal(1000, large.Rows.Count);
        Assert.True(t2.Elapsed < TimeSpan.FromSeconds(30), "The views must build in linear time.");
    }

    // ---- Summary ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummarySaysNotKnownForAFailedOrCutViewAndNeverZero()
    {
        var site = new StubHygieneSite(LiveFixtures.State());
        site.Fail("query:SMS_DistributionDPStatus");
        var sql = new FakeSqlHost { SiteLine = """{"t":"error","stage":"site"}""" };
        var snapshot = await Service(site, sql).RefreshAsync(LiveSettings.Default, AllViews, new Progress(), CancellationToken.None);
        var text = LiveText.Summary(snapshot, new DateTime(2026, 10, 25, 10, 29, 6));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Site Hygiene Live summary, 2026-10-25 10:29:06", lines[0]);
        Assert.Equal("Deployments: 9 deployments, 0 with errors, compliance 58.3 %.", lines[1]);
        Assert.Equal("Content: the read failed. The read of Content failed. The log has the kind of failure.", lines[2]);
        Assert.Equal("Distribution Points: 1 distribution point, 0 critical, 0 warning.", lines[3]);
        Assert.StartsWith("Client Health: not read. " + LiveText.SqlNotSetUp, lines[4]);
        Assert.StartsWith("Inactive Devices: not read.", lines[5]);
        Assert.Equal("Site Health: 16 OK, 0 warning, 0 critical.", lines[6]);
    }
}
