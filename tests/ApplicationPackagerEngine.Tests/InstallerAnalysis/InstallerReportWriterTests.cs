using System.Text.Json;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

public sealed class InstallerReportWriterTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    private InstallerReportWriter Writer(string? folder = null) => new(folder ?? _temp.PathOf("reports"), () => Now);

    private static AnalysisReport Report(params AnalysisFact[] extraMetadata) => new(
        @"C:\in\tool.exe", "tool.exe", 2048, "AB12", "NSIS", "x86",
        new SignatureFact("Valid", "CN=Contoso", "CN=CA", "THUMB", true),
        [
            new("DisplayName", "=cmd|' /C calc'!A0", "Confirmed", "nsis-compiled-script"),
            new("DisplayVersion", "1.2.3", "Confirmed", "nsis-compiled-script"),
            new("Publisher", "<script>alert(1)</script>", "Confirmed", "nsis-compiled-script"),
            new("ProductCode", "", "None", "none"),
            new("InstallCommand", "\"tool.exe\" /S", "Confirmed", "nsis-compiled-script"),
            new("UninstallCommand", "\"%ProgramFiles%\\Tool\\uninst.exe\" /S", "Confirmed", "nsis-compiled-script"),
            new("UninstallRegistryKey", @"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool", "Confirmed", "nsis-compiled-script")
        ],
        [new InstallMode("PerMachine", "", @"%ProgramFiles%\Tool", "\"%ProgramFiles%\\Tool\\uninst.exe\" /S", @"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool", "HKLM", "PerMachine") { IsDefault = true, View = "32" }],
        [new MetadataBlock("Intune package (Detection.xml)", [new("SetupFile", "setup.exe", "Confirmed", "x"), .. extraMetadata])],
        [new("ProductCode", "{X}", "Confirmed", "msi-property-table"), new("+SUM(A1)", "@x", "Confirmed", "msi-property-table")],
        new EffectiveDetection("{PC}", "Tool", "2.0", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{PC}", "From the patch.") { BaseVersion = "1.0" },
        ["The installer type was not recognized."])
    {
        DetectionHint = "Registry key HKLM:\\...\\Tool, value DisplayVersion.",
        TypeName = "NSIS",
        RequestedExecutionLevel = "asInvoker"
    };

    private static readonly RegistryWrite[] Registry =
    [
        new("NsisScript", "HKLM", @"Software\Tool", "=HYPERLINK(\"x\")", "-2+3", "REG_SZ", "") { View = "32", Section = "Main" }
    ];

    [Fact]
    public void TheSummaryNamesTheSourceTheDeploymentFieldsTheModesAndThePostPatchTarget()
    {
        var text = Writer().Summary(Report());

        Assert.StartsWith("Installer Analysis: tool.exe", text);
        Assert.Contains("SHA-256:      AB12", text);
        Assert.Contains("Signed:       Valid (CN=Contoso), timestamped", text);
        Assert.Contains("  DisplayVersion:         1.2.3", text);
        Assert.Contains("  PerMachine (default):", text);
        Assert.Contains("Effective post-patch detection target:", text);
        Assert.Contains("  DisplayVersion:         2.0", text);
        Assert.Contains("Notes:", text);
    }

    [Fact]
    public void TheJsonDigestKeepsTheSourceFieldNames()
    {
        using var doc = JsonDocument.Parse(Writer().Json(Report(), includeKeyMaterial: false));
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("SchemaVersion").GetString());
        Assert.Equal("tool.exe", root.GetProperty("Source").GetProperty("FileName").GetString());
        Assert.Equal(2048, root.GetProperty("Source").GetProperty("FileSize").GetInt64());
        Assert.Equal("AB12", root.GetProperty("Source").GetProperty("SHA256").GetString());
        Assert.Equal("1.2.3", root.GetProperty("Application").GetProperty("DisplayVersion").GetString());
        Assert.Equal("NSIS", root.GetProperty("Application").GetProperty("InstallerType").GetString());
        Assert.Equal("", root.GetProperty("Application").GetProperty("ProductCode").GetString());
        Assert.Equal("\"tool.exe\" /S", root.GetProperty("Deployment").GetProperty("InstallCommand").GetString());
        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool", root.GetProperty("Detection").GetProperty("UninstallRegistryKey").GetString());
        Assert.Equal("{X}", root.GetProperty("Raw").GetProperty("MsiProperties").GetProperty("ProductCode").GetString());
        Assert.Equal("setup.exe", root.GetProperty("Raw").GetProperty("PackageMetadata").GetProperty("Intune package (Detection.xml)").GetProperty("SetupFile").GetString());
        Assert.Equal("PerMachine", root.GetProperty("InstallModes")[0].GetProperty("Name").GetString());
        Assert.Equal("2.0", root.GetProperty("PostPatch").GetProperty("DisplayVersion").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyMaterialNeverReachesAnyExport(bool include)
    {
        var report = Report(new("EncryptionKey", "S2V5", "Confirmed", "x"), new("MacKey", "TWFj", "Confirmed", "x"),
            new("InitializationVector", "SVY=", "Confirmed", "x"), new("Mac", "TUFD", "Confirmed", "x"));
        var writer = Writer();

        var outputs = new[]
        {
            writer.Summary(report), writer.Json(report, include),
            File.ReadAllText(writer.WriteCsv(report, Registry)), File.ReadAllText(writer.WriteHtml(report, [], Registry))
        };

        foreach (var output in outputs)
            foreach (var secret in new[] { "S2V5", "TWFj", "SVY=", "TUFD" })
                Assert.DoesNotContain(secret, output);
    }

    [Fact]
    public void TheCsvGuardsFormulasQuotesEveryCellAndIsWrittenAsANewFile()
    {
        var writer = Writer();
        var first = writer.WriteCsv(Report(), Registry);
        var second = writer.WriteCsv(Report(), Registry);
        var text = File.ReadAllText(first);

        Assert.NotEqual(first, second);
        Assert.Equal(_temp.PathOf("reports"), Path.GetDirectoryName(first));
        Assert.Equal("installer-analysis-20261019-093000.csv", Path.GetFileName(first));
        Assert.Contains("\"'=cmd|' /C calc'!A0\"", text);
        Assert.Contains("\"'+SUM(A1)\",\"'@x\"", text);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\",\"'-2+3\"", text);
        Assert.DoesNotContain(",=", text);
        Assert.Equal(0xEF, File.ReadAllBytes(first)[0]);
    }

    [Fact]
    public void TheHtmlEncodesEveryValueAndForbidsScripts()
    {
        var payload = new[] { new PayloadEntry("<b>inner</b>.msi", 10, false, "InnerInstaller", "MSI") };
        var html = File.ReadAllText(Writer().WriteHtml(Report(), payload, Registry));

        Assert.Contains("Content-Security-Policy", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;inner&lt;/b&gt;.msi", html);
        Assert.Contains("=HYPERLINK(&quot;x&quot;)", html);
    }

    [Fact]
    public void AnHtmlReportWithoutAPayloadListSaysWhy()
    {
        IInstallerReportWriter writer = Writer();
        var html = File.ReadAllText(writer.WriteHtml(Report(), null, Registry, "7-Zip was not available"));
        var listed = File.ReadAllText(writer.WriteHtml(Report(), [new PayloadEntry("a.msi", 1, false, "InnerInstaller", "MSI")], Registry, ""));

        Assert.Contains("<h2>Payload</h2>\r\n<p>The payload was not listed: 7-Zip was not available.</p>", html);
        Assert.Contains("Registry settings", html);
        Assert.DoesNotContain("was not listed", listed);
        Assert.Contains("a.msi", listed);
    }

    [Fact]
    public void ALinkedReportsFolderIsRefused()
    {
        var real = _temp.PathOf("real");
        Directory.CreateDirectory(real);
        var link = _temp.PathOf("linked");
        Junction(link, real);

        var ex = Assert.Throws<InstallerAnalysisException>(() => Writer(link).WriteCsv(Report(), Registry));

        Assert.Equal("The reports folder is a junction or symbolic link; nothing was written.", ex.Message);
        Assert.Empty(Directory.GetFiles(real));
    }

    [Fact]
    public void TheDefaultFolderIsUnderTheDataRoot()
    {
        Assert.Equal(Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Reports"), new InstallerReportWriter().Folder);
    }
}
