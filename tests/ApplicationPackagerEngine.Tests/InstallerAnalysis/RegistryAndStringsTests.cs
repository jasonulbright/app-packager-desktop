using System.Text;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Registry changes from real MSI tables and compiled scripts, and the streamed string scan.</summary>
public sealed class RegistryAndStringsTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;

    public RegistryAndStringsTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox);
    }

    public void Dispose() => _temp.Dispose();

    private Task<RegistryReadResult> Registry(string path) => _service.ReadRegistryAsync(path, AnalysisLimits.Default, CancellationToken.None);

    [Fact]
    public async Task TheMsiRegistryTableGivesHiveViewComponentConditionAndDecodedValues()
    {
        var path = _temp.PathOf("reg.msi");
        using (var b = new TestMsiBuilder(path))
        {
            b.Property("ProductCode", "{EEEEEEEE-1111-2222-3333-444444444444}").RegistryTables()
             .Component("C64", 256, "VersionNT64").Component("C32", 0, null)
             .Registry("r1", 2, @"Software\Contoso", "Path", "[INSTALLDIR]", "C64")
             .Registry("r2", -1, @"Software\Contoso", "Count", "#42", "C32")
             .Registry("r3", 1, @"Software\Contoso", "Bin", "#x0A0B", "C32")
             .Registry("r4", 1, @"Software\Contoso", "Exp", "#%%TEMP%%\\x", "C32")
             .Registry("r5", 1, @"Software\Contoso", "Hash", "##literal", "C32")
             .Registry("r6", 1, @"Software\Contoso", "Multi", "a[~]b", "C32")
             .Registry("r7", 2, @"Software\Contoso\Key", "*", null, "C64")
             .RemoveRegistry("x1", 2, @"Software\Old", "-", "C64");
        }

        var result = await Registry(path);
        var rows = result.Writes;

        Assert.Equal(8, rows.Count);
        var r1 = rows.Single(r => r.Name == "Path");
        Assert.Equal(("MsiRegistryTable", "HKLM", "[INSTALLDIR]", "REG_SZ", "64", "C64", "VersionNT64"), (r1.Origin, r1.Hive, r1.Value, r1.ValueType, r1.View, r1.Section, r1.Condition));
        var r2 = rows.Single(r => r.Name == "Count");
        Assert.Equal(("HKMU", "REG_DWORD", "42", "32"), (r2.Hive, r2.ValueType, r2.Value, r2.View));
        Assert.Equal(("REG_BINARY", "0A0B"), (rows.Single(r => r.Name == "Bin").ValueType, rows.Single(r => r.Name == "Bin").Value));
        Assert.Equal("REG_EXPAND_SZ", rows.Single(r => r.Name == "Exp").ValueType);
        Assert.Equal("#literal", rows.Single(r => r.Name == "Hash").Value);
        Assert.Equal("REG_MULTI_SZ", rows.Single(r => r.Name == "Multi").ValueType);
        Assert.Contains(rows, r => r.Key == @"Software\Contoso\Key" && r.Action == "create key; delete on uninstall" && r.Name == "");
        Assert.Contains(rows, r => r.Key == @"Software\Old" && r.Action == "delete key");
        Assert.Contains("HKMU is HKLM for a per-machine install and HKCU for a per-user install.", result.Notes);
    }

    [Fact]
    public async Task TheNsisScriptGivesWritesAndDeletesWithTheViewAtEachInstruction()
    {
        var rows = (await Registry(Fixture("nsis-reg-extras.bin"))).Writes;

        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Equal("NsisScript", r.Origin));
        var dword = rows.Single(r => r.Name == "Installed");
        Assert.Equal(("HKLM", @"Software\TestCo\TestApp", "1", "REG_DWORD", "64", "Main"), (dword.Hive, dword.Key, dword.Value, dword.ValueType, dword.View, dword.Section));
        Assert.Contains(rows, r => r.Action == "delete value" && r.Name == "DisplayName");
        Assert.Contains(rows, r => r.Action == "delete key" && r.Condition == "/ifempty");
    }

    [Fact]
    public async Task AShellContextRootIsNamedWithItsRule()
    {
        var result = await Registry(Fixture("nsis-u-multimode.bin"));

        Assert.All(result.Writes, r => Assert.Equal("SHCTX", r.Hive));
        Assert.Contains("SHCTX is HKLM after SetShellVarContext all and HKCU otherwise.", result.Notes);
    }

    [Fact]
    public async Task InnoSetupNotesExplainMissingRows()
    {
        var old = await Registry(Fixture("inno-a-557.bin"));
        var encrypted = await Registry(Fixture("inno-enc-650.bin"));
        var other = await Registry(SevenZipMsi);
        var zip = await Registry(WriteZip(_temp.PathOf("a.zip"), ("a.txt", Text("a"))));

        Assert.Equal(["Registry entries are read for setup data 6.0.0 and later; this installer is 5.5.7."], old.Notes);
        Assert.Contains(encrypted.Notes, n => n.Contains("encrypted"));
        Assert.Empty(other.Writes);
        Assert.Equal(["Registry settings are read from MSI, NSIS and Inno Setup installers."], zip.Notes);
    }

    [Theory]
    [InlineData(true, 0, "", true, "x86", "64")]
    [InlineData(true, 0, "", false, "x86", "32")]
    [InlineData(true, 1, "", true, "x86", "32")]
    [InlineData(true, 2, "", false, "x86", "64")]
    [InlineData(true, 3, "", false, "x86", "64")]
    [InlineData(true, 4, "", true, "x86", "32")]
    [InlineData(true, 4, "", false, "x64", "64")]
    [InlineData(false, 0, "", true, "x86", "64")]
    [InlineData(false, 0, "", false, "x86", "32")]
    [InlineData(false, 0, "32bit", true, "x86", "32")]
    [InlineData(false, 0, "64bit", false, "x86", "64")]
    public void TheInnoRegistryViewFollowsTheEntryBitnessOrTheInstallMode(bool v700, int bitness, string flag, bool is64BitMode, string setupArchitecture, string view)
    {
        var flags = flag.Length == 0 ? new List<string>() : [flag];

        Assert.Equal(view, ApplicationPackagerEngine.Services.InstallerScripts.InnoSetupReader.RegistryView(v700, bitness, flags, is64BitMode, setupArchitecture));
    }

    [Fact]
    public async Task InnoRegistryRowsHaveAViewAndShowTheirFlagsInTheCondition()
    {
        if (Corpus.Has(Corpus.InnoGit))
        {
            var rows = (await Registry(Corpus.InnoGit)).Writes;
            Assert.Equal(46, rows.Count);
            Assert.All(rows, r => Assert.Equal("64", r.View));
            Assert.All(rows, r => Assert.DoesNotContain("unins", r.Section));
            Assert.Equal([("", 14), ("assoc", 18), ("assoc_sh", 10), ("consolefont", 4)],
                rows.GroupBy(r => r.Section).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key, StringComparer.Ordinal));
            Assert.DoesNotContain(rows, r => r.Condition.Contains("Components:", StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Key == @"Console\Git Bash" && r.Condition == "Flags: createvalueifdoesntexist uninsdeletevalue uninsdeletekeyifempty");
        }
        if (Corpus.Has(Corpus.InnoCrystalDiskInfo))
        {
            var rows = (await Registry(Corpus.InnoCrystalDiskInfo)).Writes;
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(("64", ""), (r.View, r.Section)));
            Assert.Contains(rows, r => r.Key == @"Software\Crystal Dew World\CrystalDiskInfo" && r.Condition == "Flags: uninsdeletekey dontcreatekey");
            Assert.Contains(rows, r => r.Key == @"Software\Crystal Dew World" && r.Condition == "Flags: uninsdeletekeyifempty dontcreatekey");
        }
        if (Corpus.Has(Corpus.InnoModes))
        {
            var rows = (await Registry(Corpus.InnoModes)).Writes;
            Assert.Equal(3, rows.Count(r => r.View == "32" && r.Condition.StartsWith("Check: IsAdminInstallMode", StringComparison.Ordinal)));
            Assert.All(rows, r => Assert.Contains(r.View, new[] { "32", "64" }));
        }
    }

    [Fact]
    public async Task WorkstationInstallersGiveTheirKnownRegistryRows()
    {
        if (Corpus.Has(Corpus.InnoGit))
        {
            var rows = (await Registry(Corpus.InnoGit)).Writes;
            Assert.Equal(46, rows.Count);
            Assert.Contains(rows, r => r.Origin == "InnoSetup" && r.Hive == "HKLM" && r.Key == @"Software\GitForWindows"
                && r.Name == "CurrentVersion" && r.Value == "2.56.0.2" && r.Condition == "Check: IsAdmin; Flags: uninsdeletevalue uninsdeletekeyifempty");
        }
        if (Corpus.Has(Corpus.InnoModes))
        {
            Assert.Equal(20, (await Registry(Corpus.InnoModes)).Writes.Count);
        }
        if (Corpus.Has(Corpus.NsisMultiMode))
        {
            Assert.True(ArchiveExtractionService.FindSevenZip() is not null, "7-Zip is not installed on this machine; the uninstaller rows of an NSIS installer are read with 7-Zip 25.01 or later.");
            var rows = (await Registry(Corpus.NsisMultiMode)).Writes;
            Assert.Equal(23, rows.Count);
            Assert.Equal(6, rows.Count(r => r.Section.StartsWith("Uninstaller", StringComparison.Ordinal)));
            Assert.Empty(Directory.EnumerateDirectories(_sandbox.Root));
        }
        if (Corpus.Has(Corpus.MsiWinDirStat))
        {
            Assert.Equal(9, (await Registry(Corpus.MsiWinDirStat)).Writes.Count);
        }
        Assert.Equal(3, (await Registry(Fixture("nsis-mk-lzma-ansi.bin"))).Writes.Count);
    }

    // ---- strings ----

    private string StringsFile(Action<BinaryWriter> write)
    {
        var path = _temp.PathOf("strings-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        using var w = new BinaryWriter(File.Create(path));
        write(w);
        return path;
    }

    private static void Pad(BinaryWriter w, long to)
    {
        while (w.BaseStream.Position < to) w.Write((byte)0);
    }

    [Fact]
    public async Task AsciiAndUtf16StringsComeWithOffsetsAndCategories()
    {
        var path = StringsFile(w =>
        {
            Pad(w, 100); w.Write(Encoding.ASCII.GetBytes("https://contoso.example/download"));
            Pad(w, 400); w.Write(Encoding.Unicode.GetBytes(@"HKLM\Software\Contoso\Tool"));
            Pad(w, 800); w.Write(Encoding.ASCII.GetBytes("{12345678-ABCD-EF01-2345-6789ABCDEF01}"));
            Pad(w, 900); w.Write(Encoding.ASCII.GetBytes("msiexec.exe /i tool.msi /qn"));
            Pad(w, 1000); w.Write(Encoding.ASCII.GetBytes("12.4.0.1"));
            Pad(w, 1100); w.Write(Encoding.Unicode.GetBytes(@"C:\Program Files\Contoso\tool.exe"));
            Pad(w, 1300); w.Write(Encoding.ASCII.GetBytes("abc"));
            Pad(w, 1400);
        });

        var strings = await _service.ReadStringsAsync(path, AnalysisLimits.Default, CancellationToken.None);

        Assert.Contains(new InterestingString("https://contoso.example/download", "Ascii", 100, "Url"), strings);
        Assert.Contains(new InterestingString(@"HKLM\Software\Contoso\Tool", "Utf16", 400, "Registry"), strings);
        Assert.Contains(new InterestingString("{12345678-ABCD-EF01-2345-6789ABCDEF01}", "Ascii", 800, "Guid"), strings);
        Assert.Contains(new InterestingString("msiexec.exe /i tool.msi /qn", "Ascii", 900, "Command"), strings);
        Assert.Contains(new InterestingString("12.4.0.1", "Ascii", 1000, "Version"), strings);
        Assert.Contains(new InterestingString(@"C:\Program Files\Contoso\tool.exe", "Utf16", 1100, "Path"), strings);
        Assert.DoesNotContain(strings, s => s.Text == "abc");
    }

    [Fact]
    public async Task AStringAcrossTheChunkBoundaryIsFoundWhole()
    {
        const long chunk = 1024 * 1024;
        var path = StringsFile(w =>
        {
            Pad(w, chunk - 10);
            w.Write(Encoding.ASCII.GetBytes("https://boundary.example/path"));
            Pad(w, chunk + 100);
        });

        var strings = await _service.ReadStringsAsync(path, AnalysisLimits.Default, CancellationToken.None);

        Assert.Contains(new InterestingString("https://boundary.example/path", "Ascii", chunk - 10, "Url"), strings);
    }

    [Fact]
    public async Task TheScanStopsAtMaxStringsAndRepeatsAreReportedOnce()
    {
        var path = StringsFile(w =>
        {
            for (var i = 0; i < 50; i++)
            {
                w.Write(Encoding.ASCII.GetBytes($"https://host{i}.example/a"));
                w.Write((byte)0);
                w.Write(Encoding.ASCII.GetBytes("https://same.example/b"));
                w.Write((byte)0);
            }
        });

        var capped = await _service.ReadStringsAsync(path, AnalysisLimits.Default with { MaxStrings = 10 }, CancellationToken.None);
        var all = await _service.ReadStringsAsync(path, AnalysisLimits.Default, CancellationToken.None);

        Assert.Equal(10, capped.Count);
        Assert.Equal(51, all.Count);
    }

    [Fact]
    public async Task ACancelledScanEndsAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadStringsAsync(SevenZipMsi, AnalysisLimits.Default, cts.Token));
    }
}
