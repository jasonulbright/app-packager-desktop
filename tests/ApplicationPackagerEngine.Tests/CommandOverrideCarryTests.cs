using System.Net;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Saved operator commands reached MECM through a call argument and never touched the
/// manifest, and the Intune publish reads its command lines from the manifest. An
/// operator who set a custom install command therefore got it on one target and
/// install.bat on the other. Resolution now lands on the manifest before either target
/// runs, so these tests read the request body the publish actually sends.
/// </summary>
public sealed class CommandOverrideCarryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "packr-cmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string NoApps = """{"value":[]}""";

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "secret",
        PollInterval = TimeSpan.Zero,
        CommitPollInterval = TimeSpan.Zero,
        PollTimeoutSec = 30
    };

    private static StageManifest Manifest(StageCommandOverrides? manifestCommands = null) => new()
    {
        AppName = "Contoso App",
        Publisher = "Contoso Ltd",
        SoftwareVersion = "1.2.3.4",
        CommandOverrides = manifestCommands,
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "1.2.3.4",
            Operator = "GreaterEquals"
        }
    };

    private static FakeGraphHandler BuildHandler() =>
        new((request, _) =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Contains("/oauth2/v2.0/token"))
                return FakeGraphHandler.Json("""{"access_token":"fake-token","expires_in":3599}""");

            if (uri.Contains("blob.core.windows.net"))
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };

            if (uri.Contains("mobileApps?$filter=") || uri.Contains("mobileApps?%24filter="))
                return FakeGraphHandler.Json(NoApps);

            if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Json("""{"id":"7"}""");

            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRequestPending"}""");

            if (uri.EndsWith("/commit") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Empty();

            if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
                return FakeGraphHandler.Json(
                    """{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");

            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
                return FakeGraphHandler.Json("""{"id":"app-100"}""");

            return FakeGraphHandler.Json("{}");
        });

    /// <summary>
    /// Runs the production resolution the packaging path runs, then publishes the same
    /// manifest object and returns the created app body.
    /// </summary>
    private async Task<JsonObject> ResolveThenPublish(
        StageCommandOverrides? saved, StageCommandOverrides? manifestCommands = null)
    {
        var manifest = Manifest(manifestCommands);
        DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, saved);

        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler();
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, manifest, Options());

        var create = handler.Requests.First(r =>
            r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        return (JsonObject)JsonNode.Parse(create.Body)!;
    }

    // ── The published command lines ─────────────────────────────

    [Fact]
    public async Task SavedInstallAndUninstallOverridesBothReachTheIntuneRequest()
    {
        var body = await ResolveThenPublish(new StageCommandOverrides
        {
            Install = "powershell.exe -File .\\Deploy-Application.ps1 -Install",
            Uninstall = "powershell.exe -File .\\Deploy-Application.ps1 -Uninstall"
        });

        Assert.Equal("powershell.exe -File .\\Deploy-Application.ps1 -Install",
            (string?)body["installCommandLine"]);
        Assert.Equal("powershell.exe -File .\\Deploy-Application.ps1 -Uninstall",
            (string?)body["uninstallCommandLine"]);
    }

    /// <summary>The two commands are independently optional; the unset side keeps its default.</summary>
    [Fact]
    public async Task ASavedInstallOverrideAloneLeavesTheUninstallDefault()
    {
        var body = await ResolveThenPublish(new StageCommandOverrides { Install = "setup.exe /quiet" });

        Assert.Equal("setup.exe /quiet", (string?)body["installCommandLine"]);
        Assert.Equal("uninstall.bat", (string?)body["uninstallCommandLine"]);
    }

    [Fact]
    public async Task ASavedUninstallOverrideAloneLeavesTheInstallDefault()
    {
        var body = await ResolveThenPublish(new StageCommandOverrides { Uninstall = "setup.exe /x /quiet" });

        Assert.Equal("install.bat", (string?)body["installCommandLine"]);
        Assert.Equal("setup.exe /x /quiet", (string?)body["uninstallCommandLine"]);
    }

    [Fact]
    public async Task WithNoSavedOverrideThePublishKeepsTheWrapperDefaults()
    {
        var body = await ResolveThenPublish(saved: null);

        Assert.Equal("install.bat", (string?)body["installCommandLine"]);
        Assert.Equal("uninstall.bat", (string?)body["uninstallCommandLine"]);
    }

    /// <summary>An ad-hoc stage supplies its commands on the manifest and keeps them.</summary>
    [Fact]
    public async Task AManifestCarryingItsOwnCommandsKeepsThemWhenNothingIsSaved()
    {
        var body = await ResolveThenPublish(
            saved: null,
            manifestCommands: new StageCommandOverrides
            {
                Install = "msiexec /i Contoso.msi /qn",
                Uninstall = "msiexec /x {GUID} /qn"
            });

        Assert.Equal("msiexec /i Contoso.msi /qn", (string?)body["installCommandLine"]);
        Assert.Equal("msiexec /x {GUID} /qn", (string?)body["uninstallCommandLine"]);
    }

    /// <summary>A saved override replaces the manifest's own, the precedence MECM applies.</summary>
    [Fact]
    public async Task ASavedOverrideWinsOverTheManifestCommands()
    {
        var body = await ResolveThenPublish(
            saved: new StageCommandOverrides { Install = "saved-install.cmd" },
            manifestCommands: new StageCommandOverrides
            {
                Install = "manifest-install.cmd",
                Uninstall = "manifest-uninstall.cmd"
            });

        Assert.Equal("saved-install.cmd", (string?)body["installCommandLine"]);
        // The saved record replaces the manifest's outright rather than merging field by
        // field, which is what the MECM argument already did.
        Assert.Equal("uninstall.bat", (string?)body["uninstallCommandLine"]);
    }

    // ── Resolution in isolation ─────────────────────────────────

    [Fact]
    public void ResolutionRecordsTheSavedOverrideOnTheManifest()
    {
        var manifest = Manifest();
        var saved = new StageCommandOverrides { Install = "a.cmd", Uninstall = "b.cmd" };

        var effective = DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, saved);

        Assert.Same(saved, manifest.CommandOverrides);
        Assert.Same(saved, effective);
    }

    [Fact]
    public void ResolutionLeavesTheManifestCommandsInPlaceWhenNothingIsSaved()
    {
        var own = new StageCommandOverrides { Install = "own.cmd" };
        var manifest = Manifest(own);

        var effective = DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, null);

        Assert.Same(own, manifest.CommandOverrides);
        Assert.Same(own, effective);
    }

    [Fact]
    public void ResolutionWithNothingOnEitherSideLeavesTheManifestUnset()
    {
        var manifest = Manifest();

        Assert.Null(DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, null));
        Assert.Null(manifest.CommandOverrides);
    }

    // ── Variant manifests ───────────────────────────────────────

    /// <summary>
    /// A variant-split manifest carries per-deployment-type commands, so the flat
    /// override is still refused for MECM whether it arrives as an argument or on the
    /// manifest. Recording it changes nothing.
    /// </summary>
    [Fact]
    public void AVariantManifestStillRefusesTheFlatOverride()
    {
        var manifest = Manifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "(x64)" },
            new StageDeploymentType { NameSuffix = "(x86)" }
        ];
        var saved = new StageCommandOverrides { Install = "saved-install.cmd" };

        DeploymentTypeSpecResolver.ResolveCommandOverrides(manifest, saved);

        var specs = new List<DeploymentTypeSpec>
        {
            new() { DtName = "Contoso App (x64)", InstallCommand = "install.bat" },
            new() { DtName = "Contoso App (x86)", InstallCommand = "install.bat" }
        };
        var logged = new List<string>();

        DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, manifest.CommandOverrides, "Contoso App", logged.Add);

        Assert.All(specs, s => Assert.Equal("install.bat", s.InstallCommand));
        Assert.Contains(logged, l => l.Contains("Command overrides ignored"));
    }
}
