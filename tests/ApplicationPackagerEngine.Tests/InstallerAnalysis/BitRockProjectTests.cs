using System.IO.Compression;
using System.Text;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>
/// The BitRock InstallBuilder project read from the runtime archive (a Metakit file whose
/// project.xml is zlib-compressed) of a setup: built files with the real layout, and the
/// PostgreSQL 13 setup of the lab install when it is on this workstation.
/// </summary>
public sealed class BitRockProjectTests : IDisposable
{
    private const string PostgreSql13 = @"C:\temp\ap\PostgreSQL13\13.23\postgresql-13.23-1-windows-x64.exe";

    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string PostgreSqlShape = """
        <project>
            <shortName>postgresql</shortName>
            <fullName>PostgreSQL</fullName>
            <version>13</version>
            <preInstallationActionList>
                <linuxServiceAction abortOnError="0" action="start" name="${servicename}">
                    <action::abortOnError>1</action::abortOnError>
                </linuxServiceAction>
                <setInstallerVariable>
                    <name>uninstallerName</name>
                    <persist>1</persist>
                    <value>uninstall-postgresql</value>
                </setInstallerVariable>
                <setInstallerVariable>
                    <name>default_installdir</name>
                    <value>/Library/PostgreSQL/13</value>
                    <ruleList>
                        <compareText><logic>equals</logic><text>${platform_name}</text><value>osx</value></compareText>
                    </ruleList>
                </setInstallerVariable>
                <setInstallerVariable>
                    <name>default_installdir</name>
                    <value>${platform_install_prefix}\PostgreSQL\13</value>
                    <ruleList>
                        <compareText><logic>equals</logic><text>${platform_name}</text><value>windows</value></compareText>
                    </ruleList>
                </setInstallerVariable>
            </preInstallationActionList>
            <parameterList>
                <directoryParameter>
                    <name>installdir</name>
                    <value>${default_installdir}</value>
                    <default>${default_installdir}</default>
                </directoryParameter>
            </parameterList>
            <postUninstallerCreationActionList>
                <registrySet>
                    <key>HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\${project.windowsARPRegistryPrefix}</key>
                    <name>DisplayVersion</name>
                    <type>REG_SZ</type>
                    <value>13.23-1</value>
                    <ruleList>
                        <compareText logic="equals" text="${platform_name}" value="windows"/>
                    </ruleList>
                </registrySet>
            </postUninstallerCreationActionList>
            <readyToInstallActionList>
                <actionGroup>
                    <ruleList>
                        <compareText><logic>equals</logic><text>${installer_ui}</text><value>unattended</value></compareText>
                    </ruleList>
                </actionGroup>
            </readyToInstallActionList>
            <unattendedModeUI>minimal</unattendedModeUI>
            <productDisplayName>${product_fullname} 13</productDisplayName>
            <requireInstallationByRootUser>${admin_rights}</requireInstallationByRootUser>
            <vendor>PostgreSQL Global Development Group</vendor>
            <windows64bitMode>1</windows64bitMode>
        </project>
        """;

