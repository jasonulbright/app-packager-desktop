using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The History tab and the exports say whether each record's chain is intact, refuse a History
/// Undo of a record that cannot be checked or whose creation proof is gone with a plain message,
/// and name the deployments of a create whose result is not known.
/// </summary>
public sealed class DeploymentHistoryVerificationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dhverify-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private const string SuiteLine = """{"Timestamp":"2026-09-01T10:00:00","User":"LAB\\admin","DeploymentType":"Application","ApplicationName":"Suite App","DeploymentID":16777327,"Result":"Success"}""";
    private const string PreChainLine = """{"Timestamp":"2026-10-01T10:00:00","User":"LAB\\admin","DeploymentType":"Package","ApplicationName":"Old Package","Result":"Success","Event":"Create","Source":"Application Packager","DeploymentUniqueID":"MCM20001"}""";

    private static DeploymentAuditRecord Record(string name, string result = "Success") =>
        new(new DateTime(2026, 10, 20, 8, 0, 0), @"LAB\admin", "Package", name, "Run", "Pilot", "MCM00098", 3, "Available", "Install", null, "MCM20002", result)
        {
            Event = "Create", ObjectId = "MCM0001B"
        };

    // ---- Engine: the read and the reports --------------------------------------------------------

    [Fact]
    public void AReadTellsAnIntactRecordFromAnEditedOneAndFromOnesWrittenWithoutAChain()
    {
        var path = Path.Combine(_folder, "deployment-audit.jsonl");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, SuiteLine + "\r\n" + PreChainLine + "\r\n");
        var log = new DeploymentAuditLog(() => path);
        Assert.True(log.Append(Record("Intact")).Succeeded);
        Assert.True(log.Append(Record("Edited")).Succeeded);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"ApplicationName\":\"Edited\"", "\"ApplicationName\":\"Edited by hand\"", StringComparison.Ordinal));

        var records = log.Read(10);

        Assert.Equal([(false, false), (false, false), (true, true), (false, true)], records.Select(r => (r.Verified == true, r.HasChain)));
        Assert.Equal(["Not verified (no chain)", AuditVerification.BeforeChain, AuditVerification.Verified, AuditVerification.NotVerified], records.Select(AuditVerification.Text));
        Assert.Equal("Not verified (written before the chain)", AuditVerification.BeforeChain);
        Assert.Equal("", AuditVerification.Text(Record("Not read")));
    }

    [Fact]
    public void TheReportsCarryTheVerifiedTextAndTheUnclearIdsAndIgnoreAVerifiedPropertyOfTheLine()
    {
        const string hostile = "=cmd|' /C calc'!A0 <script>alert(1)</script>";
        var forged = DeploymentAuditLog.Parse("""{"ApplicationName":"Forged","Result":"Success","Verified":"Verified","verified":"Verified"}""")! with { Verified = false, HasChain = true };
        var unclear = Record("Unclear", "Unknown") with { Verified = true, HasChain = true, UnclearDeploymentIds = hostile + "; MCM2004F" };
        var writer = new DeploymentReportWriter(_folder, () => new DateTimeOffset(2026, 10, 20, 9, 30, 5, TimeSpan.Zero));

        var csv = File.ReadAllLines(writer.WriteCsv([forged, unclear]));
        var html = File.ReadAllText(writer.WriteHtml([forged, unclear]));

        var header = csv[0].Split(',');
        Assert.Single(header, h => string.Equals(h, "\"Verified\"", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"UnclearDeploymentIds\"", header);
        var verified = Array.IndexOf(header, "\"Verified\"");
        Assert.Equal("\"Not verified\"", csv[1].Split(',')[verified]);
        Assert.Contains("\"Verified\"", csv[2]);
        Assert.Contains("\"'" + hostile.Replace("\"", "\"\"") + "; MCM2004F\"", csv[2]);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;; MCM2004F", html);
        Assert.Contains("<td>Not verified</td>", html);
        Assert.Contains("<th>Verified</th>", html);
    }

    // ---- The page over the real service -----------------------------------------------------------

    private static async Task<DeploymentHelperViewModel> CreatePackageAsync(ServicePageHarness h)
    {
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        return page;
    }

    private static async Task<DeploymentHistoryRow> HistoryCreateAsync(DeploymentHelperViewModel page)
    {
        page.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
        await page.CurrentTab.EnsureLoadedAsync();
        page.History.SelectedRow = page.History.Rows.Single(r => r.Event == "Create");
        return page.History.SelectedRow;
    }

    [Theory]
    [InlineData(false, "Not verified")]
    [InlineData(true, "Not verified (written before the chain)")]
    public async Task AHistoryUndoOfARecordThatCannotBeCheckedIsRefusedWithAPlainMessage(bool stripChain, string verifiedText)
    {
        using var h = new ServicePageHarness();
        var page = await CreatePackageAsync(h);
        var line = File.ReadAllText(h.FirstPath).Trim();
        File.WriteAllText(h.FirstPath, (stripChain ? line[..line.LastIndexOf(",\"Chain\":", StringComparison.Ordinal)] + "}" : line.Replace("\"MemberCount\":0", "\"MemberCount\":1", StringComparison.Ordinal)) + "\r\n");
        h.Plans.Shown.Clear();
        var prepared = h.Service.Prepared.Count;

        var row = await HistoryCreateAsync(page);
        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);

        Assert.Equal(verifiedText, row.VerifiedText);
        Assert.Equal("!", row.Verified);
        Assert.StartsWith(verifiedText, page.History.SelectedDetails);
        Assert.Equal("This record cannot be checked (the log was edited or its chain is broken): Undo from History is refused. Remove the deployment in the console.",
            page.Shared.StatusMessage);
        Assert.Equal(DeploymentHistoryViewModel.ChainRefused, page.Shared.StatusMessage);
        Assert.Empty(h.Plans.Shown);
        Assert.Equal(prepared, h.Service.Prepared.Count);
        Assert.Single(h.Engine.Site.List("Adverts"));
    }

    [Fact]
    public async Task AnIntactRecordShowsVerifiedAndItsMessageInTheDetails()
    {
        using var h = new ServicePageHarness();
        var page = await CreatePackageAsync(h);

        var row = await HistoryCreateAsync(page);

        Assert.Equal(("✓", "Verified"), (row.Verified, row.VerifiedText));
        Assert.StartsWith("Verified" + Environment.NewLine + "Message: Created the Available deployment of package", page.History.SelectedDetails);
        Assert.DoesNotContain("Unclear", page.History.SelectedDetails);
    }

    [Fact]
    public async Task AHistoryUndoWhoseCreationProofIsPurgedShowsTheServiceMessageAndRemovesNothing()
    {
        using var first = new ServicePageHarness();
        await CreatePackageAsync(first);
        using var later = new ServicePageHarness(first.Engine.Site.State);
        later.Settings.DeploymentHelper!.AuditLogPath = first.FirstPath;
        later.Engine.Site.List("StatusMessages").Clear();
        var page = await later.OpenAsync();

        await HistoryCreateAsync(page);
        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);

        var check = later.Service.Prepared.Single().Checks.Single(c => c.Name == "CreatedHere");
        Assert.Equal("Fail", check.Result);
        Assert.Contains("message 30006", check.Message);
        Assert.Equal(DeploymentHistoryViewModel.HistoryUndoRefused(check.Message), page.Shared.StatusMessage);
        Assert.Equal("Undo from History is refused: " + check.Message, page.Shared.StatusMessage);
        Assert.Empty(later.Plans.Shown);
        Assert.Empty(later.Service.Applied);
        Assert.Single(later.Engine.Site.List("Adverts"));
    }

    [Fact]
    public async Task ACreateWhoseResultIsNotKnownNamesTheNewDeploymentsInTheResultAndInTheHistory()
    {
        using var h = new ServicePageHarness();
        h.Engine.Site.SetOption("ForeignOnCreate:Package", true);
        h.Engine.Site.SetOption("NoReturn", true);
        h.Engine.Site.SetOption("CreatedNow", true);
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");

        await form.DeployCommand.ExecuteAsync(null);

        var ids = h.Engine.Site.List("Adverts").Select(d => (string)d!["AdvertisementID"]!).ToList();
        Assert.Equal(2, ids.Count);
        var row = page.Shared.LastResults.Single();
        Assert.False(row.Succeeded);
        Assert.All(ids, id => Assert.Contains(id, row.Message));
        Assert.Equal(string.Join("; ", ids), page.Shared.UnclearDeploymentIds);
        Assert.True(page.Shared.HasUnclearDeploymentIds);

        var history = await HistoryCreateAsync(page);
        Assert.Equal(string.Join("; ", ids), history.Record.UnclearDeploymentIds);
        Assert.EndsWith("Unclear deployment IDs: " + string.Join("; ", ids), page.History.SelectedDetails);
        page.History.TextFilter = ids[1];
        Assert.Single(page.History.Rows);
    }
}
