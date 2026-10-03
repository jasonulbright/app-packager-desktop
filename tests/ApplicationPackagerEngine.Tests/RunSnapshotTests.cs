using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class RunSnapshotTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private RunSnapshot Snapshot(string applicationId, string profileId, string? overrides = null, string target = RunTargets.Mecm) =>
        _t.Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = applicationId,
            ProfileId = profileId,
            Target = target,
            RunOverrides = overrides is null ? null : WorkbenchTestRoot.Json(overrides)
        });

    [Fact]
    public void Create_WritesASnapshotCarryingTheProfileAndRunOverrides()
    {
        var profile = _t.NewProfile("catalog:package-snapshot");
        var snapshot = Snapshot(profile.ApplicationId, profile.ProfileId, """{ "EstimatedMinutes": 12 }""");

        Assert.True(File.Exists(snapshot.Path));
        Assert.Equal(Path.Combine(_t.Store.RunsRoot, snapshot.BuildId + ".json"), snapshot.Path);
        Assert.Matches(@"^\d{8}-\d{6}-[0-9a-f]{8}$", snapshot.BuildId);
        Assert.Equal(profile.Revision, snapshot.ProfileRevision);
        Assert.Equal(12, snapshot.RunOverrides["EstimatedMinutes"]!.GetValue<int>());

        var reloaded = WorkbenchStore.ReadRunSnapshot(snapshot.Path)!;
        Assert.Equal(snapshot.BuildId, reloaded.BuildId);
        Assert.Equal(profile.Revision, reloaded.ProfileRevision);
        Assert.Equal(12, reloaded.RunOverrides["EstimatedMinutes"]!.GetValue<int>());
        Assert.Equal(profile.ProfileId, WorkbenchJson.AsText(reloaded.Profile["ProfileId"]));
        Assert.Equal(_t.Store.DataRoot, reloaded.DataRoot);
    }

    [Fact]
    public void Create_WritesTheFieldNamesAPackagerChildReads()
    {
        var snapshot = Snapshot("catalog:package-names", "default");
        var stored = WorkbenchJson.Read(snapshot.Path!)!.AsObject();
        Assert.Equal(
            ["SchemaVersion", "BuildId", "CreatedAt", "ApplicationId", "ProfileId", "ProfileRevision", "Profile", "AssetPaths",
             "RunOverrides", "SigningPolicy", "Target", "DataRoot", "DownloadRoot", "PackagerScriptPath"],
            stored.Select(p => p.Key));
        Assert.Equal("Packager default", stored["Profile"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Create_NeverWritesARunOverrideBackIntoTheProfile()
    {
        var profile = _t.NewProfile("catalog:package-nowriteback");
        Snapshot(profile.ApplicationId, profile.ProfileId, """{ "EstimatedMinutes": 42 }""");

        var reloaded = _t.Store.GetProfile(profile.ApplicationId, profile.ProfileId);
        Assert.Equal(profile.Revision, reloaded.Revision);
        Assert.False(reloaded.Get("Timing.EstimatedMinutes").Found);
    }

    [Fact]
    public void Create_RejectsARunOverrideEstimateAboveTheMaximum()
    {
        var profile = _t.NewProfile("catalog:package-runrange");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Snapshot(profile.ApplicationId, profile.ProfileId, """{ "EstimatedMinutes": 60, "MaximumMinutes": 30 }"""));
        Assert.Equal("Run override EstimatedMinutes (60) cannot exceed MaximumMinutes (30).", ex.Message);
        Assert.False(Directory.Exists(_t.Store.RunsRoot));
    }

    [Theory]
    [InlineData("""{ "EstimatedMinutes": 0 }""", "Run override EstimatedMinutes must be a positive whole number of minutes (got '0').")]
    [InlineData("""{ "MaximumMinutes": "x" }""", "Run override MaximumMinutes must be a positive whole number of minutes (got 'x').")]
    public void Create_RejectsARunOverrideThatIsNotAPositiveWholeNumber(string overrides, string message)
    {
        var profile = _t.NewProfile("catalog:package-runwhole");
        var ex = Assert.Throws<InvalidOperationException>(() => Snapshot(profile.ApplicationId, profile.ProfileId, overrides));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Create_AcceptsAnExplicitNullRunOverride()
    {
        var snapshot = Snapshot("catalog:package-runnull", "default", """{ "EstimatedMinutes": null, "InstallCommand": null }""");
        Assert.True(snapshot.RunOverrides.ContainsKey("InstallCommand"));
        Assert.True(WorkbenchStore.ReadRunSnapshot(snapshot.Path)!.RunOverrides.ContainsKey("InstallCommand"));
    }

    [Fact]
    public void Create_ResolvesManagedAndLinkedAssetPathsAtSnapshotTime()
    {
        var profile = _t.NewProfile("catalog:package-snapassets");
        var folder = _t.NewFolder();
        var icon = _t.Store.AddAsset(profile.ApplicationId, profile.ProfileId, _t.NewFile(Path.Combine(folder, "icon.ico"), "ico"));
        var linkedPath = _t.NewFile(Path.Combine(folder, "linked.ini"), "[x]");
        profile.Set("Application.Icon", new JsonObject { ["Asset"] = icon.AssetId });
        profile.Data["SourceFiles"] = new JsonArray(new JsonObject
        {
            ["Linked"] = true, ["Provenance"] = linkedPath, ["Destination"] = "linked.ini"
        });
        profile = _t.Store.SaveProfile(profile);

        var snapshot = Snapshot(profile.ApplicationId, profile.ProfileId);

        Assert.Equal(icon.Path, snapshot.AssetPaths[icon.AssetId]);
        Assert.Equal(linkedPath, snapshot.AssetPaths["linked:" + linkedPath]);
    }

    [Fact]
    public void Create_FailsWithNoPartialFileWhenALinkedAssetIsGone()
    {
        var profile = _t.NewProfile("catalog:package-snapgone");
        var gone = Path.Combine(_t.NewFolder(), "gone.ini");
        profile.Data["SourceFiles"] = new JsonArray(new JsonObject { ["Linked"] = true, ["Provenance"] = gone, ["Destination"] = "gone.ini" });
        profile = _t.Store.SaveProfile(profile);

        Assert.Throws<FileNotFoundException>(() => Snapshot(profile.ApplicationId, profile.ProfileId));
        Assert.False(Directory.Exists(_t.Store.RunsRoot));
    }

    [Theory]
    [InlineData("WSUSOnly", "WSUSOnly")]
    [InlineData("mecmandwsus", "MECMAndWSUS")]
    [InlineData("ContentOnly", "ContentOnly")]
    public void Create_AcceptsEveryTargetIncludingWsus(string target, string stored)
    {
        Assert.Equal(stored, Snapshot("catalog:package-targets", "default", target: target).Target);
    }

    [Fact]
    public void Create_RejectsAnUnknownTarget()
    {
        Assert.Throws<ArgumentException>(() => Snapshot("catalog:package-badtarget", "default", target: "Everywhere"));
    }

    [Fact]
    public void Read_RejectsASnapshotSchemaNewerThanThisBuild()
    {
        var path = Path.Combine(_t.NewFolder(), "future.json");
        File.WriteAllText(path, """{"SchemaVersion":99}""");
        var ex = Assert.Throws<InvalidOperationException>(() => WorkbenchStore.ReadRunSnapshot(path));
        Assert.Equal($"Run snapshot schema 99 is newer than this build supports (1): {path}", ex.Message);
    }

    [Fact]
    public void Read_ThrowsForAMissingOrEmptySnapshotFile()
    {
        var folder = _t.NewFolder();
        var missing = Path.Combine(folder, "missing.json");
        Assert.Equal($"Run snapshot not found: {missing}", Assert.Throws<FileNotFoundException>(() => WorkbenchStore.ReadRunSnapshot(missing)).Message);
        var empty = _t.NewFile(Path.Combine(folder, "empty.json"), " ");
        Assert.Equal($"Run snapshot is empty: {empty}", Assert.Throws<InvalidOperationException>(() => WorkbenchStore.ReadRunSnapshot(empty)).Message);
    }
}

[Collection(WorkbenchEnvironmentCollection.Name)]
public sealed class RunSnapshotEnvironmentTests
{
    [Fact]
    public void Read_ReturnsNullWhenNoSnapshotIsConfigured()
    {
        var saved = Environment.GetEnvironmentVariable(WorkbenchStore.RunSnapshotVariable);
        try
        {
            Environment.SetEnvironmentVariable(WorkbenchStore.RunSnapshotVariable, null);
            Assert.Null(WorkbenchStore.ReadRunSnapshot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkbenchStore.RunSnapshotVariable, saved);
        }
    }
}

public sealed class SigningGateTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private sealed class CountingGate(Exception? failure = null) : ISigningCertificateGate
    {
        public int Calls { get; private set; }

        public object? ResolveCertificate(JsonNode policy)
        {
            Calls++;
            if (failure is not null) throw failure;
            return new { Thumbprint = "AA" };
        }
    }

    private RunSnapshot Snapshot(string id, string policy, ISigningCertificateGate? gate)
    {
        var profile = _t.NewProfile(id);
        return _t.Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = id,
            ProfileId = profile.ProfileId,
            SigningPolicy = WorkbenchTestRoot.Json(policy)
        }, gate);
    }

    private int RunFiles() => Directory.Exists(_t.Store.RunsRoot) ? Directory.GetFiles(_t.Store.RunsRoot).Length : 0;

    [Fact]
    public void Gate_ResolvesTheCertificateOnceWhileCreatingTheSnapshot()
    {
        var gate = new CountingGate();
        var snapshot = Snapshot("catalog:package-signgate-ok", """{ "SignDeployment": true }""", gate);
        Assert.Equal(1, gate.Calls);
        Assert.True(WorkbenchJson.IsTruthy(snapshot.SigningPolicy!["SignDeployment"]));
    }

    [Fact]
    public void Gate_FailureLeavesNoSnapshotFile()
    {
        var gate = new CountingGate(new InvalidOperationException("SigningCertificateUnavailable: no certificate thumbprint is selected."));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Snapshot("catalog:package-signgate-fail", """{ "RequireDeployment": true }""", gate));
        Assert.Contains("SigningCertificateUnavailable", ex.Message);
        Assert.Equal(0, RunFiles());
    }

    [Fact]
    public void Gate_RefusesARequireSwitchWhenNoSigningServiceIsPresent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Snapshot("catalog:package-signgate-absent", """{ "RequireDetection": true }""", gate: null));
        Assert.Equal(NoSigningServiceGate.NotLoadedMessage, ex.Message);
        Assert.Equal(0, RunFiles());
    }

    [Fact]
    public void Gate_LeavesAnAllOffPolicyAlone()
    {
        var gate = new CountingGate();
        var snapshot = Snapshot("catalog:package-signgate-off", """{ "SignDeployment": false, "CertificateThumbprint": "" }""", gate);
        Assert.Equal(0, gate.Calls);
        Assert.True(File.Exists(snapshot.Path));
    }
}

