using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Citrix Workspace argument builder emits a switch only for a set value,
/// validates each value against the vendor's documented set, and keeps the
/// Current Release and LTSR switch sets apart.
/// </summary>
public sealed class CitrixInstallArgumentsTests
{
    private static IReadOnlyList<string> Args(CitrixWorkspaceSwitches config, string stream = "Current") =>
        CitrixInstallArguments.Build(config, stream).Arguments;

    [Fact]
    public void AMissingConfigEmitsSilentOnly()
    {
        var r = CitrixInstallArguments.Build(null, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void NoRebootIsNeverEmitted()
    {
        Assert.DoesNotContain("/noreboot", Args(new() { Installation = new() { CleanInstall = true } }));
    }

    [Fact]
    public void EmptySectionsEmitNothingBeyondSilent()
    {
        var config = new CitrixWorkspaceSwitches
        {
            Installation = new(), Plugins = new(), UpdateAndTelemetry = new(), StorePolicy = new(), Store = new(), Components = new()
        };
        Assert.Equal(["/silent"], Args(config));
    }

    [Theory]
    [InlineData("AllowAddStore", "")]
    [InlineData("AllowAddStore", "   ")]
    [InlineData("AllowAddStore", null)]
    [InlineData("AllowSavePwd", "")]
    [InlineData("AllowSavePwd", null)]
    public void AStorePolicyThatIsNotSetIsOmitted(string key, string? value)
    {
        var r = CitrixInstallArguments.Build(new() { StorePolicy = Policy(key, value) }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("AllowAddStore", "ALLOWADDSTORE", "S", "S")]
    [InlineData("AllowAddStore", "ALLOWADDSTORE", "a", "A")]
    [InlineData("AllowAddStore", "ALLOWADDSTORE", " N ", "N")]
    [InlineData("AllowSavePwd", "ALLOWSAVEPWD", "S", "S")]
    [InlineData("AllowSavePwd", "ALLOWSAVEPWD", "n", "N")]
    [InlineData("AllowSavePwd", "ALLOWSAVEPWD", "A", "A")]
    public void AStorePolicyEmitsTheUpperCasedValue(string key, string @switch, string value, string expected)
    {
        Assert.Contains($"{@switch}={expected}", Args(new() { StorePolicy = Policy(key, value) }));
    }

    [Theory]
    [InlineData("Y")]
    [InlineData("Secure")]
    [InlineData("(not set)")]
    public void AnUndocumentedStorePolicyValueIsRejectedWithAWarning(string value)
    {
        var r = CitrixInstallArguments.Build(new() { StorePolicy = new() { AllowAddStore = value, AllowSavePwd = value } }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Equal(2, r.Warnings.Count);
    }

    private static CwaStorePolicy Policy(string key, string? value) =>
        key == "AllowAddStore" ? new() { AllowAddStore = value } : new() { AllowSavePwd = value };

    [Fact]
    public void EnableSsonIsEmittedOnlyTogetherWithIncludeSson()
    {
        Assert.Equal(["/silent"], Args(new() { Installation = new() { IncludeSSON = false, EnableSSON = true } }));
        Assert.Equal(["/silent", "/includeSSON", "ENABLE_SSON=Yes"], Args(new() { Installation = new() { IncludeSSON = true, EnableSSON = true } }));
        Assert.Equal(["/silent", "/includeSSON", "ENABLE_SSON=No"], Args(new() { Installation = new() { IncludeSSON = true, EnableSSON = false } }));
    }

    [Fact]
    public void AppProtectionUsesStartAppProtection()
    {
        var args = Args(new() { Installation = new() { AppProtection = true } });
        Assert.Contains("startAppProtection", args);
        Assert.DoesNotContain(args, a => a.Contains("includeappprotection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreLaunchAndSelfServiceEmitTheDocumentedTrueFalseValues()
    {
        Assert.Equal(["/silent", "/CleanInstall", "ENABLEPRELAUNCH=False", "SELFSERVICEMODE=True"],
            Args(new() { Installation = new() { CleanInstall = true, SessionPreLaunch = false, SelfServiceMode = true } }));
    }

    [Fact]
    public void CurrentReleaseTeamsIsYOrNAndZoomAndEpaAreOptOutsOnly()
    {
        Assert.Equal(["/silent", "/InstallMSTeamsPlugin=Y"],
            Args(new() { Plugins = new() { MSTeamsPlugin = true, ZoomPlugin = true, EPAClient = true } }));
        Assert.Equal(["/silent", "/InstallMSTeamsPlugin=N", "Installzoomplugin=N", "InstallEPAClient=N"],
            Args(new() { Plugins = new() { MSTeamsPlugin = false, ZoomPlugin = false, EPAClient = false } }));
    }

    [Fact]
    public void LtsrInstallsZoomThroughAddonsAndIgnoresTeamsWithAWarning()
    {
        var r = CitrixInstallArguments.Build(new() { Plugins = new() { MSTeamsPlugin = true, ZoomPlugin = true, WebExPlugin = true } }, "LTSR");
        Assert.Equal(["/silent", "ADDONS=ZoomVDIPlugin,WebexVDIPlugin"], r.Arguments);
        Assert.Single(r.Warnings, w => w.StartsWith("Plugins.MSTeamsPlugin", StringComparison.Ordinal));
    }

    [Fact]
    public void LtsrEmitsNoZoomOptOut()
    {
        Assert.Equal(["/silent"], Args(new() { Plugins = new() { ZoomPlugin = false } }, "LTSR"));
    }

    [Fact]
    public void CurrentReleaseAddonsCarryWebexOnly()
    {
        Assert.Equal(["/silent", "ADDONS=WebexVDIPlugin"], Args(new() { Plugins = new() { ZoomPlugin = true, WebExPlugin = true } }));
    }

    [Fact]
    public void SkipUberAgentUpgradeFollowsInstallUberAgent()
    {
        Assert.Equal(["/silent", "/InstallUberAgent", "/SkipUberAgentUpgrade"],
            Args(new() { Plugins = new() { UberAgent = true, UberAgentSkipUpgrade = true } }));
    }

    [Fact]
    public void SkipUberAgentUpgradeAloneWarnsAndIsSkipped()
    {
        var r = CitrixInstallArguments.Build(new() { Plugins = new() { UberAgent = false, UberAgentSkipUpgrade = true } }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void SessionRecordingIsCurrentReleaseOnly()
    {
        var config = new CitrixWorkspaceSwitches { Plugins = new() { SessionRecording = true } };
        Assert.Contains("/InstallSRAgent", Args(config));
        var ltsr = CitrixInstallArguments.Build(config, "LTSR");
        Assert.DoesNotContain("/InstallSRAgent", ltsr.Arguments);
        Assert.Single(ltsr.Warnings);
    }

    [Fact]
    public void AutoUpdateCheckIsOmittedWhenNotSet()
    {
        Assert.Equal(["/silent"], Args(new() { UpdateAndTelemetry = new() { AutoUpdateCheck = "" } }));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("Manual", "manual")]
    [InlineData("disabled", "disabled")]
    public void AutoUpdateCheckIsLowerCased(string value, string expected)
    {
        Assert.Equal(["/silent", $"AutoUpdateCheck={expected}"], Args(new() { UpdateAndTelemetry = new() { AutoUpdateCheck = value } }));
    }

    [Fact]
    public void AnUndocumentedAutoUpdateCheckValueIsRejected()
    {
        var r = CitrixInstallArguments.Build(new() { UpdateAndTelemetry = new() { AutoUpdateCheck = "off" } }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void CeipAndTracingUseTheVendorSpelling()
    {
        Assert.Equal(["/silent", "EnableCEIP=False", "EnableTracing=false"],
            Args(new() { UpdateAndTelemetry = new() { EnableCEIP = false, EnableTracing = false } }));
    }

    [Fact]
    public void AStoreFrontDiscoveryUrlIsPassedAsTyped()
    {
        Assert.Equal(["/silent", "STORE0=HR;https://sf.example.com/Citrix/Store/discovery;On;HR"],
            Args(new() { Store = new() { Name = "HR", Url = "https://sf.example.com/Citrix/Store/discovery" } }));
    }

    [Fact]
    public void AGatewayUrlWithAStoreFragmentIsPassedAsTyped()
    {
        Assert.Contains("STORE0=HR;https://ag.example.com#Store;On;HR",
            Args(new() { Store = new() { Name = "HR", Url = "https://ag.example.com#Store" } }));
    }

    [Fact]
    public void AStoreNameWithASpaceIsQuoted()
    {
        Assert.Contains("STORE0=\"HR Store;https://sf.example.com/Citrix/Store/discovery;On;HR Store\"",
            Args(new() { Store = new() { Name = "HR Store", Url = "https://sf.example.com/Citrix/Store/discovery" } }));
    }

    [Fact]
    public void ABlankStoreAddsNothingAndNoWarning()
    {
        var r = CitrixInstallArguments.Build(new() { Store = new() { Name = "", Url = "" } }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("Workspace", "cloud.example.com")]
    [InlineData("Workspace", "ftp://cloud.example.com")]
    [InlineData("A;B", "https://sf.example.com/discovery")]
    [InlineData("Workspace", "")]
    [InlineData("", "https://sf.example.com/discovery")]
    public void AnInvalidStoreIsRejectedWithAWarning(string name, string url)
    {
        var r = CitrixInstallArguments.Build(new() { Store = new() { Name = name, Url = url } }, "Current");
        Assert.Equal(["/silent"], r.Arguments);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void AddLocalIsOmittedUnlessCustomizeIsSet()
    {
        Assert.Equal(["/silent"], Args(new() { Components = new() { Customize = false, ReceiverInside = true } }));
    }

    [Fact]
    public void AddLocalListsTheSelectedComponentsInVendorSpelling()
    {
        Assert.Equal(["/silent", "ADDLOCAL=ReceiverInside,ICA_Client,AM,SelfService"],
            Args(new() { Components = new() { Customize = true, ReceiverInside = true, ICA_Client = true, AM = true, SelfService = true, USB = false } }));
    }

    [Fact]
    public void AnUnknownStreamIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => CitrixInstallArguments.Build(null, "Beta"));
    }

    // ── Definitions and staging ─────────────────────────────────

    [Theory]
    [InlineData("citrixworkspacecr.json", "Current")]
    [InlineData("citrixworkspace-ltsr-x86.json", "LTSR")]
    [InlineData("citrixworkspace-ltsr-x64.json", "LTSR")]
    [InlineData("citrixworkspace-ltsr-arm64.json", "LTSR")]
    public void EveryCitrixDefinitionBuildsItsArgumentsForItsOwnStream(string file, string stream)
    {
        var def = DefinitionFixture.Load(file);
        Assert.Equal(stream, def.CitrixSwitchStream);
        Assert.Equal("/silent", def.InstallArgs);
    }

    [Fact]
    public void TheStagedArgumentsComeFromTheSwitchSettings()
    {
        var logs = new List<string>();
        var args = StagingService.BuildCitrixInstallArgs("LTSR",
            new CitrixWorkspaceSwitches { Plugins = new() { MSTeamsPlugin = true, ZoomPlugin = true } }, logs.Add);

        Assert.Equal("/silent ADDONS=ZoomVDIPlugin", args);
        Assert.Contains(logs, l => l.StartsWith("WARNING: Plugins.MSTeamsPlugin", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApostropheInAnArgumentRefusesTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.BuildCitrixInstallArgs("Current",
            new CitrixWorkspaceSwitches { Store = new() { Name = "Bob's", Url = "https://sf.example.com/discovery" } }, null));
        Assert.Contains("apostrophe", ex.Message);
    }

    // ── Legacy settings ─────────────────────────────────────────

    [Fact]
    public void UntouchedLegacySettingsMigrateToNothingBeyondSilent()
    {
        Assert.Equal(["/silent"], CitrixInstallArguments.Build(new LegacyCwaInstallSettings().ToSwitches(), "Current").Arguments);
    }

    [Fact]
    public void CheckedLegacyBoxesAndChosenValuesCarryOver()
    {
        var legacy = new LegacyCwaInstallSettings
        {
            StoreName = "HR", StoreUrl = "https://sf.example.com/discovery", Clean = true, SingleSignOn = true,
            PluginTeams = true, AutoUpdate = "Disabled", AddStorePolicy = "Admin", SavePassword = "Off", PluginStorageResync = true
        };

        Assert.Equal(
            ["/silent", "/CleanInstall", "/includeSSON", "ENABLE_SSON=Yes", "/InstallMSTeamsPlugin=Y", "AutoUpdateCheck=disabled",
             "ALLOWSAVEPWD=N", "STORE0=HR;https://sf.example.com/discovery;On;HR"],
            CitrixInstallArguments.Build(legacy.ToSwitches(), "Current").Arguments);
    }

    [Fact]
    public void ALegacySettingsFileDeserializesIntoTheLegacyShape()
    {
        var json = """{ "StoreName": "HR", "Clean": true, "AutoUpdate": "Manual", "SavePassword": "On" }""";
        var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyCwaInstallSettings>(json)!;
        var switches = legacy.ToSwitches();

        Assert.True(switches.Installation!.CleanInstall);
        Assert.Equal("manual", switches.UpdateAndTelemetry!.AutoUpdateCheck);
        Assert.Equal("A", switches.StorePolicy!.AllowSavePwd);
        Assert.Equal("HR", switches.Store!.Name);
    }

    [Fact]
    public void TheNestedShapeMatchesTheSwitchFileThePowerShellPackagersRead()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CitrixWorkspaceSwitches
        {
            Store = new() { Name = "HR" }, Installation = new() { CleanInstall = true }, Plugins = new() { ZoomPlugin = false },
            UpdateAndTelemetry = new() { AutoUpdateCheck = "auto" }, StorePolicy = new() { AllowAddStore = "S" }, Components = new() { Customize = true, AM = true }
        });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("HR", root.GetProperty("Store").GetProperty("Name").GetString());
        Assert.True(root.GetProperty("Installation").GetProperty("CleanInstall").GetBoolean());
        Assert.False(root.GetProperty("Plugins").GetProperty("ZoomPlugin").GetBoolean());
        Assert.Equal("auto", root.GetProperty("UpdateAndTelemetry").GetProperty("AutoUpdateCheck").GetString());
        Assert.Equal("S", root.GetProperty("StorePolicy").GetProperty("AllowAddStore").GetString());
        Assert.True(root.GetProperty("Components").GetProperty("AM").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("Plugins").GetProperty("MSTeamsPlugin").ValueKind);
    }
}
