using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// The Settings contracts that need no window: the data root override, the launcher
/// handoff, the Script Signing round trip, the title checkboxes and the site change.
/// Each test points APP_PACKAGER_DATA_ROOT at its own folder and restores the variables.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-settings-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SettingsContractTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static JsonObject Stored() => JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!.AsObject();

    // ── Data root ───────────────────────────────────────────────

    [Fact]
    public void TheDataRootVariableMovesSettingsAndTheSrlPackrFolder()
    {
        Assert.Equal(Path.Combine(_root, "settings.json"), SettingsService.SettingsPath);
        Assert.Equal(Path.Combine(_root, "SRLPackr"), AppDataPaths.LegacyRoot);
        Assert.Equal(Path.Combine(_root, "Workbench"), ApplicationPackagerEngine.Services.Workbench.WorkbenchPaths.DefaultDataRoot);

        var service = new SettingsService();
        var s = service.Load();
        s.CompanyName = "Contoso";
        service.Save(s);
        Assert.Equal("Contoso", new SettingsService().Load().CompanyName);
    }

    // ── Launcher handoff ────────────────────────────────────────

    [Fact]
    public void LauncherValuesFillANeverSavedSiteAndAnEmptyProviderInMemoryOnly()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, " PS1 ");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "cm01.contoso.com");
        var service = new SettingsService();

        var s = service.Load();
        Assert.Equal("PS1", s.SiteCode);
        Assert.Equal("cm01.contoso.com", s.ProviderMachine);

        s.CompanyName = "Contoso";
        service.Save(s);
        var stored = Stored();
        Assert.Equal("MCM", (string?)stored["SiteCode"]);
        Assert.Equal("", (string?)stored["ProviderMachine"]);
        Assert.Equal("Contoso", (string?)stored["CompanyName"]);

        var again = service.Load();
        Assert.Equal("PS1", again.SiteCode);
        service.SaveOperatorEdit(again);
        stored = Stored();
        Assert.Equal("PS1", (string?)stored["SiteCode"]);
        Assert.Equal("cm01.contoso.com", (string?)stored["ProviderMachine"]);
        Assert.True((bool?)stored["SiteCodeSaved"]);
    }

    [Fact]
    public void ASavedSiteCodeAndProviderWinOverTheLauncher()
    {
        var service = new SettingsService();
        var s = service.Load();
        s.SiteCode = "ABC";
        s.ProviderMachine = "cm02";
        service.SaveOperatorEdit(s);

        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "PS1");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "cm01");
        var loaded = new SettingsService().Load();
        Assert.Equal("ABC", loaded.SiteCode);
        Assert.Equal("cm02", loaded.ProviderMachine);
    }

    [Fact]
    public void AFileWrittenBeforeTheFlagKeepsItsSiteCode()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsService.SettingsPath, """{ "SiteCode": "OLD", "ProviderMachine": "" }""");
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "PS1");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "cm01");

        var loaded = new SettingsService().Load();
        Assert.Equal("OLD", loaded.SiteCode);
        Assert.Equal("cm01", loaded.ProviderMachine);
    }

    // ── Script Signing ──────────────────────────────────────────

    private static ScriptSigningEditor Editor(params X509Certificate2[] certificates) =>
        new(() => new SigningCertificateService(_ => certificates));

    private static X509Certificate2 CodeSigning(string subject, int daysLeft = 30)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(SigningCertificateService.CodeSigningEku)], false));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(daysLeft));
    }

    [Fact]
    public void EverySigningSwitchDefaultsOff()
    {
        var editor = Editor();
        editor.Load(new SettingsService().Load().ScriptSigning);
        Assert.False(editor.SignDetection || editor.SignRequirements || editor.SignDeployment
                     || editor.RequireDetection || editor.RequireRequirements || editor.RequireDeployment || editor.TimestampRequired);
        Assert.Equal(SigningPolicy.CurrentUser, editor.StoreLocation);
        Assert.Equal("SHA256", editor.HashAlgorithm);
        Assert.Equal(SigningCertificateOption.NoneLabel, editor.SelectedCertificate?.Label);
    }

    [Fact]
    public void AnEditThatIsNotSavedNeverReachesTheStore()
    {
        var service = new SettingsService();
        service.SaveOperatorEdit(service.Load());
        var before = File.ReadAllText(SettingsService.SettingsPath);

        var editor = Editor();
        editor.Load(service.Load().ScriptSigning);
        editor.SignDetection = true;
        editor.RequireDeployment = true;
        editor.TimestampServer = "http://ts.example";

        Assert.Equal(before, File.ReadAllText(SettingsService.SettingsPath));
        Assert.False(service.Load().ScriptSigning.SignDetection);
    }

    [Fact]
    public void ASavedEditRoundTripsWithATrimmedTimestampAndTheSameDigest()
    {
        var signer = CodeSigning("CN=Settings Contract Signer");
        var editor = Editor(signer);
        var service = new SettingsService();
        editor.Load(service.Load().ScriptSigning);
        editor.SignDetection = true;
        editor.RequireRequirements = true;
        editor.TimestampServer = "  http://timestamp.example.test  ";
        editor.TimestampRequired = true;
        editor.HashAlgorithm = "SHA384";
        editor.SelectedCertificate = editor.Certificates.Single(c => c.Thumbprint == signer.Thumbprint.ToUpperInvariant());

        var s = service.Load();
        s.ScriptSigning = editor.ToPolicy();
        service.SaveOperatorEdit(s);

        var stored = new SettingsService().Load().ScriptSigning;
        Assert.True(stored.SignDetection);
        Assert.False(stored.SignRequirements);
        Assert.True(stored.RequireRequirements);
        Assert.Equal("http://timestamp.example.test", stored.TimestampServer);
        Assert.Equal("SHA384", stored.HashAlgorithm);
        Assert.Equal(signer.Thumbprint.ToUpperInvariant(), stored.CertificateThumbprint);
        Assert.Equal(editor.ToPolicy().PolicyDigest, stored.PolicyDigest);

        var reloaded = Editor(signer);
        reloaded.Load(stored);
        Assert.Equal(signer.Thumbprint.ToUpperInvariant(), reloaded.SelectedCertificate?.Thumbprint);
        Assert.Equal(stored.PolicyDigest, reloaded.ToPolicy().PolicyDigest);
    }

    [Fact]
    public void TheDigestChangesWithEveryField()
    {
        var editor = Editor();
        editor.Load(null);
        var digests = new HashSet<string> { editor.ToPolicy().PolicyDigest };
        foreach (var change in new Action[]
                 {
                     () => editor.SignDetection = true, () => editor.SignRequirements = true, () => editor.SignDeployment = true,
                     () => editor.RequireDetection = true, () => editor.RequireRequirements = true, () => editor.RequireDeployment = true,
                     () => editor.TimestampServer = "http://ts.example", () => editor.TimestampRequired = true,
                     () => editor.HashAlgorithm = "SHA512", () => editor.StoreLocation = SigningPolicy.LocalMachine
                 })
        {
            change();
            Assert.True(digests.Add(editor.ToPolicy().PolicyDigest));
        }
    }

    [Fact]
    public void AMalformedStoredThumbprintIsNotAdopted()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsService.SettingsPath, """{ "ScriptSigning": { "SignDetection": true, "CertificateThumbprint": "not-a-thumbprint" } }""");
        var stored = new SettingsService().Load().ScriptSigning;
        Assert.True(stored.SignDetection);
        Assert.Equal("", stored.CertificateThumbprint);
    }

    [Fact]
    public void TheCertificateListShowsSubjectExpiryAndUsabilityAndKeepsTheThumbprintAcrossRefresh()
    {
        var current = CodeSigning("CN=Current Signer");
        var expired = CodeSigning("CN=Second Signer");
        var editor = Editor(current, expired);
        editor.Load(new SigningPolicy { CertificateThumbprint = current.Thumbprint });

        Assert.Equal(3, editor.Certificates.Count);
        Assert.Equal(SigningCertificateOption.NoneLabel, editor.Certificates[0].Label);
        Assert.StartsWith("CN=Current Signer  (expires ", editor.Certificates[1].Label);
        Assert.Equal(current.Thumbprint.ToUpperInvariant(), editor.SelectedCertificate?.Thumbprint);
        Assert.Contains("Thumbprint: " + current.Thumbprint.ToUpperInvariant(), editor.CertificateDetail);

        editor.RefreshCertificates();
        Assert.Equal(current.Thumbprint.ToUpperInvariant(), editor.SelectedCertificate?.Thumbprint);
    }

    [Fact]
    public async Task TheTestRunsOffTheCallingThreadAndReportsIntegrityAndTrustSeparately()
    {
        var caller = Environment.CurrentManagedThreadId;
        var testThread = 0;
        var editor = new ScriptSigningEditor(() => new SigningCertificateService(_ => []), _ =>
        {
            testThread = Environment.CurrentManagedThreadId;
            return new SigningConfigurationTestResult { SignatureStatus = "UnknownError", SignatureIntact = true, TrustedOnThisHost = false, Ok = true };
        });

        await editor.TestConfigurationAsync();

        Assert.NotEqual(caller, testThread);
        Assert.Equal("Status: UnknownError. Signature intact: yes. Trusted on this host: no. Timestamp verified: False.", editor.TestResult);
        Assert.False(editor.TestBusy);
    }

    // ── Title checkboxes ────────────────────────────────────────

    [Fact]
    public void IncludeClearsAndDisablesRemove()
    {
        var options = new ApplicationTitleOptions();
        options.Load(include: false, remove: true);
        Assert.True(options.RemoveVersionFromTitleAvailable);
        Assert.Equal((false, true), options.ToSettings());

        options.IncludeVersionInTitle = true;
        Assert.False(options.RemoveVersionFromTitle);
        Assert.False(options.RemoveVersionFromTitleAvailable);
        Assert.Equal((true, false), options.ToSettings());

        options.Load(include: true, remove: true);
        Assert.Equal((true, false), options.ToSettings());
    }

    [Fact]
    public void TheGlobalTitleSettingsReachAPackageRunWithNoProfileOrEntry()
    {
        var s = new AppSettings { RemoveVersionFromTitle = true };
        var env = PackagerExecutionService.ChildEnvironment(null, s);
        Assert.Equal("NoVersion", env[ApplicationPackagerEngine.Services.Workbench.PackagerChildBridge.TitleModeVariable]);
        Assert.Equal("Allow", env[DeploymentTypeContentOptions.FallbackVariable]);
        Assert.Equal("Download", env[DeploymentTypeContentOptions.SlowNetworkVariable]);
        Assert.Equal("Tool 2.3.1",
            ApplicationTitle.Resolve("Tool 2.3.1", "2.3.1", ApplicationTitle.ResolveMode(null, "", s.IncludeVersionInTitle, false)));
        Assert.Equal("Tool",
            ApplicationTitle.Resolve("Tool 2.3.1", "2.3.1", ApplicationTitle.ResolveMode(null, "", s.IncludeVersionInTitle, s.RemoveVersionFromTitle)));
    }

    // ── Site change ─────────────────────────────────────────────

    [Fact]
    public void ASiteChangeClearsOnlyTheSiteResults()
    {
        var row = new PackagerInfo { Uid = "a", Application = "A" };
        row.CurrentVersion = "1.0";
        row.LatestVersion = "2.0";
        row.Status = "Update available";
        row.IsSelected = true;
        var checkedAt = DateTime.Now;
        row.LastChecked = checkedAt;

        PackagesViewModel.ClearSiteResults([row]);

        Assert.Equal("", row.CurrentVersion);
        Assert.Equal("Ready", row.Status);
        Assert.Equal("2.0", row.LatestVersion);
        Assert.True(row.IsSelected);
        Assert.Equal(checkedAt, row.LastChecked);
    }

    // ── Local installer folders ─────────────────────────────────

    [Fact]
    public void ARequiredFolderIsAskedForSavedAndACancelSkipsTheApplication()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_root, "have")).FullName;
        var chosen = Directory.CreateDirectory(Path.Combine(_root, "chosen")).FullName;
        var defs = new Dictionary<string, AppDefinition>
        {
            ["a"] = new() { Id = "a", LocalSource = new LocalSourceConfig { Mode = "Required" } },
            ["b"] = new() { Id = "b", LocalSource = new LocalSourceConfig { Mode = "Required" } },
            ["c"] = new() { Id = "c", LocalSource = new LocalSourceConfig { Mode = "Required" } },
            ["d"] = new() { Id = "d" }
        };
        var rows = defs.Keys.Select(k => new PackagerInfo { Uid = k, Application = "App " + k.ToUpperInvariant() }).ToList();
        var settings = new AppSettings { LocalSourceFolders = new() { ["a"] = existing, ["b"] = Path.Combine(_root, "gone") } };
        var prompts = new List<string>();
        var log = new List<string>();

        var kept = LocalSourceFolderPrompt.Confirm(rows, r => defs[r.Uid], settings,
            description => { prompts.Add(description); return prompts.Count == 1 ? chosen : null; }, log.Add, out var changed);

        Assert.Equal(["a", "b", "d"], kept.Select(r => r.Uid));
        Assert.True(changed);
        Assert.Equal(chosen, settings.LocalSourceFolders["b"]);
        Assert.Equal("Choose the folder that holds the App B installer. Its download requires a sign-in, so it stages from a local copy.", prompts[0]);
        Assert.Equal([$"Installer source for App B: {chosen}", "Skipped App C: no installer source folder chosen."], log);
    }
}
