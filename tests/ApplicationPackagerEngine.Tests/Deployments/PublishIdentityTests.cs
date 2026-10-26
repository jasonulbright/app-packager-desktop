using System.Data.SQLite;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// A ConfigMgr publish links its inventory record to the application at publish time: the CI_ID
/// and the ModelName the packaging run read back reach the real inventory, also in an older
/// database, and the signing rule then catches a rename made in the console before any lookup.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishIdentityTests : IDisposable
{
    private const string Model = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_47899247-5d1c-426d-80cb-cf82c3313247";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-publish-identity-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public PublishIdentityTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, Path.Combine(_root, "appdata"));
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(_root);
        var settings = new SettingsService();
        var s = settings.Load();
        s.SiteCode = "MCM";
        s.ProviderMachine = "cm01.contoso.com";
        s.DownloadRoot = Path.Combine(_root, "downloads");
        settings.SaveOperatorEdit(s);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static InventoryEnvironmentIdentity Site => InventoryEnvironmentIdentity.ConfigMgr("MCM", "cm01.contoso.com");

    private static PackagerExecutionService Service(ApplicationInventoryService inventory)
    {
        var settings = new SettingsService();
        return new PackagerExecutionService(settings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(settings), new DeploymentConditionsService(settings), inventory);
    }

    private static PackagerExecutionResult Published(string? ciId, string? model) => new()
    {
        StdOut = "Packaged: Tool",
        ConfigMgrOutcome = InventoryOutcome.Succeeded,
        ConfigMgrObjectId = "Tool",
        ConfigMgrCiId = ciId,
        ConfigMgrModelName = model,
        PublishedVersion = "1.0",
        PublishedBuildId = "build-1"
    };

    private static ProductSigningCheck Check(ApplicationInventoryService inventory) =>
        new(inventory, () => new ConfigMgrConnection("MCM", "cm01.contoso.com"), () => new SigningPolicy { RequireDeployment = true });

    /// <summary>A database of the release before the ConfigMgr identity columns, with one publish record.</summary>
    private string OlderDatabase()
    {
        var path = Path.Combine(_root, "older.db");
        using var connection = new SQLiteConnection($"Data Source={path};Version=3;Pooling=False;");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = $"""
            CREATE TABLE destination_inventory (packager_uid TEXT NOT NULL, destination TEXT NOT NULL, environment_key TEXT NOT NULL, action TEXT NOT NULL,
                application_id TEXT, profile_id TEXT, last_attempt_version TEXT, last_attempt_outcome TEXT NOT NULL, last_attempt_object_id TEXT, last_attempt_at TEXT,
                last_success_version TEXT, last_success_object_id TEXT, last_success_at TEXT, last_success_build_id TEXT, last_attempt_detail TEXT,
                PRIMARY KEY(packager_uid, destination, environment_key, action));
            INSERT INTO destination_inventory VALUES('older', 'ConfigMgr', '{Site.Key.Replace("'", "''")}', 'Publish', NULL, NULL, '0.9', 'Succeeded', 'Older', '2026-10-01T00:00:00.0000000+00:00',
                '0.9', 'Older', '2026-10-01T00:00:00.0000000+00:00', 'b0', NULL);
            """;
        create.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void APublishIntoAnOlderDatabaseStoresTheCiIdAndTheModelName()
    {
        var inventory = new ApplicationInventoryService(OlderDatabase());
        var row = new PackagerInfo { Uid = "tool", Application = "Tool" };

        Service(inventory).RecordPackageOutcomes(row, null, DeploymentTargets.Mecm, Published("16780662", Model));

        var state = Assert.IsType<InventoryDestinationState>(inventory.GetDestinationState("tool", Site, "Publish"));
        Assert.Equal(("Tool", "16780662", Model), (state.LastSuccessObjectId, state.ConfigMgrCiId, state.ConfigMgrModelName));
        var older = Assert.IsType<InventoryDestinationState>(new ApplicationInventoryService(Path.Combine(_root, "older.db")).GetDestinationState("older", Site, "Publish"));
        Assert.Equal(("Older", (string?)null, (string?)null), (older.LastSuccessObjectId, older.ConfigMgrCiId, older.ConfigMgrModelName));
    }

    [Theory]
    [InlineData("16780662", Model)]
    [InlineData("16780662", null)]
    public void ARenameInTheConsoleAfterThePublishIsCaughtByTheSigningRule(string ciId, string? model)
    {
        var inventory = new ApplicationInventoryService(Path.Combine(_root, "history.db"));
        var row = new PackagerInfo { Uid = "tool", Application = "Tool" };

        Service(inventory).RecordPackageOutcomes(row, null, DeploymentTargets.Mecm, Published(ciId, model));
        var renamed = new SiteApplication("Tool (renamed)", "16780665", Model, ["16780662", "16780665"]);

        Assert.True(Check(inventory).IsPackaged(renamed));
        Assert.Contains("This product packaged application Tool (renamed) (published as Tool)", Check(inventory).RequiredDeploymentRefusal(renamed));
        Assert.Equal(Model, inventory.GetDestinationState("tool", Site, "Publish")!.ConfigMgrModelName);
    }

    [Fact]
    public void AFailedPublishLinksNothing()
    {
        var inventory = new ApplicationInventoryService(Path.Combine(_root, "history.db"));
        var row = new PackagerInfo { Uid = "tool", Application = "Tool" };

        Service(inventory).RecordPackageOutcomes(row, null, DeploymentTargets.Mecm,
            new PackagerExecutionResult { ExitCode = 1, ConfigMgrOutcome = InventoryOutcome.Failed, ConfigMgrCiId = "16780662", ConfigMgrModelName = Model, PublishedVersion = "1.0" });

        var state = inventory.GetDestinationState("tool", Site, "Publish")!;
        Assert.Equal(((string?)null, (string?)null), (state.ConfigMgrCiId, state.ConfigMgrModelName));
    }

    [Fact]
    public void ARepublishWithANewRevisionReplacesTheCiIdAndKeepsTheModel()
    {
        var inventory = new ApplicationInventoryService(Path.Combine(_root, "history.db"));
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("tool", null, Site, "Publish", "1.0", InventoryOutcome.Succeeded, "Tool", at,
            ConfigMgrCiId: "16780662", ConfigMgrModelName: Model));
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("tool", null, Site, "Publish", "1.1", InventoryOutcome.Succeeded, "Tool", at.AddMinutes(1),
            ConfigMgrCiId: "16780670", ConfigMgrModelName: Model));

        var state = inventory.GetDestinationState("tool", Site, "Publish")!;
        Assert.Equal(("16780670", Model), (state.ConfigMgrCiId, state.ConfigMgrModelName));
    }

    [Fact]
    public void ThePackagingScriptPrintsTheModelNameOfTheFinalApplicationAndTheRunReadsIt()
    {
        var manifest = new StageManifest
        {
            AppName = "Widget", Publisher = "Contoso", SoftwareVersion = "1.0", InstallerType = "EXE", InstallerFile = "setup.exe",
            Detection = new StageDetection { Type = "File", FilePath = @"%ProgramFiles%\Widget", FileName = "widget.exe", ExpectedValue = "1.0" }
        };
        Assert.Contains("Write-Host \"Final ModelName: $($finalApp.ModelName)\"", MecmTarget.BuildPackageScript(manifest, "Widget", @"\\s\c", "MCM", "", 15, 30));
        const string output = "CI_ID: 16779648\r\nCleaned revision history\r\nFinal CI_ID: 16779651\r\nFinal ModelName: " + Model + "\r\nApplication created successfully";

        Assert.Equal("16779651", MecmTarget.ReadCiId(output));
        Assert.Equal(Model, MecmTarget.ReadModelName(output));
        Assert.Equal("", MecmTarget.ReadModelName("Final ModelName: not a model name"));
        Assert.Equal("", MecmTarget.ReadModelName("CI_ID: 1"));
    }
}
