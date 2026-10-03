using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using static ApplicationPackagerEngine.Tests.Wsus.WsusTestSupport;

namespace ApplicationPackagerEngine.Tests.Wsus;

public class WsusRuleBuilderTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _content;
    private static readonly WsusRuleBuilder Builder = new();

    private static readonly ArpEntryDetection HostingBundleArp = new()
    {
        View = "32",
        DisplayNamePrefix = "Microsoft .NET 8.0.",
        DisplayNameSuffix = " - Windows Server Hosting",
        Publisher = "Microsoft Corporation",
        Version = "8.0.31"
    };

    public WsusRuleBuilderTests() => _content = NewContentFolder(_temp);

    public void Dispose() => _temp.Dispose();

    private static WsusApplicabilityRules Rules(StageDetection detection) => Builder.Build(Manifest(m => m.Detection = detection));

    [Fact]
    public void MapsADisplayVersionDetectionToThisVersionOrNewerAndPresentWithAnOlderVersion()
    {
        var rules = Builder.Build(Manifest());
        Assert.Equal($"<bar:RegSzToVersion Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"{ArpKey}\" Value=\"DisplayVersion\" Comparison=\"GreaterThanOrEqualTo\" Data=\"5.4.2.0\" />", rules.IsInstalled);
        Assert.Equal($"<lar:And><bar:RegValueExists Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"{ArpKey}\" Value=\"DisplayVersion\" Type=\"REG_SZ\" /><bar:RegSzToVersion Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"{ArpKey}\" Value=\"DisplayVersion\" Comparison=\"LessThan\" Data=\"5.4.2.0\" /></lar:And>", rules.IsInstallable);
    }

    [Fact]
    public void GuardsTheOlderCheckBecauseAVersionComparisonAloneHoldsWithoutTheValue()
    {
        var rules = Builder.Build(Manifest());
        var versionOnly = Regex.Match(rules.IsInstallable, "<bar:RegSzToVersion [^>]*/>").Value;
        Assert.True(new RuleComputer().Evaluate(RuleComputer.Root(versionOnly)));
        Assert.False(new RuleComputer().Outcome(rules).Offered);
    }

    [Theory]
    [InlineData("product absent", null, false, false)]
    [InlineData("uninstall key without a version", "", false, false)]
    [InlineData("older version", "5.4.1", false, true)]
    [InlineData("older version with two parts", "5.3", false, true)]
    [InlineData("target version", "5.4.2", true, false)]
    [InlineData("newer version", "5.10", true, false)]
    public void OffersARegistryDetectedUpdateOnlyOverAnOlderInstalledVersion(string name, string? version, bool installed, bool offered)
    {
        var computer = new RuleComputer();
        if (version is not null)
            computer.Key(ArpKey, "64", version.Length == 0 ? [] : new() { ["DisplayVersion"] = version });
        Assert.Equal((installed, offered), computer.Outcome(Builder.Build(Manifest())));
        Assert.NotEmpty(name);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("5.4.1.9", false, true)]
    [InlineData("5.4.2.0", true, false)]
    [InlineData("6.0.0.0", true, false)]
    public void OffersAFileVersionUpdateOnlyOverAnOlderInstalledVersion(string? version, bool installed, bool offered)
    {
        var rules = Rules(FileClause(@"C:\Program Files\Contoso", "tool.exe", "Version", "5.4.2", op: "GreaterEquals"));
        var computer = new RuleComputer();
        if (version is not null) computer.Files[@"38|Contoso\tool.exe"] = version;
        Assert.Equal((installed, offered), computer.Outcome(rules));
    }

    [Theory]
    [InlineData(false, "5.4.1", false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(true, "5.4.1", false, true)]
    [InlineData(true, "5.4.2", true, false)]
    public void RequiresThePresenceClausesAndAnOlderVersionForAnAndDetection(bool marker, string? version, bool installed, bool offered)
    {
        var rules = Rules(new StageDetection
        {
            Type = "Compound", Connector = "And",
            Clauses = [FileClause(@"C:\Program Files\Contoso", "tool.exe", "Existence"), Registry(ArpKey, "5.4.2")]
        });
        var computer = new RuleComputer();
        if (version is not null) computer.Key(ArpKey, "64", new() { ["DisplayVersion"] = version });
        if (marker) computer.Files[@"38|Contoso\tool.exe"] = "";
        Assert.Equal((installed, offered), computer.Outcome(rules));
        Assert.True(WellFormed(rules.IsInstallable));
    }

    [Theory]
    [InlineData(null, "5.4.1.0", false, false)]
    [InlineData("5.4.1", "5.4.1.0", false, true)]
    [InlineData("5.4.1", "5.4.2.0", false, true)]
    [InlineData("5.4.2", "5.4.2.0", true, false)]
    public void RequiresEveryVersionClauseOfAnAndDetectionToFindItsValue(string? registry, string file, bool installed, bool offered)
    {
        var rules = Rules(new StageDetection
        {
            Type = "Compound", Connector = "And",
            Clauses = [Registry(ArpKey, "5.4.2"), FileClause(@"C:\Program Files\Contoso", "tool.exe", "Version", "5.4.2")]
        });
        var computer = new RuleComputer();
        if (registry is not null) computer.Key(ArpKey, "64", new() { ["DisplayVersion"] = registry });
        computer.Files[@"38|Contoso\tool.exe"] = file;
        Assert.Equal((installed, offered), computer.Outcome(rules));
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("5.4.1", null, false, true)]
    [InlineData("5.4.1", "5.4.2", true, false)]
    public void OffersAnOrDetectionWhereOneViewIsOlderAndNoViewIsCurrent(string? view64, string? view32, bool installed, bool offered)
    {
        var rules = Rules(new StageDetection
        {
            Type = "Compound", Connector = "Or",
            Clauses = [Registry(ArpKey, "5.4.2", is64Bit: true), Registry(ArpKey, "5.4.2", is64Bit: false)]
        });
        var computer = new RuleComputer();
        if (view64 is not null) computer.Key(ArpKey, "64", new() { ["DisplayVersion"] = view64 });
        if (view32 is not null) computer.Key(ArpKey, "32", new() { ["DisplayVersion"] = view32 });
        Assert.Equal((installed, offered), computer.Outcome(rules));
    }

    [Fact]
    public void MapsGreaterThanToAnOlderCheckOfLessThanOrEqualTo()
    {
        var rules = Rules(Registry(ArpKey, "5.4.2", op: "GreaterThan"));
        Assert.Matches("Comparison=\"GreaterThan\" Data=\"5\\.4\\.2\\.0\"", rules.IsInstalled);
        Assert.Matches("Comparison=\"LessThanOrEqualTo\" Data=\"5\\.4\\.2\\.0\"", rules.IsInstallable);
    }

    public static TheoryData<string, string, StageDetection> CannotShowAnOlderVersion => new()
    {
        { "file existence", "only checks that a file or registry key exists", FileClause(@"C:\Program Files\Contoso", "tool.exe", "Existence") },
        { "folder named for this version", "only checks that a file or registry key exists", FileClause(@"C:\Program Files\Contoso\5.4.2", "tool.exe", "Existence") },
        { "registry key existence", "only checks that a file or registry key exists", new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Contoso\Tool" } },
        { "OR with an existence clause", "joins its clauses with OR", new StageDetection { Type = "Compound", Connector = "Or", Clauses = [FileClause(@"C:\Program Files\Contoso", "tool.exe", "Existence"), Registry(@"SOFTWARE\Contoso\Tool", "5.4.2")] } },
        { "value that is not a version", "not a version of up to four numbers", Registry(@"SOFTWARE\Contoso", "Pro & Team", valueName: "Edition", op: "IsEquals") },
        { "text prefix comparison", "as text", Registry(@"SOFTWARE\Contoso", "5.4", op: "BeginsWith") },
        { "version under a Windows Installer product key", "Windows Installer product key", Registry(ProductKey, "24.08.00.0") },
        { "Windows Installer product key existence", "Windows Installer product key", new StageDetection { Type = "RegistryKey", RegistryKeyRelative = ProductKey } }
    };

    [Theory]
    [MemberData(nameof(CannotShowAnOlderVersion))]
    public void RefusesADetectionThatCannotShowAnOlderInstalledVersion(string name, string message, StageDetection detection)
    {
        var manifest = Manifest(m => m.Detection = detection);
        var ex = Assert.Throws<WsusRuleException>(() => Builder.Build(manifest));
        Assert.Contains(message, ex.Message);
        var finding = Assert.Single(WsusCompatibility.Evaluate(manifest, _content), f => f.Code == WsusRefusalCodes.DetectionNotMappable);
        Assert.Equal(WsusSeverity.Blocking, finding.Severity);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void NamesTheChangeThatMakesAnExistenceOnlyDetectionPublishableNotANewInstallFallback()
    {
        var ex = Assert.Throws<WsusRuleException>(() => Rules(FileClause(@"C:\Program Files\Contoso", "tool.exe", "Existence")));
        Assert.Contains("Add a file version or a registry version comparison to the detection", ex.Message);
        Assert.DoesNotContain("Publish it as an Application", ex.Message);
    }

    [Fact]
    public void ReadsThe32BitRegistryViewWhenTheDetectionIsNotMarked64Bit()
    {
        var rules = Rules(Registry(@"SOFTWARE\Contoso\Tool", "5.4.2", is64Bit: false));
        Assert.StartsWith("<bar:RegSzToVersion Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"SOFTWARE\\Contoso\\Tool\" RegType32=\"true\" ", rules.IsInstalled);
        Assert.Equal("<lar:And><bar:RegValueExists Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"SOFTWARE\\Contoso\\Tool\" RegType32=\"true\" Value=\"DisplayVersion\" Type=\"REG_SZ\" /><bar:RegSzToVersion Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"SOFTWARE\\Contoso\\Tool\" RegType32=\"true\" Value=\"DisplayVersion\" Comparison=\"LessThan\" Data=\"5.4.2.0\" /></lar:And>", rules.IsInstallable);
    }

    [Fact]
    public void ExpandsProgramFilesTheWayA32BitProcessDoesUnlessTheClauseIsMarked64Bit()
    {
        var environment = new Dictionary<string, string>
        {
            ["ProgramFiles"] = @"C:\Program Files", ["ProgramW6432"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["SystemRoot"] = @"C:\WINDOWS", ["ProgramData"] = @"C:\ProgramData"
        };
        var builder = new WsusRuleBuilder(environment: n => environment.GetValueOrDefault(n));
        Assert.Equal("<bar:FileVersion Path=\"Contoso\\tool.exe\" Csidl=\"42\" Comparison=\"GreaterThanOrEqualTo\" Version=\"5.4.2.0\" />",
            builder.Build(Manifest(m => m.Detection = FileClause(@"%ProgramFiles%\Contoso", "tool.exe", "Version", "5.4.2", is64Bit: false))).IsInstalled);
        Assert.Equal("<bar:FileVersion Path=\"Contoso\\tool.exe\" Csidl=\"38\" Comparison=\"GreaterThanOrEqualTo\" Version=\"5.4.2.0\" />",
            builder.Build(Manifest(m => m.Detection = FileClause(@"%ProgramFiles%\Contoso", "tool.exe", "Version", "5.4.2", is64Bit: true))).IsInstalled);
    }

    [Fact]
    public void MapsAProgramFilesX86FileVersionOntoItsCsidl()
    {
        var rules = Rules(FileClause(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)")!, @"Contoso\Tool"), "tool.exe", "Version", "5.4.2.17", is64Bit: false, op: "GreaterEquals"));
        Assert.Equal("<bar:FileVersion Path=\"Contoso\\Tool\\tool.exe\" Csidl=\"42\" Comparison=\"GreaterThanOrEqualTo\" Version=\"5.4.2.17\" />", rules.IsInstalled);
        Assert.Equal("<lar:And><bar:FileExists Path=\"Contoso\\Tool\\tool.exe\" Csidl=\"42\" /><bar:FileVersion Path=\"Contoso\\Tool\\tool.exe\" Csidl=\"42\" Comparison=\"LessThan\" Version=\"5.4.2.17\" /></lar:And>", rules.IsInstallable);
    }

    [Fact]
    public void KeepsAnAbsolutePathOutsideTheKnownFolders()
    {
        Assert.Equal("<bar:FileVersion Path=\"D:\\Tools\\Contoso\\tool.exe\" Comparison=\"GreaterThanOrEqualTo\" Version=\"5.4.2.0\" />",
            Rules(FileClause(@"D:\Tools\Contoso", "tool.exe", "Version", "5.4.2", is64Bit: false)).IsInstalled);
    }

    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(false, "64.120.56788", false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(true, "64.120.56788", false, true)]
    [InlineData(true, "64.124.58447", true, false)]
    [InlineData(true, "64.128.1000", true, false)]
    public void OffersACompoundAndUpdateOnlyWhereTheKeyExistsAndTheVersionIsOlder(bool options, string? hostVersion, bool installed, bool offered)
    {
        const string optionsKey = @"SOFTWARE\WOW6432Node\Microsoft\dotnet\host\options\8.0";
        const string hostKey = @"SOFTWARE\Classes\Installer\Dependencies\Dotnet_CLI_SharedHost_8.0_x64";
        var rules = Rules(new StageDetection
        {
            Type = "Compound", Connector = "And",
            Clauses =
            [
                new StageDetection { Type = "RegistryKey", RegistryKeyRelative = optionsKey, Is64Bit = true },
                Registry(hostKey, "64.124.0", valueName: "Version", op: "GreaterEquals", propertyType: "Version")
            ]
        });
        var computer = new RuleComputer();
        if (options) computer.Key(optionsKey, "64", new() { ["OPT_NO_ANCM"] = "0" });
        if (hostVersion is not null) computer.Key(hostKey, "64", new() { ["Version"] = hostVersion });
        Assert.Equal((installed, offered), computer.Outcome(rules));
    }

    [Fact]
    public void ReadsAWsusOnlyAddRemoveProgramsEntryThroughARegistryLoopInItsView()
    {
        var rules = Builder.Build(Manifest(m => m.WsusDetection = HostingBundleArp));
        Assert.StartsWith("<bar:RegKeyLoop Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\" RegType32=\"true\" TrueIf=\"Any\">", rules.IsInstalled);
        Assert.Contains("<bar:RegSz Key=\"HKEY_LOOP_TARGET\" Subkey=\"\\\" RegType32=\"true\" Value=\"DisplayName\" Comparison=\"BeginsWith\" Data=\"Microsoft .NET 8.0.\" />", rules.IsInstalled);
        Assert.Contains("Value=\"DisplayVersion\" Comparison=\"GreaterThanOrEqualTo\" Data=\"8.0.31.0\" />", rules.IsInstalled);
        Assert.Contains("<bar:RegValueExists Key=\"HKEY_LOOP_TARGET\" Subkey=\"\\\" RegType32=\"true\" Value=\"DisplayVersion\" Type=\"REG_SZ\" />", rules.IsInstallable);
        Assert.True(WellFormed(rules.IsInstalled));
        Assert.True(WellFormed(rules.IsInstallable));
        Assert.Equal(WsusRuleSource.WsusDetection, rules.Source);
    }

    public static TheoryData<string, (string Key, string Name, string? Version, string Publisher, string View)[], bool, bool> HostingBundleEntries => new()
    {
        { "no .NET entry", [], false, false },
        { "older bundle", [("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30.26373", "Microsoft Corporation", "32")], false, true },
        { "target bundle", [("{b3c4f892-1e5d-4df6-ab9c-7bc005bdee67}", "Microsoft .NET 8.0.31 - Windows Server Hosting", "8.0.31.26421", "Microsoft Corporation", "32")], true, false },
        { "newer bundle", [("{0c1d2e3f-0000-4000-8000-000000000001}", "Microsoft .NET 8.0.32 - Windows Server Hosting", "8.0.32.26500", "Microsoft Corporation", "32")], true, false },
        { "older bundle beside a newer Desktop Runtime", [
            ("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30.26373", "Microsoft Corporation", "32"),
            ("{11111111-2222-4333-8444-555555555555}", "Microsoft Windows Desktop Runtime - 8.0.31 (x64)", "8.0.31.26421", "Microsoft Corporation", "32")], false, true },
        { "Desktop Runtime only", [("{11111111-2222-4333-8444-555555555555}", "Microsoft Windows Desktop Runtime - 8.0.30 (x64)", "8.0.30.26373", "Microsoft Corporation", "32")], false, false },
        { ".NET 10 bundle only", [("{20FB5C72-3DE9-415A-B800-494BC18BEC76}", "Microsoft .NET 10.0.11 - Windows Server Hosting", "10.0.11.26373", "Microsoft Corporation", "32")], false, false },
        { "bundle entry in the 64-bit view", [("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30.26373", "Microsoft Corporation", "64")], false, false },
        { "bundle entry without a version", [("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", null, "Microsoft Corporation", "32")], false, false },
        { "another publisher", [("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30.26373", "Contoso", "32")], false, false },
        { "stale older entry beside the current one", [
            ("{da035e95-ecb4-4cec-854c-87f6b29fde6b}", "Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30.26373", "Microsoft Corporation", "32"),
            ("{b3c4f892-1e5d-4df6-ab9c-7bc005bdee67}", "Microsoft .NET 8.0.31 - Windows Server Hosting", "8.0.31.26421", "Microsoft Corporation", "32")], true, false }
    };

    [Theory]
    [MemberData(nameof(HostingBundleEntries))]
    public void OffersTheHostingBundleUpdateOnlyOverAnOlderHostingBundleEntry(string name,
        (string Key, string Name, string? Version, string Publisher, string View)[] entries, bool installed, bool offered)
    {
        var rules = Builder.Build(Manifest(m => m.WsusDetection = HostingBundleArp));
        var computer = new RuleComputer();
        foreach (var e in entries) computer.Arp(e.Key, e.Name, e.Version, e.Publisher, e.View);
        Assert.Equal((installed, offered), computer.Outcome(rules));
        Assert.NotEmpty(name);
    }

    [Theory]
    [InlineData("32")]
    [InlineData("64")]
    public void ReadsBothRegistryViewsForViewBoth(string view)
    {
        var rules = Builder.Build(Manifest(m => m.WsusDetection = new ArpEntryDetection { View = "Both", DisplayNamePrefix = "Contoso Tool ", Version = "5.4.2" }));
        Assert.True(new RuleComputer().Arp("ContosoTool", "Contoso Tool 5.4.1", "5.4.1", "Contoso", view).Outcome(rules).Offered);
        Assert.True(new RuleComputer().Arp("ContosoTool", "Contoso Tool 5.4.2", "5.4.2", "Contoso", view).Outcome(rules).Installed);
    }

    [Fact]
    public void PublishesAManifestWhoseOwnDetectionWsusCannotMapWhenItNamesAnAddRemoveProgramsEntry()
    {
        var manifest = Manifest(m =>
        {
            m.Detection = Registry(ProductKey, "5.4.2");
            m.WsusDetection = new ArpEntryDetection { View = "64", DisplayNamePrefix = "Contoso Tool", Publisher = "Contoso", Version = "5.4.2" };
        });
        Builder.Build(manifest);
        Assert.DoesNotContain(WsusCompatibility.Evaluate(manifest, _content), f => f.Code == WsusRefusalCodes.DetectionNotMappable);
    }

    public static TheoryData<string, string, string, string, string, string, string, string, string> MsiEntries => new()
    {
        // name, ProductName, ProductVersion, Manufacturer, Platform, View, Prefix, Suffix, DisplayName
        { "version inside the name", "7-Zip 24.08 (x64 edition)", "24.08.00.0", "Igor Pavlov", "x64", "64", "7-Zip ", " (x64 edition)", "" },
        { "version at the end of the name", "Contoso Tool 5.4.2", "5.4.2.0", "Contoso", "Intel", "32", "Contoso Tool ", "", "" },
        { "no version in the name", "Google Chrome", "131.0.6778.86", "Google LLC", "x64", "64", "", "", "Google Chrome" }
    };

    [Theory]
    [MemberData(nameof(MsiEntries))]
    public void ReadsTheAddRemoveProgramsEntryThatTheStagedMsiRegisters(string name, string productName, string productVersion,
        string manufacturer, string platform, string view, string prefix, string suffix, string displayName)
    {
        var builder = new WsusRuleBuilder(new FakeMsi(productName, productVersion, manufacturer, platform));
        var manifest = Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = Registry(ProductKey, "5.4.2"); });
        var rules = builder.Build(manifest, _content);
        Assert.Equal(WsusRuleSource.MsiEntry, rules.Source);
        var entry = rules.Entry!;
        Assert.Equal(view, entry.View);
        Assert.Equal(prefix, entry.DisplayNamePrefix ?? "");
        Assert.Equal(suffix, entry.DisplayNameSuffix ?? "");
        Assert.Equal(displayName, entry.DisplayName ?? "");
        Assert.Equal(productVersion, entry.Version);
        Assert.Equal(manufacturer, entry.Publisher);
        Assert.True(entry.WindowsInstaller);
        Assert.True(WellFormed(rules.IsInstallable));
        Assert.NotEmpty(name);
    }

    [Theory]
    [InlineData("{23170F69-40C1-2702-2407-000001000000}", "7-Zip 24.07 (x64 edition)", "24.07.00.0", "Igor Pavlov", "64", 1, false, true)]
    [InlineData("{23170F69-40C1-2702-2408-000001000000}", "7-Zip 24.08 (x64 edition)", "24.08.00.0", "Igor Pavlov", "64", 1, true, false)]
    [InlineData("{23170F69-40C1-2701-2407-000001000000}", "7-Zip 24.07", "24.07.00.0", "Igor Pavlov", "32", 1, false, false)]
    [InlineData("{11111111-2222-4333-8444-555555555555}", "7-Zip Helper 1.0 (x64 edition)", "1.0", "Contoso", "64", 1, false, false)]
    [InlineData("7-Zip", "7-Zip 24.07 (x64 edition)", "24.07.00.0", "Igor Pavlov", "64", null, false, false)]
    public void OffersAnMsiProductUpdateOnlyOverAnOlderEntryOfThatProduct(string key, string name, string version, string publisher,
        string view, int? windowsInstaller, bool installed, bool offered)
    {
        var builder = new WsusRuleBuilder(new FakeMsi("7-Zip 24.08 (x64 edition)", "24.08.00.0", "Igor Pavlov", "x64"));
        var manifest = Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = Registry(ProductKey, "24.08.00.0"); });
        var computer = new RuleComputer().Arp(key, name, version, publisher, view, windowsInstaller);
        Assert.Equal((installed, offered), computer.Outcome(builder.Build(manifest, _content)));
    }

    [Fact]
    public void NeverOffersAnMsiUpdateToAnExeInstallationWhoseNameStartsTheSameWay()
    {
        var builder = new WsusRuleBuilder(new FakeMsi("KeePass 2.59", "2.59.0.0", "Dominik Reichl", "Intel"));
        var manifest = Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = Registry(ProductKey, "2.59.0.0", is64Bit: false); });
        var rules = builder.Build(manifest, _content);
        Assert.True(rules.Entry!.WindowsInstaller);
        RuleComputer Exe(RuleComputer c) => c.Arp("KeePassPasswordSafe2_is1", "KeePass Password Safe 2.58", "2.58", "Dominik Reichl", "32");
        RuleComputer Msi(RuleComputer c) => c.Arp("{E1E1A3B6-ED2C-4D53-B5A8-2F6F0B2E0AB3}", "KeePass 2.58", "2.58.0.0", "Dominik Reichl", "32", 1);
        Assert.False(Exe(new RuleComputer()).Outcome(rules).Offered);
        Assert.True(Msi(new RuleComputer()).Outcome(rules).Offered);
        Assert.True(Msi(Exe(new RuleComputer())).Outcome(rules).Offered);
    }

    [Fact]
    public void RequiresTheWindowsInstallerFlagWhenAWsusDetectionBlockAsksForIt()
    {
        var rules = Builder.Build(Manifest(m => m.WsusDetection = new ArpEntryDetection
        {
            View = "32", DisplayNamePrefix = "Contoso Tool ", Publisher = "Contoso", WindowsInstaller = true, Version = "5.4.2"
        }));
        Assert.Contains("<bar:RegDword Key=\"HKEY_LOOP_TARGET\" Subkey=\"\\\" RegType32=\"true\" Value=\"WindowsInstaller\" Comparison=\"EqualTo\" Data=\"1\" />", rules.IsInstalled);
        Assert.True(new RuleComputer().Arp("ContosoTool", "Contoso Tool 5.4.1", "5.4.1", "Contoso", "32", 1).Outcome(rules).Offered);
        Assert.False(new RuleComputer().Arp("ContosoTool", "Contoso Tool 5.4.1", "5.4.1", "Contoso", "32").Outcome(rules).Offered);
    }

    [Fact]
    public void KeepsADetectionThatWsusCanMapEvenWhenTheInstallerIsAnMsi()
    {
        var builder = new WsusRuleBuilder(new ThrowingMsi());
        var manifest = Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; });
        Assert.Equal(WsusRuleSource.Detection, builder.Build(manifest, _content).Source);
    }

    [Fact]
    public void StillRefusesADetectionWsusCannotMapWhenTheInstallerIsNotAnMsiOrNoContentFolderIsGiven()
    {
        var builder = new WsusRuleBuilder(new FakeMsi("Contoso Tool 5.4.2", "5.4.2.0", "Contoso", "x64"));
        var blocked = Registry(ProductKey, "5.4.2");
        Assert.Contains("Windows Installer product key", Assert.Throws<WsusRuleException>(() => builder.Build(Manifest(m => m.Detection = blocked), _content)).Message);
        Assert.Contains("Windows Installer product key", Assert.Throws<WsusRuleException>(() =>
            builder.Build(Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = blocked; }))).Message);
    }

    [Fact]
    public void ReportsTheMsiEntryAsAnInfoFindingInsteadOfABlockingOne()
    {
        var builder = new WsusRuleBuilder(new FakeMsi("Contoso Tool 5.4.2", "5.4.2.0", "Contoso", "x64"));
        var manifest = Manifest(m => { m.InstallerType = "MSI"; m.InstallerFile = "product.msi"; m.Detection = Registry(ProductKey, "5.4.2"); });
        var findings = WsusCompatibility.Evaluate(manifest, _content, builder);
        Assert.DoesNotContain(findings, f => f.Code == WsusRefusalCodes.DetectionNotMappable);
        var info = Assert.Single(findings, f => f.Code == WsusRefusalCodes.WsusRulesFromMsi);
        Assert.Equal(WsusSeverity.Info, info.Severity);
        Assert.Contains("'Contoso Tool *' by Contoso, 64-bit view", info.Message);
    }

    public static TheoryData<string, ArpEntryDetection, string> UnmappableWsusDetections => new()
    {
        { "unknown type", new ArpEntryDetection { Type = "File", View = "32", DisplayNamePrefix = "X", Version = "1.0" }, "type ArpEntry" },
        { "no DisplayName prefix", new ArpEntryDetection { View = "32", Version = "1.0" }, "DisplayNamePrefix" },
        { "unknown view", new ArpEntryDetection { View = "x86", DisplayNamePrefix = "X", Version = "1.0" }, "not 32, 64 or Both" },
        { "version that is not numeric", new ArpEntryDetection { View = "32", DisplayNamePrefix = "X", Version = "latest" }, "not a version" }
    };

    [Theory]
    [MemberData(nameof(UnmappableWsusDetections))]
    public void RefusesAWsusDetectionItCannotMap(string name, ArpEntryDetection detection, string message)
    {
        var manifest = Manifest(m => m.WsusDetection = detection);
        Assert.Contains(message, Assert.Throws<WsusRuleException>(() => Builder.Build(manifest)).Message);
        Assert.Single(WsusCompatibility.Evaluate(manifest, _content), f => f.Code == WsusRefusalCodes.DetectionNotMappable && f.Severity == WsusSeverity.Blocking);
        Assert.NotEmpty(name);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32", "37", "vcruntime140.dll")]
    [InlineData(@"%SystemRoot%\SysWOW64", "36", @"SysWOW64\vcruntime140.dll")]
    public void MapsTheVisualCppRuntimeFileOntoItsCsidl(string folder, string csidl, string path)
    {
        var rules = Rules(FileClause(folder, "vcruntime140.dll", "Version", "14.51.36247.0", op: "GreaterEquals"));
        Assert.Equal($"<bar:FileVersion Path=\"{path}\" Csidl=\"{csidl}\" Comparison=\"GreaterThanOrEqualTo\" Version=\"14.51.36247.0\" />", rules.IsInstalled);
        Assert.Equal($"<lar:And><bar:FileExists Path=\"{path}\" Csidl=\"{csidl}\" /><bar:FileVersion Path=\"{path}\" Csidl=\"{csidl}\" Comparison=\"LessThan\" Version=\"14.51.36247.0\" /></lar:And>", rules.IsInstallable);
    }

    public static TheoryData<string, StageDetection, string> CannotEvaluate => new()
    {
        { "per-user hive", new StageDetection { Type = "RegistryKey", Hive = "CurrentUser", RegistryKeyRelative = @"Software\Contoso" }, "HKEY_CURRENT_USER" },
        { "script", new StageDetection { Type = "Script", ScriptText = "exit 0" }, "Script detection" },
        { "per-user path", FileClause(@"%LOCALAPPDATA%\Contoso", "tool.exe", "Version", "1.0", is64Bit: false), "per-user path" },
        { "grouped compound", new StageDetection { Type = "Compound", Connector = "And", GroupSizes = [1, 1], Clauses = [] }, "Grouped compound" },
        { "less-than operator", Registry(@"SOFTWARE\C", "1.0", op: "LessThan"), "operator 'LessThan'" }
    };

    [Theory]
    [MemberData(nameof(CannotEvaluate))]
    public void RefusesDetectionsWsusCannotEvaluate(string name, StageDetection detection, string message)
    {
        Assert.Contains(message, Assert.Throws<WsusRuleException>(() => Rules(detection)).Message);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void RefusesAManifestWithoutADetection()
    {
        Assert.Contains("carries no detection", Assert.Throws<WsusRuleException>(() => Builder.DetectionRules(null)).Message);
    }

    [Fact]
    public void EscapesXmlMetacharactersInKeysValuesAndPaths()
    {
        var rules = Rules(Registry("SOFTWARE\\A&B <\"x\">", "1.0", valueName: "V'1", op: "IsEquals"));
        Assert.True(WellFormed(rules.IsInstalled));
        Assert.True(WellFormed(rules.IsInstallable));
        Assert.Contains("Subkey=\"SOFTWARE\\A&amp;B &lt;&quot;x&quot;&gt;\"", rules.IsInstalled);
        Assert.Contains("Value=\"V&apos;1\"", rules.IsInstallable);
    }
}
