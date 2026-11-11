using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using static ApplicationPackagerEngine.Tests.Wsus.WsusTestSupport;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The publish sequence against a stand-in server: the stand-in answers the
/// PublishPrepare and Publish adapter operations from the updates it holds,
/// and records every plan, approval and decline it receives.
/// </summary>
public class WsusPublishServiceTests : IDisposable
{
    private const string Identity = "AppPackager:catalog:package-contosotool/default";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly string _content;
    private readonly WsusSettings _settings = new() { ServerName = "wsus01.contoso.com", PortNumber = 8531, UseSsl = true, Classification = "Updates" };

    private readonly List<JsonObject> _serverUpdates = [];
    private JsonObject? _certificate = Certificate(Now.AddDays(-1), Now.AddYears(1));
    private bool _certificateReadable = true;
    private string? _prepareErrorKind;
    private string _prepareError = "";
    private string? _publishError;
    private string? _approvalError;
    private string? _visibilityError;
    private readonly Dictionary<string, string> _declineErrors = [];
    private readonly List<JsonObject> _plans = [];
    private readonly List<string> _approvals = [];
    private readonly List<string> _declines = [];
    private readonly List<JsonObject> _publishRequests = [];

    public WsusPublishServiceTests() => _content = NewContentFolder(_temp);

    public void Dispose() => _temp.Dispose();

    private static JsonObject Certificate(DateTimeOffset notBefore, DateTimeOffset notAfter) => new()
    {
        ["Subject"] = "CN=WSUS Publishers Self-signed", ["Issuer"] = "CN=WSUS Publishers Self-signed", ["Thumbprint"] = "AB12",
        ["NotBefore"] = notBefore.ToString("o"), ["NotAfter"] = notAfter.ToString("o"), ["KeyLength"] = 2048
    };

    private static JsonObject Update(Guid id, string title, string description, bool declined = false, string state = "Published") => new()
    {
        ["PackageId"] = id.ToString(), ["Title"] = title, ["Description"] = description, ["Created"] = "2026-09-01T00:00:00.0000000Z",
        ["Declined"] = declined, ["PublicationState"] = state, ["ApprovedGroups"] = new JsonArray(), ["ApprovalsRead"] = true
    };

    private static string Line(string version, string type = "Update", string identity = Identity) =>
        WsusIdentity.IdentityLine(identity, type, version);

    private WsusPublishService Service(out FakeAdapterRunner runner)
    {
        runner = new FakeAdapterRunner(Handle);
        return new WsusPublishService(runner, new WsusRuleBuilder(new ThrowingMsi()), () => Now);
    }