public sealed class WorkbenchDraftTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Draft_SavesReadsAndRemoves()
    {
        var id = "catalog:package-draft";
        _t.Store.SaveDraft(id, "default", new JsonObject { ["Name"] = "work in progress" });

        Assert.Equal("work in progress", _t.Store.GetDraft(id, "default")!["Name"]!.GetValue<string>());
        Assert.True(_t.Store.RemoveDraft(id, "default"));
        Assert.Null(_t.Store.GetDraft(id, "default"));
        Assert.False(_t.Store.RemoveDraft(id, "default"));
    }

    [Fact]
    public void Draft_IsSeparateFromTheSavedProfile()
    {
        var profile = _t.NewProfile("catalog:package-draft-profile");
        var draft = profile.Clone();
        draft.Set("Install.Command", "unsaved.bat");
        _t.Store.SaveDraft(profile.ApplicationId, profile.ProfileId, draft.Data);

        Assert.False(_t.Store.GetProfile(profile.ApplicationId, profile.ProfileId).Get("Install.Command").Found);
        var stored = _t.Store.GetDraft(profile.ApplicationId, profile.ProfileId)!;
        Assert.Equal("unsaved.bat", WorkbenchJson.Get(stored, "Install.Command").Text);
        Assert.Equal(profile.ProfileId, WorkbenchJson.AsText(stored["ProfileId"]));
    }
}
