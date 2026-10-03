using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.CommandLine;

/// <summary>The publish run per destination that the Packages page and the command-line build share.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishRunServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-publishrun-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public PublishRunServiceTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, null);
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsService.SettingsPath, """{"SiteCode":"","SiteCodeSaved":true,"Wsus":{"ServerName":"wsus01.lab"}}""");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FakePublisher(Func<WsusPublishResult> publish) : IWsusPublisher
    {
        public int Calls { get; private set; }

        public Task<WsusPublishResult> PublishAsync(StageManifest manifest, string contentFolder, WsusSettings settings,
            Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null)
        {
            Calls++;
            return Task.FromResult(publish());
        }
    }

    private static PublishRunService Service(IWsusPublisher publisher) =>
        new ServiceCollection().AddPackagerServices().AddSingleton(publisher).BuildServiceProvider().GetRequiredService<PublishRunService>();

    private PackagerInfo StagedRow()
    {
        var folder = Path.Combine(_root, "dl", "Contoso", "Tool", "1.0");
        Directory.CreateDirectory(folder);
        var manifestPath = Path.Combine(folder, "stage-manifest.json");
        new ManifestService().Write(manifestPath, new StageManifest { AppName = "Tool", Publisher = "Contoso", SoftwareVersion = "1.0" });
        return new PackagerInfo { Uid = "byo-x", Application = "Tool", Script = "tool.msi", StagedManifestPath = manifestPath };
    }

    [Fact]
    public async Task AWsusRowReusesItsStageAndReportsThePublishedOutcome()
    {
        var id = Guid.NewGuid();
        var publisher = new FakePublisher(() => new WsusPublishResult(id, "Tool 1.0", WsusPublishOutcome.Published, "", [], [], [],
            "published " + id, WsusRuleSource.Detection, []));
        var row = StagedRow();
        var log = new List<string>();

        var run = await Service(publisher).PublishWsusRowAsync(row, null, log.Add, CancellationToken.None);

        Assert.False(run.StageFailed);
        Assert.Equal(1, publisher.Calls);
        Assert.Equal((WsusStepStatus.Published, id.ToString()), (run.Step!.Status, run.Step.PackageId));
        Assert.Equal((PublishRowOutcomeKind.Success, "Published"), (run.Outcome!.Kind, row.Status));
        Assert.Equal($"  Stage manifest found: {row.StagedManifestPath}", log[0]);
        Assert.Equal("WSUS publish: " + run.Step.Message, log[^1]);
    }

    [Fact]
    public async Task AWsusRefusalIsNotSupportedWithItsCodes()
    {
        var refusal = new WsusRefusedException([new WsusFinding(WsusSeverity.Blocking, WsusRefusalCodes.CustomInstall, "install.ps1 runs extra steps")]);
        var row = StagedRow();

        var run = await Service(new FakePublisher(() => throw refusal)).PublishWsusRowAsync(row, null, _ => { }, CancellationToken.None);

        Assert.True(run.Step!.Refused);
        Assert.Equal(PublishRowOutcomeKind.NotSupported, run.Outcome!.Kind);
        Assert.Equal("not supported: CustomInstall", row.Status);
        Assert.Equal(run.Step.Message, row.StatusDetail);
        Assert.Contains("install.ps1 runs extra steps", row.StatusDetail);
    }

    [Fact]
    public async Task AWsusRepeatPublishSaysAlreadyOnTheServer()
    {
        var id = Guid.NewGuid();
        var publisher = new FakePublisher(() => new WsusPublishResult(id, "Tool 1.0", WsusPublishOutcome.AlreadyPublished, "", [], [], [],
            "already on the server " + id, WsusRuleSource.Detection, []));
        var row = StagedRow();

        var run = await Service(publisher).PublishWsusRowAsync(row, null, _ => { }, CancellationToken.None);

        Assert.Equal((PublishRowOutcomeKind.Success, "Already on the server"), (run.Outcome!.Kind, row.Status));
        Assert.Null(row.StatusDetail);
    }

    [Fact]
    public void ANewStatusClearsTheDetailOfTheOldOne()
    {
        var row = new PackagerInfo { Status = "not supported: CustomInstall", StatusDetail = "full text" };
        row.Status = "Staging...";
        Assert.Null(row.StatusDetail);
    }

    [Fact]
    public void RowStatusPackagedLineAndIntuneNotesKeepThePageTexts()
    {
        Assert.Equal("Packaged", PublishRunService.RowStatus(PublishRunTargets.ConfigMgr, 0));
        Assert.Equal("Package error", PublishRunService.RowStatus(PublishRunTargets.ConfigMgr, 1));
        Assert.Equal("Published", PublishRunService.RowStatus(PublishRunTargets.IntuneOnly, 0));
        Assert.Equal("Publish error", PublishRunService.RowStatus(PublishRunTargets.IntuneOnly, 1));

        Assert.Equal("Packaged. Packaged: Tool 1.0", PublishRunService.PackagedLine(new PackagerExecutionResult { StdOut = "Packaged: Tool 1.0" }));
        Assert.Equal("Packaged. Log: run.out.log", PublishRunService.PackagedLine(new PackagerExecutionResult { StdOut = "x", OutLogPath = @"C:\logs\run.out.log" }));

        var row = new PackagerInfo();
        var log = new List<string>();
        PublishRunService.LogIntuneNotes(row, new PackagerExecutionResult { IntuneWinNote = "built a.intunewin", IntunePublishNote = "published (app id 1)" }, log.Add);
        Assert.Equal(["Intunewin: built a.intunewin", "Intune publish: published (app id 1)"], log);
        Assert.Equal("built a.intunewin — published (app id 1)", row.IntuneNote);
    }

    [Fact]
    public void TheRunGateReadsTheStoredSettings()
    {
        var settings = new SettingsService().Load();
        Assert.Null(PublishRunService.RunGate(PublishRunTargets.WsusOnly, settings));
        Assert.Equal("Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret.",
            PublishRunService.RunGate(PublishRunTargets.IntuneOnly, settings));
        Assert.Equal(PublishActionState.RunGate(PublishRunTargets.ConfigMgr, PublishRunService.ReadinessOf(settings)),
            PublishRunService.RunGate(PublishRunTargets.ConfigMgr, settings));
    }
}