    private WsusAdapterResult Handle(string operation, JsonObject request, string? stdin)
    {
        if (operation == "PublishPrepare")
        {
            if (_prepareErrorKind is not null) return new WsusAdapterResult(false, _prepareErrorKind, _prepareError, null);
            return FakeAdapterRunner.Ok(new JsonObject
            {
                ["ServerName"] = "wsus01.contoso.com",
                ["CertificateReadable"] = _certificateReadable,
                ["CertificateError"] = _certificateReadable ? "" : "Access is denied",
                ["SigningCertificate"] = _certificate?.DeepClone(),
                ["CabLimitMegabytes"] = 384,
                ["Candidates"] = new JsonArray(_serverUpdates.Where(u => u["Description"]!.GetValue<string>().Contains(request["IdentityTag"]!.GetValue<string>()))
                    .Select(u => (JsonNode)u.DeepClone()).ToArray())
            });
        }
        if (operation == "Publish")
        {
            _publishRequests.Add((JsonObject)request.DeepClone());
            var existing = request["ExistingPackageId"]?.GetValue<string>();
            var id = existing ?? request["Plan"]!["PackageId"]!.GetValue<string>();
            if (existing is null)
            {
                _plans.Add((JsonObject)request["Plan"]!.DeepClone());
                if (_publishError is not null) return new WsusAdapterResult(false, WsusErrorKinds.PublishRejected, _publishError, null);
            }
            var data = new JsonObject { ["PackageId"] = id, ["Published"] = existing is null, ["UpdateVisible"] = true };
            data["UpdateVisibilityError"] = _visibilityError;
            if (request["SkipFollowUp"]?.GetValue<bool>() == true) return FakeAdapterRunner.Ok(data);
            var group = request["ApprovalGroup"]?.GetValue<string>() ?? "";
            if (group.Length > 0)
            {
                _approvals.Add(group);
                data["Approval"] = new JsonObject { ["Group"] = group, ["Ok"] = _approvalError is null, ["Message"] = _approvalError ?? "" };
            }
            var declines = new JsonArray();
            // The adapter declines earlier versions only after the approval succeeded.
            var approvalFailed = group.Length > 0 && _approvalError is not null;
            data["DeclinesSkipped"] = approvalFailed && (request["DeclinePackageIds"] as JsonArray ?? []).Count > 0;
            if (approvalFailed) data["Declines"] = declines;
            foreach (var decline in approvalFailed ? [] : (request["DeclinePackageIds"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
            {
                var failed = _declineErrors.TryGetValue(decline, out var error);
                if (!failed) _declines.Add(decline);
                declines.Add(new JsonObject { ["PackageId"] = decline, ["Ok"] = !failed, ["Message"] = error ?? "" });
            }
            data["Declines"] = declines;
            return FakeAdapterRunner.Ok(data);
        }
        throw new InvalidOperationException("Unexpected operation " + operation);
    }

    private StageManifest Hashed(Action<StageManifest>? change = null) => WsusTestSupport.Hashed(_content, change);

    [Fact]
    public async Task PublishesANewUpdateBuiltFromTheManifest()
    {
        var result = await Service(out var runner).PublishAsync(Hashed(), _content, _settings);
        var plan = Assert.Single(_plans);
        var expectedId = Guid.Parse(plan["PackageId"]!.GetValue<string>());
        Assert.Equal(WsusPublishOutcome.Published, result.Outcome);
        Assert.Equal(expectedId, result.PackageId);
        Assert.NotEqual(Guid.Empty, expectedId);
        Assert.Equal("Contoso Tool 5.4 5.4.2", plan["Title"]!.GetValue<string>());
        Assert.Equal("AppPackager Applications", plan["ProductName"]!.GetValue<string>());
        Assert.Equal("AppPackager", plan["VendorName"]!.GetValue<string>());
        Assert.Equal("/S", plan["CommandLine"]!.GetValue<string>());
        Assert.Equal("setup.exe", plan["InstallerFile"]!.GetValue<string>());
        Assert.Equal("EXE", plan["InstallerType"]!.GetValue<string>());
        var description = plan["Description"]!.GetValue<string>();
        Assert.Contains(WsusIdentity.IdentityLine(Identity, "Update"), description);
        Assert.Contains("build 20260929-101500-abcdef01", description);
        Assert.Contains("; type Update; version 5.4.2", description);
        Assert.Equal("5.4.2", WsusIdentity.ParseIdentity(description)!.Version);
        Assert.Matches("^<bar:RegSzToVersion .*Comparison=\"GreaterThanOrEqualTo\"", plan["IsInstalled"]!.GetValue<string>());
        Assert.Matches("^<lar:And><bar:RegValueExists .*<bar:RegSzToVersion .*Comparison=\"LessThan\"", plan["IsInstallable"]!.GetValue<string>());
        Assert.Null(plan["PackageType"]);
        Assert.Equal(["custom.mst", "product.msi", "setup.exe"],
            (_publishRequests[0]["PayloadFiles"] as JsonArray)!.Select(n => n!.GetValue<string>()).Order());
        Assert.Equal([(0, "Succeeded", false), (1707, "Succeeded", false), (3010, "Succeeded", true), (1641, "Succeeded", true)],
            (plan["ReturnCodes"] as JsonArray)!.Select(n => (n!["Code"]!.GetValue<int>(), n["Result"]!.GetValue<string>(), n["Reboot"]!.GetValue<bool>())));
        Assert.Equal(["PublishPrepare", "Publish"], runner.Calls.Select(c => c.Operation));
        Assert.DoesNotContain("rules read", result.Message);
        Assert.Equal($"published {expectedId}; applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it", result.Message);
    }

    [Fact]
    public async Task TheReturnCodesOfThePlanCarryTheExitCodesOfTheDefinition()
    {
        var manifest = Hashed();
        manifest.RebootExitCodes = [2];
        manifest.SuccessExitCodes = [1223];

        await Service(out _).PublishAsync(manifest, _content, _settings);

        var plan = Assert.Single(_plans);
        Assert.Equal(
            [(0, "Succeeded", false), (1707, "Succeeded", false), (3010, "Succeeded", true), (1641, "Succeeded", true), (2, "Succeeded", true), (1223, "Succeeded", false)],
            (plan["ReturnCodes"] as JsonArray)!.Select(n => (n!["Code"]!.GetValue<int>(), n["Result"]!.GetValue<string>(), n["Reboot"]!.GetValue<bool>())));
    }

    [Fact]
    public async Task NamesTheAddRemoveProgramsEntryInTheResultWhenTheRulesComeFromTheStagedMsi()
    {
        var runner = new FakeAdapterRunner(Handle);
        var service = new WsusPublishService(runner, new WsusRuleBuilder(new FakeMsi("Contoso Tool 5.4.2", "5.4.2.0", "Contoso", "x64")), () => Now);
        var manifest = Hashed(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = Registry(ProductKey, "5.4.2"); });
        var result = await service.PublishAsync(manifest, _content, _settings);
        Assert.Equal(WsusPublishOutcome.Published, result.Outcome);
        Assert.Contains("; rules read the Add/Remove Programs entry that the MSI registers: 'Contoso Tool *' by Contoso, 64-bit view", result.Message);
        Assert.Equal(WsusRuleSource.MsiEntry, result.RulesSource);
    }

    [Fact]
    public async Task SupersedesOnlyLowerVersionsOfTheSameIdentityAndTypeAndDeclinesThemWhenAsked()
    {
        var older = Guid.Parse("00000000-0000-0000-0000-000000000541");
        var declinedOlder = Guid.Parse("00000000-0000-0000-0000-000000000540");
        _serverUpdates.AddRange(
        [
            Update(older, "Contoso Tool 5.4.1", "x\r\n" + Line("5.4.1")),
            Update(declinedOlder, "Contoso Tool 5.4.0", Line("5.4.0"), declined: true),
            Update(Guid.NewGuid(), "Contoso Tool 5.4.3", Line("5.4.3")),
            Update(Guid.NewGuid(), "Contoso Tool unversioned", Line("")),
            Update(Guid.NewGuid(), "Contoso Tool 5.3", Line("5.3"), declined: true, state: "Expired"),
            Update(Guid.NewGuid(), "Contoso Tool app", Line("5.4.1", "Application")),
            Update(Guid.NewGuid(), "Other", Line("1.0", identity: "AppPackager:catalog:package-other/default")),
            Update(Guid.NewGuid(), "Foreign", "Published elsewhere")
        ]);
        _settings.DeclineSuperseded = true;
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal([older.ToString(), declinedOlder.ToString()], (_plans[0]["SupersededPackageIds"] as JsonArray)!.Select(n => n!.GetValue<string>()));
        Assert.Equal(2, result.Superseded.Count);
        Assert.Equal([older.ToString()], _declines);
        Assert.Contains("declined 1 earlier version", result.Message);
        Assert.Contains("1 update(s) of the retired type Application stay on the server", result.Message);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ReportsAnExistingUpdateWithoutPublishingAgainAndStillAppliesTheApprovalAndTheDecline()
    {
        var current = Guid.Parse("00000000-0000-0000-0000-000000000542");
        var older = Guid.Parse("00000000-0000-0000-0000-000000000541");
        _serverUpdates.AddRange([Update(current, "Contoso Tool 5.4.2", Line("5.4.2")), Update(older, "Contoso Tool 5.4.1", Line("5.4.1"))]);
        _settings.ApprovalGroup = "Pilot";
        _settings.DeclineSuperseded = true;
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal(WsusPublishOutcome.AlreadyPublished, result.Outcome);
        Assert.Equal(current, result.PackageId);
        Assert.Equal("Pilot", result.Approved);
        Assert.Empty(_plans);
        Assert.Equal(["Pilot"], _approvals);
        Assert.Equal([older.ToString()], _declines);
        Assert.StartsWith($"already on the server {current}", result.Message);
    }

    /// <summary>Publishes once, and puts the update that the first publish made on the server, as the server would hold it.</summary>
    private async Task<Guid> PublishedOnTheServerAsync(StageManifest manifest)
    {
        await Service(out _).PublishAsync(manifest, _content, _settings);
        var plan = Assert.Single(_plans);
        var id = Guid.Parse(plan["PackageId"]!.GetValue<string>());
        _serverUpdates.Add(Update(id, plan["Title"]!.GetValue<string>(), plan["Description"]!.GetValue<string>()));
        _plans.Clear();
        _publishRequests.Clear();
        return id;
    }

    [Fact]
    public async Task ThePlanDescriptionEndsWithTheDigestLine()
    {
        await Service(out _).PublishAsync(Hashed(), _content, _settings);

        var description = Assert.Single(_plans)["Description"]!.GetValue<string>();
        Assert.Matches(@"\r\nAppPackager plan: [0-9a-f]{16}$", description);
        Assert.NotNull(WsusIdentity.ParseIdentity(description));
    }

    [Fact]
    public async Task ARepeatPublishOfTheSameStageFindsTheUpdateUnchanged()
    {
        var id = await PublishedOnTheServerAsync(Hashed());

        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);

        Assert.Equal(WsusPublishOutcome.AlreadyPublished, result.Outcome);
        Assert.Equal(id, result.PackageId);
        Assert.Empty(result.Warnings);
    }

    public static TheoryData<string> ChangedStages => new() { "install command", "reboot code", "success code", "detection" };

    [Theory]
    [MemberData(nameof(ChangedStages))]
    public async Task ARepeatPublishOfAChangedStageIsRefusedAndNothingReachesTheServer(string change)
    {
        await PublishedOnTheServerAsync(Hashed());
        var changed = Hashed();
        switch (change)
        {
            case "install command": changed.InstallArgs = "/S /norestart"; break;
            case "reboot code": changed.RebootExitCodes = [2]; break;
            case "success code": changed.SuccessExitCodes = [1223]; break;
            default: changed.Detection.ExpectedValue = "9.9.9"; break;
        }

        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(changed, _content, _settings));

        Assert.Equal(WsusErrorKinds.UpdateDiffers, ex.Kind);
        Assert.Contains("already on the server", ex.Message);
        Assert.Contains("Expire it", ex.Message);
        Assert.Empty(_publishRequests);
        Assert.Empty(_plans);
    }

    [Fact]
    public async Task AnUpdateFromAnEarlierReleaseHasNoDigestAndIsReportedWithAWarning()
    {
        _serverUpdates.Add(Update(Guid.Parse("00000000-0000-0000-0000-000000000542"), "Contoso Tool 5.4.2", Line("5.4.2")));

        var progress = new List<string>();

        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings, progress.Add);

        Assert.Equal(WsusPublishOutcome.AlreadyPublished, result.Outcome);
        // A warning would make a command-line build exit with 1 at each run; the note is a log line.
        Assert.Empty(result.Warnings);
        Assert.Contains(progress, line => line.Contains("earlier release") && line.Contains("not compared"));
    }

