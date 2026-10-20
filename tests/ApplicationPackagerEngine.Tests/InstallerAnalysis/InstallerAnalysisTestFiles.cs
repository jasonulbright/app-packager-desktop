using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>A private temp folder per test class, with builders for real installer-format files.</summary>
public sealed class InstallerAnalysisTempFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "apd-ia-" + Guid.NewGuid().ToString("N"));

    public InstallerAnalysisTempFolder() => Directory.CreateDirectory(Root);

    public string PathOf(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) Directory.Delete(dir);
            Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class InstallerAnalysisTestFiles
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string SevenZipMsi => Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules", "seven-zip.msi");

    /// <summary>Installers found on this workstation; a test that uses one also runs a fixture that is always present.</summary>
    public static class Corpus
    {
        public const string NsisMultiMode = @"C:\temp\ap\Bitwarden\2026.9.1\Bitwarden-Installer-2026.9.1.exe";
        public const string NsisMachine = @"C:\temp\ap\Rainmeter\4.5.27\Rainmeter-4.5.27.exe";
        public const string InnoModes = @"C:\temp\ap\PathCopyCopy\PathCopyCopy20.0.exe";
        public const string InnoLowest = @"C:\temp\ap\Greenshot\1.3.323\Greenshot-INSTALLER-1.3.323-RELEASE.exe";
        public const string InnoGit = @"C:\ProgramData\PatchMyPC\Git-2.56.0.2.exe";
        public const string MsiWinDirStat = @"C:\ProgramData\PatchMyPC\WinDirStat-2.9.2.msi";
        public const string MsiAdobeBase = @"C:\temp\ap\AdobeReader\26.002.21931\AcroRead.msi";
        public const string MspAdobe = @"C:\temp\ap\AdobeReader\26.002.21931\AcroRdrDCUpd2600221931.msp";
        public const string SfxAdobe = @"C:\temp\ap\AdobeReader\AcroRdrDC2600221931_en_US.exe";
        public const string Msix = @"C:\temp\ap\OhMyPosh\31.6.0\install-x64.msix";
        public const string BurnVcRedist = @"C:\ProgramData\Package Cache\{ca67548a-5ebe-413a-b50c-4b9ceb6d66c6}\vcredist_x64.exe";
        public const string SfxNdp = @"C:\ProgramData\Package Cache\99B1D7F9264E7D5AEA7B01B69EF541065030055A37CFD76F9846B3CC84FD6F2B\redist\NDP472-KB4054531-Web.exe";
        public const string Squirrel = @"C:\temp\ap\Postman\12.31.7\Postman-x64-Setup.exe";
        public const string NuGet = @"C:\Users\jason\.nuget\packages\drippyal\2.1.3\drippyal.2.1.3.nupkg";

        public static bool Has(string path) => File.Exists(path);
    }

    public static string WriteZip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            entry.Write(data);
        }
        return path;
    }

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (!Directory.Exists(link)) throw new InvalidOperationException("The junction was not created.");
    }

    /// <summary>A copy of <paramref name="source"/> cut to the given fraction of its length.</summary>
    public static string Truncated(string source, string target, double fraction)
    {
        var bytes = File.ReadAllBytes(source);
        File.WriteAllBytes(target, bytes[..Math.Max(1, (int)(bytes.Length * fraction))]);
        return target;
    }

    /// <summary>A copy of <paramref name="source"/> with bytes flipped at fixed, spread positions.</summary>
    public static string BitFlipped(string source, string target, int flips = 64, int seed = 7)
    {
        var bytes = File.ReadAllBytes(source);
        var random = new Random(seed);
        for (var i = 0; i < flips; i++)
        {
            var at = random.Next(0, bytes.Length);
            bytes[at] ^= (byte)(1 << random.Next(0, 8));
        }
        File.WriteAllBytes(target, bytes);
        return target;
    }
}

/// <summary>Builds a real Windows Installer database through msi.dll (create mode) for table-level tests.</summary>
internal sealed class TestMsiBuilder : IDisposable
{
    private readonly IntPtr _db;
    public string Path { get; }

