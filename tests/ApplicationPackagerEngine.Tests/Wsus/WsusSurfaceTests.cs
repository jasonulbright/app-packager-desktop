using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>A WSUS server stand-in that records each call and answers from its fields.</summary>
internal sealed class FakeWsusAdminClient : IWsusAdminClient
{
    public List<string> Calls { get; } = [];
    public WsusSettings? LastSettings { get; private set; }
    public WsusServerStatus? Status { get; set; }
    public Exception? Failure { get; set; }
    public IReadOnlyList<WsusPublishedUpdate> Updates { get; set; } = [];
    public IReadOnlyList<string> GroupNames { get; set; } = ["All Computers", "Pilot"];
    public Func<Guid, WsusItemResult>? ImportResult { get; set; }
    public Func<Guid, WsusItemResult>? StateResult { get; set; }

    private Task<T> Answer<T>(string call, WsusSettings settings, Func<T> value)
    {
        Calls.Add(call);
        LastSettings = settings;
        return Failure is null ? Task.FromResult(value()) : Task.FromException<T>(Failure);
    }

    public Task<WsusServerStatus> GetStatusAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("Status", settings, () => Status!);
    public Task<WsusServerStatus> CreateSelfSignedCertificateAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("CreateSelfSigned", settings, () => Status!);
    public Task<WsusServerStatus> ImportPfxAsync(WsusSettings settings, string pfxPath, string password, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("ImportPfx:" + Path.GetFileName(pfxPath) + ":" + password, settings, () => Status!);
    public Task<string> ExportCertificateAsync(WsusSettings settings, string cerPath, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("ExportCer", settings, () => cerPath);
    public Task<IReadOnlyList<string>> GetComputerGroupNamesAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("Groups", settings, () => GroupNames);
    public Task<IReadOnlyList<WsusPublishedUpdate>> ListAsync(WsusSettings settings, bool includeOtherPublishers = false, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("List:" + includeOtherPublishers, settings, () => Updates);
    public Task<IReadOnlyList<WsusItemResult>> SetStateAsync(WsusSettings settings, IEnumerable<Guid> packageIds, string action, string? groupName = null,
        Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer($"SetState:{action}:{groupName}", settings, () => (IReadOnlyList<WsusItemResult>)packageIds.Select(id => StateResult?.Invoke(id) ?? new WsusItemResult(id.ToString(), "T " + id.ToString()[..4], true, "")).ToList());
    public Task<IReadOnlyList<WsusItemResult>> ImportCatalogUpdatesAsync(WsusSettings settings, IEnumerable<Guid> updateIds, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Answer("Import", settings, () => (IReadOnlyList<WsusItemResult>)updateIds.Select(id => ImportResult?.Invoke(id) ?? new WsusItemResult(id.ToString(), "", true, "")).ToList());
}

/// <summary>Answers every prompt from its fields and records the questions.</summary>
internal sealed class FakeWsusPrompts : IWsusPrompts
{
    public bool Answer { get; set; } = true;
    public List<string> Questions { get; } = [];
    public string? Pfx { get; set; }
    public string? Password { get; set; }
    public string? SavePath { get; set; }
    public string? IdFile { get; set; }
    public List<string> Opened { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message) { Questions.Add(title + ": " + message); return Task.FromResult(Answer); }
    public string? PickPfxFile() => Pfx;
    public Task<string?> AskPasswordAsync(string prompt) { Questions.Add(prompt); return Task.FromResult(Password); }
    public string? PickCertificateSavePath(string defaultFileName) { Questions.Add(defaultFileName); return SavePath; }
    public string? PickIdFile() => IdFile;
    public void OpenUrl(string url) => Opened.Add(url);
}

/// <summary>
/// The WSUS Publishing section, the WSUS Updates window and Catalog Import, driven
/// through their view models with a stand-in server: no window and no WSUS API.
/// </summary>
public sealed class WsusSurfaceTests
{
    private static readonly WsusApiStatus NoApi = new(false, "", "", WsusApiProbe.NotInstalledMessage);
    private static readonly WsusApiStatus Api = new(true, "10.0.17763.1", @"C:\Windows\Microsoft.NET\assembly\x.dll", "");

    private static WsusPublishingEditor Editor(FakeWsusAdminClient client, WsusApiStatus? api = null, FakeWsusPrompts? prompts = null)
    {
        var editor = new WsusPublishingEditor(client, () => api ?? Api, prompts ?? new FakeWsusPrompts());
        editor.Load(new WsusSettings());
        return editor;
    }

    private static WsusServerStatus Connected(WsusCertificateInfo? cert = null, string message = "", bool expired = false) =>
        new(true, "wsus01", "10.0.17763.1", "Administrator", false, cert, expired, message, Api);

    // ── WSUS Publishing ─────────────────────────────────────────

    [Fact]
    public void TheDefaultsAreAnUnsetServerOnPort8530WithTheUpdatesClassification()
    {
        var editor = Editor(new FakeWsusAdminClient());
        Assert.Equal(("", "8530", false, "Updates"), (editor.ServerName, editor.Port, editor.UseSsl, editor.Classification));
        Assert.Equal(WsusClassifications.Names.Count, WsusPublishingEditor.ClassificationOptions.Count);
        Assert.Equal("Security Updates", WsusPublishingEditor.ClassificationOptions.Single(o => o.Name == "SecurityUpdates").Label);
        Assert.Contains("older version", WsusPublishingEditor.PolicyText);
        Assert.Contains("AcceptTrustedPublisherCerts = 1 (DWORD)", WsusPublishingEditor.ClientTrustText);
    }

    [Fact]
    public void WithoutTheApiTheServerButtonsAreDisabledAndTheStatusSaysNotInstalled()
    {
        var editor = Editor(new FakeWsusAdminClient(), NoApi);
        Assert.False(editor.ServerButtonsEnabled);
        Assert.False(editor.TestConnectionCommand.CanExecute(null));
        Assert.False(editor.LoadGroupsCommand.CanExecute(null));
        Assert.Contains("not installed", editor.StatusText);
        Assert.Contains("Rsat.WSUS.Tools", editor.StatusText);
    }

    [Fact]
    public void ThePortFollowsTheSslChoiceWithoutAClickAndKeepsATypedPort()
    {
        var editor = Editor(new FakeWsusAdminClient());
        editor.UseSsl = true;
        Assert.Equal("8531", editor.Port);
        editor.UseSsl = false;
        Assert.Equal("8530", editor.Port);
        editor.Port = "9000";
        editor.UseSsl = true;
        Assert.Equal("9000", editor.Port);
    }

    [Fact]
    public void TheBoxesRoundTripTrimmedAndANonNumericPortKeepsTheStoredOne()
    {
        var editor = Editor(new FakeWsusAdminClient());
        editor.Load(new WsusSettings { ServerName = "wsus01", PortNumber = 8531, UseSsl = true });
        editor.ServerName = " wsus01.contoso.com ";
        editor.Classification = "SecurityUpdates";
        editor.ApprovalGroup = " Pilot ";
        editor.DeclineSuperseded = true;
        var saved = editor.ToSettings();
        Assert.Equal(("wsus01.contoso.com", 8531, true, "SecurityUpdates", "Pilot", true),
            (saved.ServerName, saved.PortNumber, saved.UseSsl, saved.Classification, saved.ApprovalGroup, saved.DeclineSuperseded));
        editor.Port = "abc";
        Assert.Equal(8531, editor.ToSettings().PortNumber);
    }

    [Fact]
    public async Task TestConnectionWithoutAServerAsksForItAndCallsNothing()
    {
        var client = new FakeWsusAdminClient();
        var editor = Editor(client);
        await editor.TestConnectionCommand.ExecuteAsync(null);
        Assert.Empty(client.Calls);
        Assert.Equal("\u2717 Enter the WSUS server first.", editor.StatusText);
    }

    [Fact]
    public async Task TestConnectionShowsTheServerTheRoleAndTheCertificate()
    {
        var cert = new WsusCertificateInfo("CN=WSUS Publishers Self-signed", "CN=WSUS Publishers Self-signed", "ABCD", DateTimeOffset.Now.AddYears(-1), new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero), 2048);
        var client = new FakeWsusAdminClient { Status = Connected(cert) };
        var editor = Editor(client);
        editor.ServerName = "wsus01";
        editor.UseSsl = true;
        await editor.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(("wsus01", 8531, true), (client.LastSettings!.ServerName, client.LastSettings.PortNumber, client.LastSettings.UseSsl));
        Assert.Equal("\u2713 Connected  -  wsus01, WSUS 10.0.17763.1, role Administrator, unencrypted connection.", editor.StatusText);
        Assert.StartsWith("Subject: CN=WSUS Publishers Self-signed   Thumbprint: ABCD   Valid to: 2030-01-02   Key: 2048 bits   (self-signed)", editor.CertificateText);
        Assert.False(editor.Busy);
    }

    [Fact]
    public void AStatusWithoutACertificateOrConnectionSaysSo()
    {
        var editor = Editor(new FakeWsusAdminClient());
        editor.ShowStatus(Connected());
        Assert.Equal("\u2717 No signing certificate is set on this server. Publishing fails until one is created or imported.", editor.CertificateText);
        editor.ShowStatus(Connected(message: "The signing certificate could not be read: access denied"));
        Assert.Equal("The signing certificate could not be read; see the status line.", editor.CertificateText);
        editor.ShowStatus(new WsusServerStatus(false, "wsus01", "", "", false, null, false, WsusApiProbe.NotInstalledMessage, NoApi));
        Assert.Equal("\u2717 " + WsusApiProbe.NotInstalledMessage, editor.StatusText);
        Assert.Equal("Not connected.", editor.CertificateText);
    }

    [Fact]
    public async Task AServerErrorLandsInTheStatusLine()
    {
        var client = new FakeWsusAdminClient { Failure = new WsusOperationException(WsusErrorKinds.ApiMissing, WsusApiProbe.NotInstalledMessage) };
        var editor = Editor(client);
        editor.ServerName = "wsus01";
        await editor.LoadGroupsCommand.ExecuteAsync(null);
        Assert.Equal("\u2717 " + WsusApiProbe.NotInstalledMessage, editor.StatusText);
    }

    [Fact]
    public async Task CreateSelfSignedAsksFirstAndNoChangesNothing()
    {
        var prompts = new FakeWsusPrompts { Answer = false };
        var client = new FakeWsusAdminClient { Status = Connected() };
        var editor = Editor(client, prompts: prompts);
        editor.ServerName = "wsus01";
        await editor.CreateSelfSignedCommand.ExecuteAsync(null);
        Assert.Empty(client.Calls);
        Assert.StartsWith("Create Signing Certificate: Have wsus01 create a self-signed signing certificate", prompts.Questions[0]);
        prompts.Answer = true;
        await editor.CreateSelfSignedCommand.ExecuteAsync(null);
        Assert.Equal(["CreateSelfSigned"], client.Calls);
    }

    [Fact]
    public async Task ImportPfxAsksForThePasswordAndConfirmsBeforeTheServerChanges()
    {
        var prompts = new FakeWsusPrompts { Pfx = @"C:\certs\signer.pfx", Password = "s3cret" };
        var client = new FakeWsusAdminClient { Status = Connected() };
        var editor = Editor(client, prompts: prompts);
        editor.ServerName = "wsus01";
        await editor.ImportPfxCommand.ExecuteAsync(null);
        Assert.Equal("Password for signer.pfx", prompts.Questions[0]);
        Assert.StartsWith("Import Signing Certificate: Make this certificate the signing certificate of wsus01?", prompts.Questions[1]);
        Assert.Equal(["ImportPfx:signer.pfx:s3cret"], client.Calls);

        prompts.Password = null;
        await editor.ImportPfxCommand.ExecuteAsync(null);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task ExportAndLoadGroupsReportWhatTheyDid()
    {
        var prompts = new FakeWsusPrompts { SavePath = @"C:\out\wsus.cer" };
        var client = new FakeWsusAdminClient();
        var editor = Editor(client, prompts: prompts);
        editor.ServerName = "wsus01";
        await editor.ExportCertificateCommand.ExecuteAsync(null);
        Assert.Equal("WSUS-signing-wsus01.cer", prompts.Questions[0]);
        Assert.Equal("\u2713 Saved C:\\out\\wsus.cer", editor.StatusText);

        editor.ApprovalGroup = "Typed";
        await editor.LoadGroupsCommand.ExecuteAsync(null);
        Assert.Equal(["All Computers", "Pilot"], editor.Groups);
        Assert.Equal("Typed", editor.ApprovalGroup);
        Assert.Equal("2 computer group(s) loaded.", editor.StatusText);
    }

    // ── WSUS Updates ────────────────────────────────────────────

    private static WsusPublishedUpdate Update(string title, Guid id) =>
        new(id.ToString(), title, "Update", "AppPackager:catalog:x/default", "1.0", DateTimeOffset.Now, "2026-10-03 10:00", "", false, false, false);

    [Fact]
    public async Task TheWindowListsTheUpdatesAndTheGroupsAndShowsAListingFailure()
    {
        var id = Guid.NewGuid();
        var client = new FakeWsusAdminClient { Updates = [Update("Contoso 1.0", id)] };
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01", ApprovalGroup = "Pilot" }, new FakeWsusPrompts());
        Assert.Equal("Pilot", vm.GroupText);
        await vm.LoadAsync();
        Assert.Single(vm.Updates);
        Assert.Equal(["All Computers", "Pilot"], vm.Groups);
        Assert.Equal("1 update(s) on wsus01.", vm.SummaryText);

        vm.ShowOtherPublishers = true;
        await vm.LoadAsync();
        Assert.Contains("List:True", client.Calls);

        client.Failure = new WsusOperationException(WsusErrorKinds.ApiMissing, WsusApiProbe.NotInstalledMessage);
        await vm.LoadAsync();
        Assert.Empty(vm.Updates);
        Assert.Equal("Listing failed: " + WsusApiProbe.NotInstalledMessage, vm.SummaryText);
    }

    [Fact]
    public async Task AnActionNeedsASelectionAndApproveNeedsAGroup()
    {
        var client = new FakeWsusAdminClient();
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts());
        await vm.DeclineCommand.ExecuteAsync(null);
        Assert.Equal("Select one or more updates first.", vm.SummaryText);
        vm.SetSelection([Update("A", Guid.NewGuid())]);
        vm.GroupText = "  ";
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.Equal("Enter or choose a computer group to approve for.", vm.SummaryText);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("SetState", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Expire", "Expire Updates", "Expiry is permanent")]
    [InlineData("Remove", "Remove Updates", "It cannot be undone")]
    public async Task ExpireAndRemoveAskFirst(string action, string title, string text)
    {
        var prompts = new FakeWsusPrompts { Answer = false };
        var client = new FakeWsusAdminClient();
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, prompts);
        vm.SetSelection([Update("A", Guid.NewGuid()), Update("B", Guid.NewGuid())]);
        var command = action == "Expire" ? vm.ExpireCommand : vm.RemoveCommand;
        await command.ExecuteAsync(null);
        Assert.StartsWith($"{title}: {action} 2 update(s)", prompts.Questions[0]);
        Assert.Contains(text, prompts.Questions[0]);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("SetState", StringComparison.Ordinal));
        prompts.Answer = true;
        await command.ExecuteAsync(null);
        Assert.Contains($"SetState:{action}:", client.Calls);
    }

    [Fact]
    public async Task EachIdReportsItsOwnResult()
    {
        var bad = Guid.NewGuid();
        var client = new FakeWsusAdminClient
        {
            StateResult = id => id == bad ? new WsusItemResult(id.ToString(), "Bad one", false, "referenced by another update") : new WsusItemResult(id.ToString(), "Good", true, "")
        };
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts());
        vm.SetSelection([Update("Good", Guid.NewGuid()), Update("Bad one", bad)]);
        vm.GroupText = "Pilot";
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.Contains("SetState:Approve:Pilot", client.Calls);
        Assert.Equal("Approve: 1 of 2 update(s) done. Bad one: referenced by another update", vm.SummaryText);
    }

    // ── Catalog Import ──────────────────────────────────────────

    [Fact]
    public async Task OneImportPerIdWithAResultLineEach()
    {
        var first = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var second = Guid.Parse("22345678-90ab-cdef-1234-567890abcdef");
        var client = new FakeWsusAdminClient { ImportResult = id => new WsusItemResult(id.ToString(), "", id == first, id == first ? "" : WsusApiProbe.NotInstalledMessage) };
        var vm = new WsusCatalogImportViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts())
        {
            InputText = $"{first}\r\nhttps://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid={second}\r\n{first}"
        };
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(2, client.Calls.Count(c => c == "Import"));
        var lines = vm.ResultsText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"{first}  imported", lines[0]);
        Assert.Equal($"{second}  FAILED  {WsusApiProbe.NotInstalledMessage}", lines[1]);
        Assert.Equal(WsusCatalogImportViewModel.FinishedText, vm.StatusText);
    }

    [Fact]
    public async Task TextWithoutIdsImportsNothingAndTheCatalogOpens()
    {
        var prompts = new FakeWsusPrompts();
        var client = new FakeWsusAdminClient();
        var vm = new WsusCatalogImportViewModel(client, new WsusSettings { ServerName = "wsus01" }, prompts) { InputText = "no ids here" };
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Empty(client.Calls);
        Assert.Equal("No update IDs found in the text.", vm.StatusText);
        vm.OpenCatalogCommand.Execute(null);
        Assert.Equal([WsusCatalogImportViewModel.CatalogUrl], prompts.Opened);
    }

    // ── API probe and the publish step ──────────────────────────

    [Fact]
    public void TheProbeReadsTheFirstAssemblyThatExistsAndElseGivesTheAdaptersInstallHint()
    {
        using var temp = new TempFolder();
        var missing = Path.Combine(temp.Path, "none", WsusApiProbe.AssemblyFileName);
        var none = WsusApiProbe.Probe([missing]);
        Assert.False(none.Available);
        Assert.Equal(WsusApiProbe.NotInstalledMessage, none.Reason);
        string[] parts =
        [
            "The WSUS administration API is not installed on this computer. Install the WSUS console: on Windows 10 or 11, ",
            "Settings > System > Optional features > RSAT: Windows Server Update Services Tools (Add-WindowsCapability -Online -Name Rsat.WSUS.Tools~~~~0.0.1.0); ",
            "on Windows Server, Install-WindowsFeature UpdateServices-UI."
        ];
        Assert.Equal(WsusApiProbe.NotInstalledMessage, string.Concat(parts));
        var script = WsusAdapterRunner.ScriptText();
        Assert.All(parts, part => Assert.Contains(part, script));

        var present = Path.Combine(temp.Path, WsusApiProbe.AssemblyFileName);
        System.IO.File.WriteAllText(present, "x");
        var found = WsusApiProbe.Probe([missing, present]);
        Assert.True(found.Available);
        Assert.Equal(present, found.Location);
    }

    private sealed class FakePublisher(Func<WsusPublishResult> publish) : IWsusPublisher
    {
        public Task<WsusPublishResult> PublishAsync(StageManifest manifest, string contentFolder, WsusSettings settings,
            Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null) => Task.FromResult(publish());
    }

    [Fact]
    public async Task ARefusalBecomesNotSupportedWithItsCodesAndTheFullText()
    {
        var refusal = new WsusRefusedException([new WsusFinding(WsusSeverity.Blocking, WsusRefusalCodes.CustomInstall, "install.ps1 runs extra steps")]);
        var step = await WsusPublishStep.RunAsync(new FakePublisher(() => throw refusal), new StageManifest(), "c:\\x", new WsusSettings { ServerName = "wsus01" });
        Assert.True(step.Refused);
        Assert.Equal(["CustomInstall"], step.RefusalCodes);
        Assert.Equal("not supported: CustomInstall", step.NotSupportedReason);
        Assert.Equal("not supported: CustomInstall. This application cannot be published to WSUS: CustomInstall: install.ps1 runs extra steps", step.Message);
        Assert.Equal("WSUS: not supported", PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: step).Status);
    }

    [Fact]
    public async Task AServerFailureIsAFailedStepAndAPublishCarriesItsPackageId()
    {
        var failed = await WsusPublishStep.RunAsync(new FakePublisher(() => throw new WsusOperationException(WsusErrorKinds.Connect, "no route")),
            new StageManifest(), "c:\\x", new WsusSettings());
        Assert.Equal((WsusStepStatus.Failed, "no route"), (failed.Status, failed.Message));
        Assert.Equal("Publish error", PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: failed).Status);

        var id = Guid.NewGuid();
        var published = await WsusPublishStep.RunAsync(new FakePublisher(() => new WsusPublishResult(id, "T", WsusPublishOutcome.Published, "", [], [], [],
            "published " + id, WsusRuleSource.Detection, [])), new StageManifest(), "c:\\x", new WsusSettings());
        Assert.Equal((WsusStepStatus.Published, id.ToString()), (published.Status, published.PackageId));
    }
}