    private void WriteScript(string body, string signature) =>
        File.WriteAllText(Path.Combine(_content, "tool.ps1"), body + "\r\n# SIG # Begin signature block\r\n" + signature + "\r\n# SIG # End signature block\r\n");

    [Fact]
    public async Task AScriptThatIsSignedAgainWithTheSameBodyDoesNotMakeTheUpdateDiffer()
    {
        WriteScript("Write-Host 'one'", "SIGNATURE-A");
        await PublishedOnTheServerAsync(Hashed());
        WriteScript("Write-Host 'one'", "SIGNATURE-B");

        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);

        Assert.Equal(WsusPublishOutcome.AlreadyPublished, result.Outcome);
    }

    [Fact]
    public async Task AScriptWithACorrectedBodyMakesTheUpdateDiffer()
    {
        WriteScript("Write-Host 'one'", "SIGNATURE-A");
        await PublishedOnTheServerAsync(Hashed());
        WriteScript("Write-Host 'corrected'", "SIGNATURE-A");

        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(Hashed(), _content, _settings));

        Assert.Equal(WsusErrorKinds.UpdateDiffers, ex.Kind);
    }

    [Fact]
    public async Task AChangedPayloadFileFailsTheRepeatPublish()
    {
        await PublishedOnTheServerAsync(Hashed());
        // A corrected installer: other bytes, and a manifest that records them.
        File.WriteAllText(Path.Combine(_content, "setup.exe"), "corrected installer bytes");
        var changed = Hashed();

        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(changed, _content, _settings));

        Assert.Equal(WsusErrorKinds.UpdateDiffers, ex.Kind);
    }

    [Fact]
    public async Task ADeclinedExistingUpdateThatDiffersFailsAndSaysItIsDeclined()
    {
        var id = await PublishedOnTheServerAsync(Hashed());
        var onServer = _serverUpdates.Single(u => u["PackageId"]!.GetValue<string>() == id.ToString());
        onServer["Declined"] = true;
        var changed = Hashed();
        changed.InstallArgs = "/S /other";

        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(changed, _content, _settings));

        Assert.Equal(WsusErrorKinds.UpdateDiffers, ex.Kind);
        Assert.Contains(", declined)", ex.Message);
    }

    [Fact]
    public async Task TheUpdateDiffersFailureIsAFailedStepAndNotARefusal()
    {
        await PublishedOnTheServerAsync(Hashed());
        var changed = Hashed();
        changed.InstallArgs = "/S /other";

        var step = await WsusPublishStep.RunAsync(Service(out _), changed, _content, _settings);

        // A refusal would be remembered by One Click as "not supported" until the version changes.
        Assert.Equal(WsusStepStatus.Failed, step.Status);
        Assert.Contains("Expire it in WSUS Updates", step.Message);
        Assert.Empty(step.RefusalCodes);
        Assert.False(step.Refused);
    }

    [Fact]
    public async Task LeavesAnExistingUpdateThatTheServerDeclinedAloneWithAWarning()
    {
        _serverUpdates.AddRange(
        [
            Update(Guid.Parse("00000000-0000-0000-0000-000000000542"), "Contoso Tool 5.4.2", Line("5.4.2"), declined: true),
            Update(Guid.Parse("00000000-0000-0000-0000-000000000541"), "Contoso Tool 5.4.1", Line("5.4.1"))
        ]);
        _settings.ApprovalGroup = "Pilot";
        _settings.DeclineSuperseded = true;
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal(WsusPublishOutcome.AlreadyPublished, result.Outcome);
        Assert.Empty(_approvals);
        Assert.Empty(_declines);
        Assert.Contains("declined on the server", result.Warnings[0]);
    }

    [Fact]
    public async Task PublishesTheSameVersionUnderANewIdWhenItsEarlierUpdateIsExpired()
    {
        var expired = Guid.Parse("00000000-0000-0000-0000-000000000542");
        _serverUpdates.Add(Update(expired, "Contoso Tool 5.4.2", Line("5.4.2"), declined: true, state: "Expired"));
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal(WsusPublishOutcome.Published, result.Outcome);
        Assert.NotEqual(expired, result.PackageId);
        Assert.Empty((_plans[0]["SupersededPackageIds"] as JsonArray)!);
    }

    [Fact]
    public async Task GivesEveryNewPublishItsOwnId()
    {
        var service = Service(out _);
        await service.PublishAsync(Hashed(), _content, _settings);
        await service.PublishAsync(Hashed(), _content, _settings);
        Assert.Equal(2, _plans.Count);
        Assert.NotEqual(_plans[0]["PackageId"]!.GetValue<string>(), _plans[1]["PackageId"]!.GetValue<string>());
    }

    [Fact]
    public async Task KeepsTheEarlierVersionsAndASuccessfulPublishWhenTheApprovalFails()
    {
        var older = Guid.Parse("00000000-0000-0000-0000-000000000541");
        _serverUpdates.Add(Update(older, "Contoso Tool 5.4.1", Line("5.4.1")));
        _approvalError = "WSUS has no computer group named 'Pilto'.";
        _settings.ApprovalGroup = "Pilto";
        _settings.DeclineSuperseded = true;
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal(WsusPublishOutcome.Published, result.Outcome);
        Assert.Equal("", result.Approved);
        Assert.Contains("approval for Pilto failed: WSUS has no computer group named 'Pilto'", result.Warnings[0]);
        Assert.Equal("earlier versions were not declined because the approval failed", result.Warnings[1]);
        Assert.Empty(_declines);
        Assert.Empty(result.Declined);
        Assert.Contains("approval for Pilto failed", result.Message);
        Assert.Equal(WsusStepStatus.Published, result.ToStepResult().Status);
    }

    [Fact]
    public async Task VisibilityFailureIsAWarningInTheSuccessfulPublishResult()
    {
        _visibilityError = "Update lookup timed out";

        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);

        Assert.Equal(WsusPublishOutcome.Published, result.Outcome);
        Assert.Contains("Update lookup timed out", Assert.Single(result.Warnings));
        Assert.Contains("visibility could not be checked", result.Message);
        Assert.Equal(WsusStepStatus.Published, result.ToStepResult().Status);
    }

    [Fact]
    public async Task NamesTheUpdateWhoseDeclineFailed()
    {
        var older = Guid.Parse("00000000-0000-0000-0000-000000000541");
        _serverUpdates.Add(Update(older, "Contoso Tool 5.4.1", Line("5.4.1")));
        _declineErrors[older.ToString()] = "The update is not editable.";
        _settings.DeclineSuperseded = true;
        var result = await Service(out _).PublishAsync(Hashed(), _content, _settings);
        Assert.Equal("declining Contoso Tool 5.4.1 failed: The update is not editable.", Assert.Single(result.Warnings));
        Assert.Empty(result.Declined);
    }

    [Fact]
    public async Task ApprovesANewUpdateForTheConfiguredGroup()
    {
        _settings.ApprovalGroup = "Pilot";
        Assert.Equal("Pilot", (await Service(out _).PublishAsync(Hashed(), _content, _settings)).Approved);
        Assert.Equal(["Pilot"], _approvals);
    }

    [Fact]
    public async Task RefusesABlockingManifestBeforeContactingTheServer()
    {
        var ex = await Assert.ThrowsAsync<WsusRefusedException>(() =>
            Service(out var runner).PublishAsync(Hashed(m => m.InstallerType = "MSIX"), _content, _settings));
        Assert.StartsWith("This application cannot be published to WSUS:", ex.Message);
        Assert.Contains("InstallerTypeUnsupported", ex.Message);
        Assert.Empty(_publishRequests);
    }

    [Fact]
    public async Task MarksARefusalSoACallerCanTellItApartFromAFailedServerCall()
    {
        var service = Service(out var runner);
        var refusal = await Assert.ThrowsAsync<WsusRefusedException>(() =>
            service.PublishAsync(Hashed(m => { m.InstallerType = "MSIX"; m.SetupFile = "custom.cmd"; }), _content, _settings));
        Assert.Equal([WsusRefusalCodes.InstallerTypeUnsupported, WsusRefusalCodes.CustomInstall], refusal.Codes);
        Assert.Empty(runner.Calls);

        _prepareErrorKind = WsusErrorKinds.Failed;
        _prepareError = "The remote server returned an error: (503) Server Unavailable.";
        var fault = await Assert.ThrowsAsync<WsusOperationException>(() => service.PublishAsync(Hashed(), _content, _settings));
        Assert.Equal(_prepareError, fault.Message);
    }

    [Fact]
    public async Task RefusesAManifestThatRecordsAFileOutsideTheContentFolderBeforeContactingTheServer()
    {
        var outside = Path.Combine(_temp.Path, "outside-publish.txt");
        File.WriteAllText(outside, "marker");
        var manifest = Hashed();
        manifest.FileHashes = [.. manifest.FileHashes!, new StageFileHash { RelativePath = @"..\outside-publish.txt", Sha256 = Sha256(outside), Size = 6 }];
        var ex = await Assert.ThrowsAsync<WsusRefusedException>(() => Service(out var runner).PublishAsync(manifest, _content, _settings));
        Assert.Contains(WsusRefusalCodes.PayloadOutsideStage, ex.Codes);
        Assert.Empty(_publishRequests);
    }

    [Fact]
    public async Task RefusesAnInstallerWhoseBytesChangedAfterStaging()
    {
        var manifest = Hashed();
        manifest.FileHashes!.Single(h => h.RelativePath == "setup.exe").Sha256 = new string('0', 64);
        var service = Service(out var runner);
        Assert.Contains("changed after it was staged", (await Assert.ThrowsAsync<WsusPayloadException>(() => service.PublishAsync(manifest, _content, _settings))).Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RefusesAManifestWithoutFileHashes()
    {
        var manifest = Hashed();
        manifest.FileHashes = null;
        var service = Service(out var runner);
        Assert.Contains("records no file hashes", (await Assert.ThrowsAsync<WsusPayloadException>(() => service.PublishAsync(manifest, _content, _settings))).Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RefusesAServerWithoutASigningCertificateAndOneWhoseCertificateExpired()
    {
        _certificate = null;
        Assert.Contains("has no signing certificate", (await Assert.ThrowsAsync<WsusOperationException>(() =>
            Service(out _).PublishAsync(Hashed(), _content, _settings))).Message);
        _certificate = Certificate(Now.AddYears(-2), Now.AddDays(-1));
        Assert.Contains("expired", (await Assert.ThrowsAsync<WsusOperationException>(() =>
            Service(out _).PublishAsync(Hashed(), _content, _settings))).Message);
        Assert.Empty(_publishRequests);
    }

    [Fact]
    public async Task PassesMsiPropertiesOnlyAndCarriesTheTransformWithTheOtherRecordedFiles()
    {
        File.WriteAllText(Path.Combine(_content, "product.msi"), "msi");
        var manifest = Hashed(m =>
        {
            m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.InstallArgs = "/qn /norestart TRANSFORMS=custom.mst";
            m.Detection = Registry(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", "24.08.00.0");
        });
        await Service(out _).PublishAsync(manifest, _content, _settings);
        Assert.Equal("TRANSFORMS=custom.mst REBOOT=ReallySuppress", _plans[0]["CommandLine"]!.GetValue<string>());
        Assert.Equal("product.msi", _plans[0]["InstallerFile"]!.GetValue<string>());
        Assert.Equal(["custom.mst", "product.msi", "setup.exe"], (_publishRequests[0]["PayloadFiles"] as JsonArray)!.Select(n => n!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task SendsTheStagedSha256OfEveryPayloadFileInTheOrderOfThePayloadFiles()
    {
        await Service(out _).PublishAsync(Hashed(), _content, _settings);

        var files = (_publishRequests[0]["PayloadFiles"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToList();
        var hashes = (_publishRequests[0]["PayloadSha256"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(files.Select(f => Sha256(Path.Combine(_content, f))), hashes);
    }

    [Fact]
    public async Task RefusesAnAccountThatIsNotAWsusAdministratorBeforeBuildingAnything()
    {
        _prepareErrorKind = WsusErrorKinds.NotAdministrator;
        _prepareError = @"The account CONTOSO\op has the role Reporter on WSUS wsus01. Add it to the WSUS Administrators group on the server.";
        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(Hashed(), _content, _settings));
        Assert.Matches("role Reporter.*WSUS Administrators", ex.Message);
        Assert.Empty(_plans);
    }

    [Fact]
    public async Task PublishesWhenTheCertificateCannotBeReadLeavingTheRefusalToTheServer()
    {
        _certificateReadable = false;
        _certificate = null;
        var lines = new List<string>();
        Assert.Equal(WsusPublishOutcome.Published, (await Service(out _).PublishAsync(Hashed(), _content, _settings, lines.Add)).Outcome);
        Assert.Contains(lines, l => l.Contains("not readable (Access is denied); publishing continues"));
    }

    [Fact]
    public async Task FilesEveryUpdateUnderOneVendorAndOneProductCategory()
    {
        await Service(out _).PublishAsync(Hashed(m => m.Publisher = "Microsoft Corporation"), _content, _settings);
        Assert.Equal("AppPackager", _plans[0]["VendorName"]!.GetValue<string>());
        Assert.Equal("AppPackager Applications", _plans[0]["ProductName"]!.GetValue<string>());
        Assert.Contains("from Microsoft Corporation, published by AppPackager", _plans[0]["Description"]!.GetValue<string>());
    }

    [Fact]
    public async Task KeepsALongTitleUnder80CharactersWithTheVersionAtTheEnd()
    {
        await Service(out _).PublishAsync(Hashed(m => m.AppName = "Contoso Enterprise Remote Management and Monitoring Agent for Windows Workstations (x64)"), _content, _settings);
        var title = _plans[0]["Title"]!.GetValue<string>();
        Assert.True(title.Length < 80);
        Assert.EndsWith("... 5.4.2", title);
    }

    [Fact]
    public async Task NamesTheVersionOnceInTheDescriptionWhenTheTitleAlreadyCarriesIt()
    {
        await Service(out _).PublishAsync(Hashed(m => m.AppName = "Contoso Tool 5.4.2"), _content, _settings);
        Assert.StartsWith("Contoso Tool 5.4.2 from Contoso, published by AppPackager", _plans[0]["Description"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddsTheFixToAKnownServerError()
    {
        _publishError = @"Exception occurred during publishing: Verification of file signature failed for file: \\wsus01\UpdateServicesPackages\x_1.cab";
        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(Hashed(), _content, _settings));
        Assert.Matches("Verification of file signature failed.*must trust the WSUS signing certificate", ex.Message);
    }

    [Fact]
    public async Task NamesTheServerErrorWhenWsusRejectsThePackage()
    {
        _publishError = "The signing certificate is not trusted";
        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => Service(out _).PublishAsync(Hashed(), _content, _settings));
        Assert.StartsWith("WSUS rejected the package: The signing certificate is not trusted", ex.Message);
    }

    [Fact]
    public async Task UsesAnAbsoluteSupportUrlOnly()
    {
        await Service(out _).PublishAsync(Hashed(), _content, _settings, supportUrl: "https://contoso.example/tool");
        await Service(out _).PublishAsync(Hashed(), _content, _settings, supportUrl: "contoso.example/tool");
        Assert.Equal("https://contoso.example/tool", _plans[0]["SupportUrl"]!.GetValue<string>());
        Assert.Equal("", _plans[1]["SupportUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusesWithoutAServer()
    {
        _settings.ServerName = "";
        Assert.Equal("No WSUS server is configured.", (await Assert.ThrowsAsync<WsusOperationException>(() =>
            Service(out _).PublishAsync(Hashed(), _content, _settings))).Message);
    }

    [Fact]
    public void ThePayloadHintNamesTheCabLimitWhenTheServerTextIsUnknown()
    {
        Assert.Equal("WSUS rejected the package: boom The payload is 500 MB and the server allows 384 MB per cab: raise LocalPublishingMaxCabSize on the server (at most 2047).",
            WsusMessages.PublishRejected("boom", 500, 384));
        Assert.Equal("WSUS rejected the package: boom", WsusMessages.PublishRejected("boom", 100, 384));
    }
}