    public TestMsiBuilder(string path)
    {
        Path = path;
        Check(MsiOpenDatabaseW(path, new IntPtr(3), out _db));
        Execute("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)");
    }

    public TestMsiBuilder Property(string name, string value)
    {
        Execute($"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{name}', '{value}')");
        return this;
    }

    public TestMsiBuilder RegistryTables()
    {
        Execute("CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)");
        Execute("CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)");
        Execute("CREATE TABLE `RemoveRegistry` (`RemoveRegistry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `RemoveRegistry`)");
        return this;
    }

    public TestMsiBuilder Component(string name, int attributes, string? condition)
    {
        var cond = condition is null ? "" : $", `Condition`";
        var condValue = condition is null ? "" : $", '{condition}'";
        Execute($"INSERT INTO `Component` (`Component`, `Directory_`, `Attributes`{cond}) VALUES ('{name}', 'TARGETDIR', {attributes}{condValue})");
        return this;
    }

    public TestMsiBuilder Registry(string id, int root, string key, string? name, string? value, string component)
    {
        var columns = "`Registry`, `Root`, `Key`, `Component_`";
        var values = $"'{id}', {root}, '{key}', '{component}'";
        if (name is not null) { columns += ", `Name`"; values += $", '{name}'"; }
        if (value is not null) { columns += ", `Value`"; values += $", '{value}'"; }
        Execute($"INSERT INTO `Registry` ({columns}) VALUES ({values})");
        return this;
    }

    public TestMsiBuilder RemoveRegistry(string id, int root, string key, string name, string component)
    {
        Execute($"INSERT INTO `RemoveRegistry` (`RemoveRegistry`, `Root`, `Key`, `Name`, `Component_`) VALUES ('{id}', {root}, '{key}', '{name}', '{component}')");
        return this;
    }

    public TestMsiBuilder Stream(string name, string sourceFile)
    {
        Check(MsiDatabaseOpenViewW(_db, "SELECT `Name`, `Data` FROM `_Streams`", out var view));
        var record = MsiCreateRecord(2);
        try
        {
            Check(MsiRecordSetStringW(record, 1, name));
            Check(MsiRecordSetStreamW(record, 2, sourceFile));
            Check(MsiViewModify(view, 1 /* MSIMODIFY_INSERT */, record));
        }
        finally
        {
            MsiCloseHandle(record);
            MsiCloseHandle(view);
        }
        return this;
    }

    public TestMsiBuilder Template(string template)
    {
        Check(MsiGetSummaryInformationW(_db, null, 20, out var summary));
        try
        {
            Check(MsiSummaryInfoSetPropertyW(summary, 7, 30, 0, IntPtr.Zero, template));
            Check(MsiSummaryInfoSetPropertyW(summary, 9, 30, 0, IntPtr.Zero, "{11111111-2222-3333-4444-555555555555}"));
            Check(MsiSummaryInfoSetPropertyW(summary, 14, 3, 200, IntPtr.Zero, null));
            Check(MsiSummaryInfoPersist(summary));
        }
        finally
        {
            MsiCloseHandle(summary);
        }
        return this;
    }

    private void Execute(string sql)
    {
        Check(MsiDatabaseOpenViewW(_db, sql, out var view));
        try { Check(MsiViewExecute(view, IntPtr.Zero)); }
        finally { MsiCloseHandle(view); }
    }

    private static void Check(uint error)
    {
        if (error != 0) throw new InvalidOperationException("msi.dll error " + error);
    }

    public void Dispose()
    {
        MsiDatabaseCommit(_db);
        MsiCloseHandle(_db);
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewModify(IntPtr view, int mode, IntPtr record);
    [DllImport("msi.dll")] private static extern IntPtr MsiCreateRecord(uint fields);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordSetStreamW(IntPtr record, uint field, string file);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformationW(IntPtr db, string? path, uint updates, out IntPtr summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiSummaryInfoSetPropertyW(IntPtr summary, uint property, uint type, int value, IntPtr time, string? text);
    [DllImport("msi.dll")] private static extern uint MsiSummaryInfoPersist(IntPtr summary);
    [DllImport("msi.dll")] private static extern uint MsiDatabaseCommit(IntPtr db);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr handle);
}
