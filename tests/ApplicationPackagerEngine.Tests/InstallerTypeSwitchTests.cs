using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Installer type setting (EXE or MSI): a definition that ships both types declares the other one in InstallerTypeOverrides; the
/// setting InstallerType picks the type, and the staged definition then carries that type's download, silent switches and detection.
/// </summary>
public class InstallerTypeSwitchTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static AppDefinition Load(string file)
    {
        var path = Path.Combine(IconPackService.BundledDefinitionsDirectory(), file);
        var def = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path), Json)!;
        def.SourcePath = path;
        return def;
    }

    [Theory]
    [InlineData("7zip.json", "MSI", "EXE")]
    [InlineData("notepadplusplus.json", "EXE", "MSI")]
    [InlineData("teamviewerhost.json", "MSI", "EXE")]
    public void TheThreeApplicationsOfferBothTypesWithTheShippedTypeFirst(string file, string first, string second)
    {
        var def = Load(file);
        Assert.True(def.OffersInstallerTypes);
        Assert.Equal([first, second], def.InstallerTypeChoices);
        Assert.Equal(first, def.InstallerType);
    }

    [Fact]
    public void AnApplicationWithOneTypeOffersNoChoice()
    {
        var def = Load("teamviewer.json");
        Assert.False(def.OffersInstallerTypes);
        Assert.Same(def, InstallerTypeSwitch.Apply(def, "EXE"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MSIX")]
    [InlineData("msi")]
    public void TheDefaultTypeAnUnknownWordAndAnEmptyChoiceKeepTheDefinition(string? chosen)
    {
        var def = Load("7zip.json");
        Assert.Equal("MSI", InstallerTypeSwitch.Apply(def, chosen).InstallerType);
        Assert.Contains("-x64.msi", InstallerTypeSwitch.Apply(def, chosen).DownloadUrl);
    }

    [Fact]
    public void SevenZipExeUsesTheSilentSwitchAndTheRegistryDetection()
    {
        var def = InstallerTypeSwitch.Apply(Load("7zip.json"), "exe");
        Assert.Equal("EXE", def.InstallerType);
        Assert.EndsWith("7z{version}-x64.exe", def.DownloadUrl);
        Assert.Equal("/S", def.InstallArgs);
        Assert.Equal("/S", def.UninstallArgs);
        Assert.Contains("Uninstall.exe", def.UninstallCommand);
        Assert.Equal("RegistryKeyValue", def.Detection.Type);
        Assert.Contains("x64\\.exe", def.VersionScraper.PayloadHashQuery);
        Assert.True(def.RequirePayloadHash);
        Assert.Equal("7-Zip (x64)", def.Application);
        Assert.Equal(def.Id, Load("7zip.json").Id);
        Assert.Equal(Load("7zip.json").SourcePath, def.SourcePath);
        Assert.Null(def.InstallerTypeOverrides);
    }

    [Fact]
    public void NotepadPlusPlusMsiUsesTheMsiSwitchesAndKeepsTheRest()
    {
        var def = InstallerTypeSwitch.Apply(Load("notepadplusplus.json"), "MSI");
        Assert.Equal("MSI", def.InstallerType);
        Assert.EndsWith(".Installer.x64.msi", def.DownloadUrl);
        Assert.Equal("/qn /norestart", def.InstallArgs);
        Assert.Equal("/x {ProductCode} /qn /norestart", def.UninstallArgs);
        Assert.Equal("notepad++.exe", def.Detection.FileName);
        Assert.Equal("Notepad++", def.CMName);
    }

    [Fact]
    public void TeamViewerHostExeDownloadsAndMsiStaysOnTheLocalSource()
    {
        var host = Load("teamviewerhost.json");
        Assert.True(host.LocalSource!.IsRequired);
        Assert.Contains("APITOKEN", host.InstallerSettings!.Select(s => s.CommandFragment).First(f => f.StartsWith("APITOKEN")));

        var exe = InstallerTypeSwitch.Apply(host, "EXE");
        Assert.Equal("EXE", exe.InstallerType);
        Assert.Null(exe.LocalSource);
        Assert.Equal("https://download.teamviewer.com/download/TeamViewer_Host_Setup.exe", exe.DownloadUrl);
        Assert.Equal("/S", exe.InstallArgs);
        Assert.Equal("HttpRegex", exe.VersionScraper.Type);
        Assert.False(exe.Detection.ArpEntry!.WindowsInstaller);
        // MSI properties do not apply to the EXE.
        Assert.True(exe.InstallerSettings is null or { Length: 0 });
        Assert.Equal("LocalFile", host.VersionScraper.Type);
    }

    [Fact]
    public void TheGridColumnWritesTheChoiceToTheSameEntryThatThePackageRunReads()
    {
        var column = ApplicationColumns.Find("installertype")!;
        var work = new ApplicationWork();
        var row = new SupersedenceRow
        {
            Uid = "u", ApplicationId = "catalog:1", DefinitionId = "def1", Application = "App", Vendor = "V", Architecture = "x64",
            InstallerTypes = ["MSI", "EXE"]
        };
        Assert.Null(column.NotApplicable(work, row));
        Assert.Equal("Packager default", column.Get(work, row));
        Assert.True(column.Set(work, row, "EXE"));
        Assert.Equal("EXE", work.Conditions["def1"].InstallerType);
        Assert.Equal("EXE", column.Get(work, row));
        Assert.False(column.Set(work, row, "MSIX"));
        Assert.True(column.Set(work, row, column.ResetValue));
        Assert.Empty(work.Conditions);
    }

    [Fact]
    public void TheColumnIsDisabledAndChangesNothingForAnApplicationWithOneType()
    {
        var column = ApplicationColumns.Find("installertype")!;
        var work = new ApplicationWork();
        var row = new SupersedenceRow { Uid = "u", ApplicationId = "catalog:1", DefinitionId = "def1", Application = "App", Vendor = "V", Architecture = "x64" };
        Assert.NotNull(column.NotApplicable(work, row));
        Assert.False(column.Set(work, row, "EXE"));
        Assert.Empty(work.Conditions);
        Assert.False(string.IsNullOrWhiteSpace(column.Tooltip));
    }

    [Fact]
    public void TheCopyOfTheMapKeepsTheInstallerType()
    {
        var map = new Dictionary<string, DeploymentConditionEntry> { ["d"] = new() { InstallerType = "MSI" } };
        Assert.Equal("MSI", ApplicationGridSettings.Copy(map)["d"].InstallerType);
    }

    [Fact]
    public void TheMigratedProfileCarriesTheSavedInstallerType()
    {
        var profile = ApplicationPackagerEngine.Models.Workbench.WorkbenchProfile.CreateNew("catalog:1");
        Assert.Null(profile.Get("InstallerType").Value);
        Assert.True(profile.Get("InstallerType").Found);
        var condition = System.Text.Json.Nodes.JsonNode.Parse("""{ "InstallerType": "MSI" }""")!;
        ApplicationPackagerEngine.Services.Workbench.LegacyPreferenceMigration.ApplyConditions(profile, condition);
        Assert.Equal("MSI", profile.Get("InstallerType").Text);
    }
}
