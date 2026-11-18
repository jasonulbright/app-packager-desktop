using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-25: Settings, General, "Import from App Packager (PowerShell)". The preferences file of each shipped PowerShell version is read into a preview,
/// the preview is the only thing that writes, a credential is never read, and the PowerShell file stays as it was.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PowerShellOptionsImportTests
{
    private const string SecretMarker = "SECRET-MARKER";

    public static readonly string[] Versions = ["1.6.0.9", "2026.09.18.0089", "2026.10.11.0108", "2026.10.25.0122", "2026.11.14.0141"];

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var version in Versions) data.Add(version);
        return data;
    }

    private static string FixturePath(string version) =>
        Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests", "ApplicationPackagerEngine.Tests", "Fixtures", "PowerShellPreferences", $"preferences-{version}.json");

    private static string Fixture(string version) => File.ReadAllText(FixturePath(version));

    private static PowerShellImportRow Row(PowerShellImportPreview preview, string key) => preview.Rows.Single(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static void WithIsolatedSettings(Action<SettingsService> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-import-" + Guid.NewGuid().ToString("N"));
        var names = new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, root);
            Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
            Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
            Directory.CreateDirectory(AppDataPaths.Root);
            test(new SettingsService());
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakePrompts(Func<PowerShellImportPreview, PowerShellImportChoice> choose, string? picked = null) : IPowerShellImportPrompts
    {
        public int Previews { get; private set; }
        public int Picks { get; private set; }
        public string? PickFile(string? currentPath) { Picks++; return picked; }
        public PowerShellImportChoice ShowPreview(PowerShellImportPreview preview) { Previews++; return choose(preview); }
    }

    // ---- every shipped version

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheFileOfEveryShippedVersionGivesAPreviewWithRowsToImport(string version)
    {
        var preview = PowerShellOptionsImport.Preview(Fixture(version), new AppSettings());

        Assert.Null(preview.Error);
        Assert.True(preview.Differs >= 15, $"{version}: only {preview.Differs} rows differ");
        Assert.Equal("P01", Row(preview, "SiteCode").PowerShellValue);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "SiteCode").Outcome);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "ContentLayout").Outcome);
        Assert.Equal(preview.Rows.Count, preview.Imported + preview.Skipped + preview.Unknown);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheImportWritesTheMappedValuesAndNothingElse(string version)
    {
        var target = new AppSettings();
        var before = System.Text.Json.JsonSerializer.Serialize(new AppSettings());
        var preview = PowerShellOptionsImport.Preview(Fixture(version), target);

        var written = PowerShellOptionsImport.Apply(preview, target);

        Assert.True(preview.Differs == written, "Rows without a writer: " + string.Join(", ", preview.Rows.Where(r => r.Differs && !preview.CanWrite(r.Key)).Select(r => r.Key)) + $" ({preview.Differs} differ, {written} written)");
        Assert.Equal("P01", target.SiteCode);
        Assert.True(target.SiteCodeSaved);
        Assert.Equal("cm02.contoso.com", target.ProviderMachine);
        Assert.Equal(ContentLayouts.Flat, target.ContentLayout);
        Assert.Equal(25, target.EstimatedRuntimeMins);
        Assert.Equal(90, target.MaximumRuntimeMins);
        Assert.Equal("Contoso", target.CompanyName);
        Assert.EndsWith("bc5.txt", target.SideFiles["BeyondCompareKeyFile"]);
        Assert.True(target.IncludeVersionInTitle);
        Assert.Equal("Stage", target.OneClick.Action);
        Assert.True(target.OneClick.ForceOnLaunch);
        Assert.True(target.OneClick.DefaultDestinations!.Wsus);
        Assert.True(target.ContentDistribution.AutoDistribute);
        Assert.Equal("All DPs", target.ContentDistribution.DpGroupName);
        Assert.Equal("contoso.onmicrosoft.com", target.Intune.TenantId);
        Assert.Equal(DeploymentTargets.MecmAndIntune, target.Intune.DeploymentTarget);
        Assert.True(target.ScriptSigning.SignDetection);
        Assert.Equal(new string('A', 40), target.ScriptSigning.CertificateThumbprint);
        Assert.NotEqual(before, System.Text.Json.JsonSerializer.Serialize(target));
    }

    [Fact]
    public void AnOlderFileThatNamesTheServerUnderMecmGivesTheProvider()
    {
        var preview = PowerShellOptionsImport.Preview(Fixture("1.6.0.9"), new AppSettings());

        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "MECM.ServerFQDN").Outcome);
        Assert.Equal("MonthlyEnterprise", Row(preview, "M365Channel").PowerShellValue);
    }

    [Fact]
    public void TheNewestFileGivesWsusVulnerabilityAndSqlSettings()
    {
        var target = new AppSettings();
        PowerShellOptionsImport.Apply(PowerShellOptionsImport.Preview(Fixture("2026.11.14.0141"), target), target);

        Assert.Equal("wsus02.contoso.com", target.Wsus.ServerName);
        Assert.Equal(8531, target.Wsus.PortNumber);
        Assert.True(target.Wsus.UseSsl);
        Assert.Equal("SecurityUpdates", target.Wsus.Classification);
        Assert.Equal(120, target.Vulnerability.CacheTtlMinutes);
        Assert.True(target.Vulnerability.LookupAfterCheckCm);
        Assert.Equal(@"sql02.contoso.com\INST1,1444", target.DiscoverySqlServer);
        Assert.Equal("CM_P01", target.DiscoverySqlDatabase);
        Assert.True(target.Systems!.Wsus);
    }

    // ---- credentials

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ACredentialIsNeverReadIntoTheRowsAndNeverWritten(string version)
    {
        var target = new AppSettings { GitHubTokenProtected = "keep-github" };
        target.Intune.ClientSecretProtected = "keep-intune";
        target.Vulnerability.NvdApiKeyProtected = "keep-nvd";
        var preview = PowerShellOptionsImport.Preview(Fixture(version), target);

        PowerShellOptionsImport.Apply(preview, target);

        Assert.DoesNotContain(preview.Rows, r => r.PowerShellValue.Contains(SecretMarker) || r.ApdValue.Contains(SecretMarker) || r.Note.Contains(SecretMarker));
        var credentials = preview.Rows.Where(r => r.Key.EndsWith("Protected", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(credentials);
        Assert.All(credentials, r => { Assert.Equal(PowerShellImportOutcome.Skipped, r.Outcome); Assert.Equal("", r.PowerShellValue); });
        Assert.Equal("keep-github", target.GitHubTokenProtected);
        Assert.Equal("keep-intune", target.Intune.ClientSecretProtected);
        Assert.Equal("keep-nvd", target.Vulnerability.NvdApiKeyProtected);
        Assert.DoesNotContain(SecretMarker, System.Text.Json.JsonSerializer.Serialize(target));
    }

    [Theory]
    [InlineData("""{ "Intune": { "ClientSecret": "plain" } }""", "Intune.ClientSecret")]
    [InlineData("""{ "Vulnerability": { "NvdApiKey": "plain" } }""", "Vulnerability.NvdApiKey")]
    [InlineData("""{ "GitHubToken": "plain" }""", "GitHubToken")]
    [InlineData("""{ "Mail": { "Password": "plain" } }""", "Mail.Password")]
    public void APlainTextCredentialOfAnOlderBuildIsSkippedToo(string json, string key)
    {
        var preview = PowerShellOptionsImport.Preview(json, new AppSettings());

        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, key).Outcome);
        Assert.DoesNotContain("plain", string.Concat(preview.Rows.Select(r => r.PowerShellValue + r.Note)));
    }

    // ---- Q-42: every Intune key of the PowerShell file has a row with a reason or a value

    private static string IntuneFixture(string name) =>
        File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests", "ApplicationPackagerEngine.Tests", "Fixtures", "PowerShellPreferences", name));

    [Fact]
    public void EveryIntuneKeyHasARowAndNoneIsUnknown()
    {
        var preview = PowerShellOptionsImport.Preview(IntuneFixture("intune-all-keys.json"), new AppSettings());

        foreach (var key in new[] { "Systems.Intune", "Intune.DeploymentTarget", "Intune.PublishToIntune", "Intune.TenantId", "Intune.ClientId", "Intune.ClientSecretProtected", "DetectedTools.IntuneWinAppUtil" })
            Assert.NotEqual(PowerShellImportOutcome.Unknown, Row(preview, key).Outcome);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "Intune.DeploymentTarget").Outcome);
        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, "Intune.PublishToIntune").Outcome);
        Assert.Contains("deployment target", Row(preview, "Intune.PublishToIntune").Note);
        Assert.Contains("DPAPI", Row(preview, "Intune.ClientSecretProtected").Note);
        Assert.Contains("downloads its own", Row(preview, "DetectedTools.IntuneWinAppUtil").Note);
    }

    [Fact]
    public void ImportWritesTheIntuneValuesAndPublishFollowsTheTarget()
    {
        var target = new AppSettings();
        target.Intune.ClientSecretProtected = "keep-intune";
        var preview = PowerShellOptionsImport.Preview(IntuneFixture("intune-all-keys.json"), target);

        PowerShellOptionsImport.Apply(preview, target);

        Assert.Equal(DeploymentTargets.IntuneOnly, target.Intune.DeploymentTarget);
        Assert.True(DeploymentTargets.PublishesToIntune(target.Intune.DeploymentTarget));
        Assert.Equal("contoso.onmicrosoft.com", target.Intune.TenantId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", target.Intune.ClientId);
        Assert.Equal("keep-intune", target.Intune.ClientSecretProtected);
        Assert.DoesNotContain("SECRET-MARKER", string.Concat(preview.Rows.Select(r => r.PowerShellValue + r.Note)));
    }

    [Fact]
    public void AFileWithoutATargetAndPublishTrueWritesMecmAndIntune()
    {
        var target = new AppSettings();
        var preview = PowerShellOptionsImport.Preview(IntuneFixture("intune-legacy-toggle.json"), target);

        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "Intune.PublishToIntune").Outcome);
        PowerShellOptionsImport.Apply(preview, target);
        Assert.Equal(DeploymentTargets.MecmAndIntune, target.Intune.DeploymentTarget);

        var again = PowerShellOptionsImport.Preview(IntuneFixture("intune-legacy-toggle.json"), target);
        Assert.Equal(PowerShellImportOutcome.Same, Row(again, "Intune.PublishToIntune").Outcome);
    }

    [Fact]
    public void ImportingTwiceChangesNothingTheSecondTime()
    {
        var target = new AppSettings();
        PowerShellOptionsImport.Apply(PowerShellOptionsImport.Preview(IntuneFixture("intune-all-keys.json"), target), target);
        var second = PowerShellOptionsImport.Preview(IntuneFixture("intune-all-keys.json"), target);

        Assert.Equal(0, second.Rows.Count(r => r.Key.StartsWith("Intune.", StringComparison.Ordinal) && r.Differs));
        Assert.False(DeploymentTargets.PublishesToIntune(new AppSettings().Intune.DeploymentTarget));
    }

    [Fact]
    public void APublishValueThatIsNotABooleanIsSkipped()
    {
        var preview = PowerShellOptionsImport.Preview("""{ "Intune": { "DeploymentTarget": "MECM", "PublishToIntune": "yes" } }""", new AppSettings());

        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, "Intune.PublishToIntune").Outcome);
    }

    // ---- the SQL Server name: three keys of the PowerShell file, one name in APD

    [Theory]
    [InlineData("""{ "DiscoverySqlServer": "sql02", "DiscoverySqlInstance": "INST1", "DiscoverySqlPort": 1444 }""", @"sql02\INST1,1444")]
    [InlineData("""{ "DiscoverySqlServer": "sql02", "DiscoverySqlInstance": "", "DiscoverySqlPort": 0 }""", "sql02")]
    [InlineData("""{ "DiscoverySqlServer": "sql02", "DiscoverySqlInstance": "", "DiscoverySqlPort": 1433 }""", "sql02,1433")]
    [InlineData("""{ "DiscoverySqlServer": "sql02\\INST1,1444", "DiscoverySqlInstance": "", "DiscoverySqlPort": 0 }""", @"sql02\INST1,1444")]
    public void TheThreeSqlKeysBecomeOneSqlServerName(string json, string expected)
    {
        var target = new AppSettings();
        var preview = PowerShellOptionsImport.Preview(json, target);

        PowerShellOptionsImport.Apply(preview, target);

        Assert.Equal(expected, target.DiscoverySqlServer);
        foreach (var key in new[] { "DiscoverySqlServer", "DiscoverySqlInstance", "DiscoverySqlPort" })
            Assert.NotEqual(PowerShellImportOutcome.Unknown, Row(preview, key).Outcome);
    }

    [Fact]
    public void TheSqlRowsShowTheirPartOfTheCurrentApdName()
    {
        var target = new AppSettings { DiscoverySqlServer = @"cm01\SITE,2000" };
        var preview = PowerShellOptionsImport.Preview("""{ "DiscoverySqlServer": "cm01", "DiscoverySqlInstance": "SITE", "DiscoverySqlPort": 2000 }""", target);

        Assert.Equal("cm01", Row(preview, "DiscoverySqlServer").ApdValue);
        Assert.Equal("SITE", Row(preview, "DiscoverySqlInstance").ApdValue);
        Assert.Equal("2000", Row(preview, "DiscoverySqlPort").ApdValue);
        Assert.Equal(0, preview.Differs);
    }

    [Fact]
    public void ASqlPortAboveTheRangeIsSkippedAndAnInstanceWithABackslashIsSkipped()
    {
        var preview = PowerShellOptionsImport.Preview("""{ "DiscoverySqlServer": "sql02", "DiscoverySqlInstance": "a\\b", "DiscoverySqlPort": 70000 }""", new AppSettings());

        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, "DiscoverySqlInstance").Outcome);
        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, "DiscoverySqlPort").Outcome);
    }

    // ---- keys that the PowerShell app added: minimum Windows release, default cadence

    [Fact]
    public void TheMinimumWindowsReleaseAndTheDefaultCadenceAreImported()
    {
        var target = new AppSettings();
        var preview = PowerShellOptionsImport.Preview(IntuneFixture("new-keys-2026.11.18.json"), target);

        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "Intune.MinimumWindowsRelease").Outcome);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "AppFlow.DefaultCadenceDays").Outcome);
        PowerShellOptionsImport.Apply(preview, target);
        Assert.Equal("Windows11_23H2", target.Intune.MinimumWindowsRelease);
        Assert.Equal(30, target.OneClick.DefaultCadenceDays);

        var again = PowerShellOptionsImport.Preview(IntuneFixture("new-keys-2026.11.18.json"), target);
        Assert.Equal(PowerShellImportOutcome.Same, Row(again, "Intune.MinimumWindowsRelease").Outcome);
        Assert.Equal(PowerShellImportOutcome.Same, Row(again, "AppFlow.DefaultCadenceDays").Outcome);
    }

    [Theory]
    [InlineData("""{ "Intune": { "MinimumWindowsRelease": "latest" } }""", "Intune.MinimumWindowsRelease")]
    [InlineData("""{ "AppFlow": { "DefaultCadenceDays": 0 } }""", "AppFlow.DefaultCadenceDays")]
    [InlineData("""{ "AppFlow": { "DefaultCadenceDays": 366 } }""", "AppFlow.DefaultCadenceDays")]
    public void AValueThatApdRefusesForTheNewKeysIsSkipped(string json, string key)
    {
        var target = new AppSettings();
        var preview = PowerShellOptionsImport.Preview(json, target);

        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, key).Outcome);
        PowerShellOptionsImport.Apply(preview, target);
        Assert.Equal(7, target.OneClick.DefaultCadenceDays);
        Assert.Equal(IntuneWindowsReleases.Default, target.Intune.MinimumWindowsRelease);
    }

    // ---- skipped, unknown, differs

    [Fact]
    public void PerApplicationValuesAndMachineStateAreSkippedWithAReason()
    {
        var preview = PowerShellOptionsImport.Preview(Fixture("2026.11.14.0141"), new AppSettings());

        foreach (var key in new[] { "AppFlow.Tracked", "AppFlow.CadenceOverrides", "LocalSourceFolders", "HiddenApplications", "DiscoveryDismissed", "DetectedTools.SevenZipCli", "SSMSInstallOptions", "M365Options" })
        {
            var row = Row(preview, key);
            Assert.Equal(PowerShellImportOutcome.Skipped, row.Outcome);
            Assert.NotEmpty(row.Note);
        }
    }

    [Fact]
    public void AKeyWithoutAnApdSettingIsUnknownEvenInsideAKnownBlock()
    {
        var preview = PowerShellOptionsImport.Preview("""{ "FutureSetting": 5, "Wsus": { "ServerName": "w", "NewThing": true }, "Extra": { "A": 1 } }""", new AppSettings());

        Assert.Equal(PowerShellImportOutcome.Unknown, Row(preview, "FutureSetting").Outcome);
        Assert.Equal(PowerShellImportOutcome.Unknown, Row(preview, "Wsus.NewThing").Outcome);
        Assert.Equal(PowerShellImportOutcome.Unknown, Row(preview, "Extra.A").Outcome);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "Wsus.ServerName").Outcome);
        Assert.Equal(3, preview.Unknown);
    }

    [Fact]
    public void ARowThatMatchesTheApdValueIsSameAndNotWritten()
    {
        var current = new AppSettings { CompanyName = "Contoso", SiteCode = "P01" };
        var preview = PowerShellOptionsImport.Preview("""{ "CompanyName": "Contoso", "SiteCode": "P02" }""", current);

        Assert.Equal(PowerShellImportOutcome.Same, Row(preview, "CompanyName").Outcome);
        Assert.Equal(PowerShellImportOutcome.Import, Row(preview, "SiteCode").Outcome);
        Assert.Equal("P01", Row(preview, "SiteCode").ApdValue);
        Assert.Equal(2, preview.Imported);
        Assert.Equal(1, preview.Differs);
        Assert.Equal(1, PowerShellOptionsImport.Apply(preview, current));
    }

    [Theory]
    [InlineData("""{ "EstimatedRuntimeMins": 500 }""", "EstimatedRuntimeMins")]
    [InlineData("""{ "MaximumRuntimeMins": 5 }""", "MaximumRuntimeMins")]
    [InlineData("""{ "ContentLayout": "Spiral" }""", "ContentLayout")]
    [InlineData("""{ "SiteCode": "TOOLONGCODE" }""", "SiteCode")]
    [InlineData("""{ "ScriptSigning": { "CertificateThumbprint": "not-a-thumbprint" } }""", "ScriptSigning.CertificateThumbprint")]
    [InlineData("""{ "DownloadRoot": "C:\\temp\\ap" }""", "DownloadRoot")]
    [InlineData("""{ "Wsus": { "Classification": "Nonsense" } }""", "Wsus.Classification")]
    [InlineData("""{ "Systems": { "Intune": "yes" } }""", "Systems.Intune")]
    public void AValueThatApdRefusesIsSkippedAndNeverWritten(string json, string key)
    {
        var target = new AppSettings();
        var before = System.Text.Json.JsonSerializer.Serialize(target);
        var preview = PowerShellOptionsImport.Preview(json, target);

        Assert.Equal(PowerShellImportOutcome.Skipped, Row(preview, key).Outcome);
        Assert.Equal(0, PowerShellOptionsImport.Apply(preview, target));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(target));
    }

    [Fact]
    public void OnlyTheChosenRowsAreWritten()
    {
        var target = new AppSettings();
        var preview = PowerShellOptionsImport.Preview("""{ "SiteCode": "P01", "CompanyName": "Contoso" }""", target);

        var written = PowerShellOptionsImport.Apply(preview, target, new HashSet<string> { "CompanyName" });

        Assert.Equal(1, written);
        Assert.Equal("Contoso", target.CompanyName);
        Assert.Equal("MCM", target.SiteCode);
    }

    [Fact]
    public void ImportingTwiceLeavesNothingToDifferRoundTrip()
    {
        foreach (var version in Versions)
        {
            var target = new AppSettings();
            PowerShellOptionsImport.Apply(PowerShellOptionsImport.Preview(Fixture(version), target), target);

            var again = PowerShellOptionsImport.Preview(Fixture(version), target);

            Assert.Equal(0, again.Differs);
            Assert.True(again.Imported > 15, version);
        }
    }

    // ---- bad files

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("{\"SiteCode\": ")]
    public void ABadFileGivesAnErrorAndNoRowsAndWritesNothing(string json)
    {
        var target = new AppSettings();
        var before = System.Text.Json.JsonSerializer.Serialize(target);

        var preview = PowerShellOptionsImport.Preview(json, target);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Rows);
        Assert.Equal(0, PowerShellOptionsImport.Apply(preview, target));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(target));
        Assert.Equal(preview.Error, preview.Summary);
    }

    [Fact]
    public void AMissingFileGivesAnError()
    {
        var preview = PowerShellOptionsImport.PreviewFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json"), new AppSettings());

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Rows);
    }

    // ---- finding the file, and the flow

    [Fact]
    public void TheFileIsFoundNextToTheScriptAboveThePackagersFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Packagers"));
            Assert.Null(PowerShellOptionsImport.FindBesideScript([Path.Combine(root, "Packagers"), null, ""]));

            File.WriteAllText(Path.Combine(root, PowerShellOptionsImport.FileName), "{}");

            Assert.Equal(Path.Combine(root, PowerShellOptionsImport.FileName), PowerShellOptionsImport.FindBesideScript([null, Path.Combine(root, "Packagers") + "\\"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (string Folder, string File, string Hash) PowerShellFile(string version)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-psfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Packagers"));
        var file = Path.Combine(folder, PowerShellOptionsImport.FileName);
        File.WriteAllText(file, Fixture(version));
        return (folder, file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Fact]
    public void NothingIsWrittenBeforeTheOperatorAcceptsThePreview() => WithIsolatedSettings(service =>
    {
        var (folder, file, hash) = PowerShellFile("2026.11.14.0141");
        try
        {
            var before = System.Text.Json.JsonSerializer.Serialize(service.Load());
            var prompts = new FakePrompts(_ => new PowerShellImportChoice(false, false, new HashSet<string>()));

            var result = new PowerShellImportFlow(service, prompts, () => [Path.Combine(folder, "Packagers")]).Run();

            Assert.False(result.Written);
            Assert.Equal(1, prompts.Previews);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(new SettingsService().Load()));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    [Fact]
    public void AcceptingThePreviewWritesTheSavedSettingsAndLeavesThePowerShellFileAsItWas() => WithIsolatedSettings(service =>
    {
        var (folder, file, hash) = PowerShellFile("2026.11.14.0141");
        try
        {
            var prompts = new FakePrompts(p => new PowerShellImportChoice(true, false, p.Rows.Where(r => r.Differs).Select(r => r.Key).ToHashSet()));

            var result = new PowerShellImportFlow(service, prompts, () => [Path.Combine(folder, "Packagers")]).Run();

            Assert.True(result.Written);
            Assert.Equal(file, result.SourcePath);
            var saved = new SettingsService().Load();
            Assert.Equal("P01", saved.SiteCode);
            Assert.Equal("wsus02.contoso.com", saved.Wsus.ServerName);
            Assert.DoesNotContain(SecretMarker, File.ReadAllText(SettingsService.SettingsPath));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            Assert.Equal(0, prompts.Picks);
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    [Fact]
    public void WithoutAFileNextToTheScriptThePickerAsksAndABadFileWritesNothing() => WithIsolatedSettings(service =>
    {
        var bad = Path.Combine(Path.GetTempPath(), "apd-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bad, "{ broken");
        try
        {
            var before = System.Text.Json.JsonSerializer.Serialize(service.Load());
            PowerShellImportPreview? shown = null;
            var prompts = new FakePrompts(p => { shown = p; return new PowerShellImportChoice(true, false, new HashSet<string>()); }, bad);

            var result = new PowerShellImportFlow(service, prompts, () => []).Run();

            Assert.False(result.Written);
            Assert.Equal(1, prompts.Picks);
            Assert.NotNull(shown!.Error);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(new SettingsService().Load()));
        }
        finally { File.Delete(bad); }
    });

    [Fact]
    public void CancellingThePickerEndsTheImport() => WithIsolatedSettings(service =>
    {
        var prompts = new FakePrompts(_ => throw new InvalidOperationException("no preview without a file"), null);

        var result = new PowerShellImportFlow(service, prompts, () => []).Run();

        Assert.False(result.Written);
        Assert.Equal(0, prompts.Previews);
    });

    // ---- the page and the window

    [Fact]
    public void TheGeneralPageHasTheImportButtonWithAPreviewHint()
    {
        var xaml = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));
        var general = xaml[xaml.IndexOf("x:Name=\"page_general\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"page_sidebar\"", StringComparison.Ordinal)];

        Assert.Contains("ImportFromPowerShellCommand", general);
        Assert.Contains("Import from App Packager (PowerShell)", general);
    }

    public static TheoryData<ApplicationTheme> Themes() => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    [Theory]
    [MemberData(nameof(Themes))]
    public void ThePreviewWindowShowsEveryRowAndOnlyChecksTheRowsThatDiffer(ApplicationTheme theme) => WithApplication(theme, () =>
    {
        var preview = PowerShellOptionsImport.Preview(Fixture("2026.11.14.0141"), new AppSettings());
        var window = new PowerShellImportWindow(preview) { Width = 1920, Height = 1032, Left = -32000, Top = -32000, ShowInTaskbar = false };
        Show(window);
        try
        {
            Settle(window);
            var grid = ById<System.Windows.Controls.DataGrid>(window, "dgImportRows");
            var rows = grid.ItemsSource.Cast<PowerShellImportRowView>().ToList();

            Assert.Equal(preview.Rows.Count, rows.Count);
            Assert.All(rows, r => Assert.Equal(r.Differs, r.Selected));
            Assert.Equal("Differs", rows[0].OutcomeText);
            Assert.Contains(preview.Summary, ById<System.Windows.Controls.TextBlock>(window, "txtImportSummary").Text);
            Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnImportAccept").IsEnabled);
            Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnImportCancel").IsDefault);

            var folder = Environment.GetEnvironmentVariable("APD_SETTINGS_SHOTS");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, $"import-preview-{theme.ToString().ToLowerInvariant()}.png"));
                encoder.Save(stream);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ThePreviewWindowShowsTheIntuneRowsWithTheirReasons(ApplicationTheme theme) => WithApplication(theme, () =>
    {
        var preview = PowerShellOptionsImport.Preview(IntuneFixture("intune-all-keys.json"), new AppSettings());
        var window = new PowerShellImportWindow(preview) { Width = 1920, Height = 1032, Left = -32000, Top = -32000, ShowInTaskbar = false };
        Show(window);
        try
        {
            Settle(window);
            var grid = ById<System.Windows.Controls.DataGrid>(window, "dgImportRows");
            var rows = grid.ItemsSource.Cast<PowerShellImportRowView>().ToList();

            Assert.Equal(preview.Rows.Count, rows.Count);
            Assert.Contains(rows, r => r.Key == "Intune.PublishToIntune");
            Assert.Contains(rows, r => r.Key == "Intune.ClientSecretProtected");
            Assert.Contains(rows, r => r.Key == "DetectedTools.IntuneWinAppUtil");

            var folder = Environment.GetEnvironmentVariable("APD_SETTINGS_SHOTS");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, $"import-preview-intune-{theme.ToString().ToLowerInvariant()}.png"));
                encoder.Save(stream);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePreviewWindowOfABadFileOffersNoImport() => WithApplication(ApplicationTheme.Light, () =>
    {
        var window = new PowerShellImportWindow(PowerShellOptionsImport.Preview("{ nope", new AppSettings())) { Left = -32000, Top = -32000, ShowInTaskbar = false };
        Show(window);
        try
        {
            Settle(window);
            Assert.False(ById<Wpf.Ui.Controls.Button>(window, "btnImportAccept").IsEnabled);
            Assert.Contains("not valid JSON", ById<System.Windows.Controls.TextBlock>(window, "txtImportSummary").Text);
        }
        finally { window.Close(); }
    });
}