    /// <summary>A 32-bit PE stub, the payload, then the Metakit runtime archive with the zlib project.xml among other files.</summary>
    private string WriteSetup(string name, string projectXml)
    {
        static byte[] Zlib(string text)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Encoding.UTF8.GetBytes(text));
            return ms.ToArray();
        }

        var stub = new byte[0x200];
        stub[0] = (byte)'M'; stub[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(stub, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(stub, 0x80);
        BitConverter.GetBytes((ushort)0x14C).CopyTo(stub, 0x84);
        Encoding.ASCII.GetBytes("BitRock InstallBuilder").CopyTo(stub, 0x100);

        var runtime = new MemoryStream();
        runtime.Write("JL\u001a\0"u8);
        runtime.Write(new byte[4]);
        runtime.Write("<root>\0boot.tcl\0main.tcl\0project.xml\0license.xml\0"u8);
        runtime.Write(Zlib("proc main {} { return }"));
        runtime.Write(Zlib(projectXml));
        runtime.Write(Zlib("<licenseInfo/>"));
        var mk = runtime.ToArray();
        var length = BitConverter.GetBytes(mk.Length);
        Array.Reverse(length);
        length.CopyTo(mk, 4);

        var path = _temp.PathOf(name);
        using var file = File.Create(path);
        file.Write(stub);
        file.Write(new byte[64 * 1024]);
        file.Write(mk);
        file.Write("bitrock-lzma-4.0"u8);
        return path;
    }

    [Fact]
    public void TheProjectFieldsAreReadFromTheRuntimeArchive()
    {
        var path = WriteSetup("pg.exe", PostgreSqlShape);

        var p = BitRockProjectReader.Read(path, CancellationToken.None);

        Assert.NotNull(p);
        Assert.Equal("postgresql", p.ShortName);
        Assert.Equal("PostgreSQL", p.FullName);
        Assert.Equal("13", p.Version);
        Assert.Equal("PostgreSQL 13", p.DisplayName);
        Assert.Equal("PostgreSQL Global Development Group", p.Vendor);
        Assert.Equal("13.23-1", p.DisplayVersion);
        Assert.True(p.DisplayVersionFromRegistryAction);
        Assert.Equal("PostgreSQL 13", p.ArpKeyName);
        Assert.False(p.ArpKeyNameInProject);
        Assert.True(p.Windows64BitMode);
        Assert.Equal("uninstall-postgresql", p.UninstallerName);
        Assert.Equal("action", p.UninstallerNameSource);
        Assert.Equal(@"%ProgramW6432%\PostgreSQL\13", p.InstallDir);
    }

    [Fact]
    public void AnArpPrefixAndAnUninstallerNameInTheProjectAreReadAsGiven()
    {
        var path = WriteSetup("app.exe", """
            <project>
                <shortName>app</shortName><fullName>Sample App</fullName><version>2.1</version>
                <windowsARPRegistryPrefix>SampleApp-${product_version}</windowsARPRegistryPrefix>
                <uninstallerName>remove-app</uninstallerName>
                <parameterList>
                    <directoryParameter name="installdir" default="${platform_install_prefix}/${product_shortname}-${product_version}"/>
                </parameterList>
            </project>
            """);

        var p = BitRockProjectReader.Read(path, CancellationToken.None)!;

        Assert.Equal("SampleApp-2.1", p.ArpKeyName);
        Assert.True(p.ArpKeyNameInProject);
        Assert.Null(p.Windows64BitMode);
        Assert.Equal("remove-app", p.UninstallerName);
        Assert.Equal("project", p.UninstallerNameSource);
        Assert.Equal("2.1", p.DisplayVersion);
        Assert.False(p.DisplayVersionFromRegistryAction);
        Assert.Equal("", p.DisplayName);
        Assert.Equal(@"%ProgramFiles(x86)%\app-2.1", p.InstallDir);
    }

    [Fact]
    public async Task ADisplayVersionActionWithAnExtraRuleIsPredicted()
    {
        var path = WriteSetup("pg17.exe", PostgreSqlShape.Replace(
            """<compareText logic="equals" text="${platform_name}" value="windows"/>""",
            """<compareText logic="equals" text="${platform_name}" value="windows"/><isFalse value="${extract_mode}"/>"""));
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var p = BitRockProjectReader.Read(path, CancellationToken.None)!;
        var r = await service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        Assert.Equal("13.23-1", p.DisplayVersion);
        Assert.True(p.DisplayVersionConditional);
        var version = r.Deployment.Single(f => f.Name == "DisplayVersion");
        Assert.Equal("13.23-1", version.Value);
        Assert.Equal("Predicted", version.Confidence);
    }

    [Fact]
    public void ASetupWithoutARuntimeArchiveGivesNoProject()
    {
        var path = _temp.PathOf("plain.exe");
        File.WriteAllBytes(path, new byte[128 * 1024]);

        Assert.Null(BitRockProjectReader.Read(path, CancellationToken.None));
    }

    [Fact]
    public void AProjectWithADocumentTypeIsRefused()
    {
        var path = WriteSetup("dtd.exe", """<?xml version="1.0"?><!DOCTYPE project [<!ENTITY x "y">]><project><fullName>&x;</fullName></project>""");

        Assert.Null(BitRockProjectReader.Read(path, CancellationToken.None));
    }

    [Fact]
    public async Task TheAnalysisProposesTheProjectKeyViewUninstallerAndVersion()
    {
        var path = WriteSetup("postgresql-13.23-1-windows-x64.exe", PostgreSqlShape);
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var r = await service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        AnalysisFact F(string name) => r.Deployment.Single(f => f.Name == name);
        Assert.Equal("BitRock", r.Type);
        Assert.Equal("x64", r.Architecture);
        Assert.Equal(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13", F("UninstallRegistryKey").Value);
        Assert.Equal("Predicted", F("UninstallRegistryKey").Confidence);
        Assert.Equal("13.23-1", F("DisplayVersion").Value);
        Assert.Equal("Confirmed", F("DisplayVersion").Confidence);
        Assert.Equal("PostgreSQL 13", F("DisplayName").Value);
        Assert.Equal(@"""%ProgramW6432%\PostgreSQL\13\uninstall-postgresql.exe"" --mode unattended --unattendedmodeui none", F("UninstallCommand").Value);
        Assert.Equal("Predicted", F("UninstallCommand").Confidence);
        Assert.Contains(r.Notes, n => n.Contains("windowsARPRegistryPrefix", StringComparison.Ordinal));
        Assert.Contains(r.Metadata, b => b.Title == "BitRock InstallBuilder project" && b.Facts.Any(f => f.Name == "windows64bitMode" && f.Value == "1"));
    }

    [Fact]
    public void TheProjectsUnattendedModeRulesAndUiAreRead()
    {
        var handled = BitRockProjectReader.Read(WriteSetup("pg.exe", PostgreSqlShape), CancellationToken.None)!;
        var plain = BitRockProjectReader.Read(WriteSetup("plain.exe", "<project><shortName>a</shortName><fullName>A</fullName><version>1</version></project>"), CancellationToken.None)!;

        Assert.True(handled.UnattendedModeHandled);
        Assert.Equal("minimal", handled.UnattendedModeUI);
        Assert.False(plain.UnattendedModeHandled);
        Assert.Equal("", plain.UnattendedModeUI);
    }

    [Fact]
    public async Task TheInstallModesTableHasTheProjectsUnattendedMode()
    {
        var path = WriteSetup("postgresql-13.23-1-windows-x64.exe", PostgreSqlShape);
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var r = await service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        var mode = Assert.Single(r.Modes);
        Assert.Equal("PerMachine", mode.Name);
        Assert.True(mode.IsDefault);
        Assert.Equal("--mode unattended --unattendedmodeui none", mode.Switch);
        Assert.Equal(@"%ProgramW6432%\PostgreSQL\13", mode.InstallFolder);
        Assert.Equal(@"""postgresql-13.23-1-windows-x64.exe"" --mode unattended --unattendedmodeui none", mode.InstallCommand);
        Assert.Equal(@"""%ProgramW6432%\PostgreSQL\13\uninstall-postgresql.exe"" --mode unattended --unattendedmodeui none", mode.UninstallCommand);
        Assert.Equal(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13", mode.ArpKey);
        Assert.Equal(("HKLM", "64", "PerMachine"), (mode.Hive, mode.View, mode.Context));
        var unattended = r.Metadata.Single(b => b.Title == "BitRock InstallBuilder project").Facts.Single(f => f.Name == "Unattended mode");
        Assert.Equal("Confirmed", unattended.Confidence);
    }

    [Fact]
    public async Task AProjectWithoutUnattendedRulesGivesAPredictedMode()
    {
        var path = WriteSetup("app.exe", """
            <project>
                <shortName>app</shortName><fullName>Sample App</fullName><version>2.1</version>
                <windows64bitMode>1</windows64bitMode>
                <parameterList>
                    <directoryParameter name="installdir" default="${platform_install_prefix}/${product_shortname}-${product_version}"/>
                </parameterList>
            </project>
            """);
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var r = await service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        Assert.Equal("--mode unattended --unattendedmodeui none", Assert.Single(r.Modes).Switch);
        var unattended = r.Metadata.Single(b => b.Title == "BitRock InstallBuilder project").Facts.Single(f => f.Name == "Unattended mode");
        Assert.Equal("Predicted", unattended.Confidence);
    }

    [Fact]
    public async Task TheNotesNameTheProjectsUninstallerAndNotTheGenericOne()
    {
        var path = WriteSetup("postgresql-13.23-1-windows-x64.exe", PostgreSqlShape);
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var r = await service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        var notes = r.Deployment.Single(f => f.Name == "Notes").Value;
        Assert.Contains("uninstall-postgresql.exe", notes);
        Assert.DoesNotContain(@"\uninstall.exe", notes);
        Assert.DoesNotContain("%INSTALLDIR%", notes);
    }

    [Fact]
    public async Task ThePostgreSql13SetupMatchesTheLabInstall()
    {
        if (!File.Exists(PostgreSql13)) return;
        var service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

        var r = await service.AnalyzeAsync(PostgreSql13, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);

        string V(string name) => r.Deployment.Single(f => f.Name == name).Value;
        Assert.Equal(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13", V("UninstallRegistryKey"));
        Assert.Equal("13.23-1", V("DisplayVersion"));
        Assert.Equal(@"""%ProgramW6432%\PostgreSQL\13\uninstall-postgresql.exe"" --mode unattended --unattendedmodeui none", V("UninstallCommand"));
        Assert.Contains("uninstall-postgresql.exe", V("Notes"));
        Assert.DoesNotContain(@"\uninstall.exe", V("Notes"));
        var mode = Assert.Single(r.Modes);
        Assert.Equal(@"%ProgramW6432%\PostgreSQL\13", mode.InstallFolder);
        Assert.Equal("--mode unattended --unattendedmodeui none", mode.Switch);
        Assert.Equal("64", mode.View);
        Assert.Equal("Confirmed", r.Metadata.Single(b => b.Title == "BitRock InstallBuilder project").Facts.Single(f => f.Name == "Unattended mode").Confidence);
    }
}
