using System.Diagnostics;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>
/// Runs the suite's installer analysis module read-only in a child Windows PowerShell on the
/// same files and compares the digest fields and the registry rows. Every accepted difference
/// is listed with its reason in <see cref="Accepted"/>. When the module is not on this machine
/// the fixed expected values of the repository fixtures still run.
/// </summary>
public sealed class SuiteModuleParityTests : IDisposable
{
    private const string ModulePath = @"C:\projects\installer-analysis\Module\InstallerAnalysisCommon.psd1";

    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly InstallerAnalysisService _service;

    public SuiteModuleParityTests() => _service = new InstallerAnalysisService(new AnalysisSandbox(_temp.PathOf("sandbox")), () => null);

    public void Dispose() => _temp.Dispose();

    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.Ordinal)
    {
        ["MSI"] = "NativeMSI", ["7zSFX"] = "SevenZipSfx", ["Msix"] = "MSIX", ["WinRarSFX"] = "WinRarSfx"
    };

    /// <summary>Differences that are right on the desktop side, keyed by file name and field.</summary>
    private static readonly Dictionary<string, string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        // Burn's 32-bit engine registers a per-machine bundle in the 32-bit view (seen on this workstation for both bundles).
        ["vcredist_x64.exe|ArpKey"] = "Burn registers under WOW6432Node",
        // The module reads ProductCodeOrEquivalent into ProductCode; Squirrel has no product code.
        ["Postman-x64-Setup.exe|ProductCode"] = "Squirrel has no ProductCode",
        // An NSIS module digest adds the uninstaller's rows only when 7-Zip runs; this run passes none.
    };

    private static string[] Files()
    {
        var files = new List<string>
        {
            SevenZipMsi, Fixture("nsis-u-multimode.bin"), Fixture("nsis-reg-extras.bin"), Fixture("nsis-mk-lzma-solid-unicode.bin"),
            Fixture("nsis-u-pf-hklm-wow.bin"), Fixture("inno-admin-modes-x64.bin"), Fixture("inno-lowest-670.bin")
        };
        foreach (var path in new[] { Corpus.NsisMultiMode, Corpus.NsisMachine, Corpus.InnoModes, Corpus.InnoLowest, Corpus.InnoGit,
                                     Corpus.MsiWinDirStat, Corpus.MsiAdobeBase, Corpus.BurnVcRedist, Corpus.Squirrel, Corpus.Msix, Corpus.NuGet })
            if (Corpus.Has(path)) files.Add(path);
        return [.. files];
    }

    private const string Harness = """
        param([string]$ModulePath, [string]$List, [string]$Out)
        $ErrorActionPreference = 'Stop'
        Import-Module $ModulePath -Force -DisableNameChecking -WarningAction SilentlyContinue
        $all = foreach ($Path in [string[]](Get-Content -LiteralPath $List)) {
            $fi = Get-InstallerFileInfo -Path $Path
            $type = Get-InstallerType -Path $Path
            $msi = $null; $sum = $null
            if ($type -eq 'MSI') { $msi = Get-MsiProperties -MsiPath $Path; $sum = Get-MsiSummaryInfo -MsiPath $Path }
            $pkg = Get-PackageMetadataFor -Path $Path -InstallerType $type
            $sw = Get-SilentSwitches -InstallerType $type -FilePath $Path -MsiProperties $msi -PackageMetadata $pkg
            $df = Get-DeploymentFields -FileInfo $fi -MsiProperties $msi -Switches $sw -PackageMetadata $pkg -InstallerType $type -MsiSummary $sum
            $json = ConvertTo-DeploymentJson -FileInfo $fi -InstallerType $type -Switches $sw -MsiProperties $msi -DeploymentFields $df -PackageMetadata $pkg | ConvertFrom-Json
            $reg = Get-InstallerRegistrySettings -Path $Path -InstallerType $type -PackageMetadata $pkg
            [pscustomobject]@{
                Path = $Path; Type = $type; DisplayName = $df.DisplayName; DisplayVersion = $df.DisplayVersion; Vendor = $df.Vendor
                ProductCode = $json.Application.ProductCode; ArpKey = $df.UninstallRegistryKey; Uninstall = $json.Deployment.UninstallCommand
                Registry = @($reg.Entries | ForEach-Object { '{0}|{1}|{2}|{3}|{4}|{5}|{6}' -f $_.Root, $_.Key, $_.Name, $_.Type, $_.Value, $_.Action, ([string]$_.View).Split(' ')[0] })
            }
        }
        ConvertTo-Json -InputObject @($all) -Depth 5 | Set-Content -LiteralPath $Out -Encoding UTF8
        """;

    private List<JsonElement> RunModule(string[] files)
    {
        var script = _temp.PathOf("harness.ps1");
        var list = _temp.PathOf("list.txt");
        var output = _temp.PathOf("out.json");
        File.WriteAllText(script, Harness);
        File.WriteAllLines(list, files);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ModulePath", ModulePath, "-List", list, "-Out", output })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { p.Kill(true); throw new TimeoutException("The module run did not finish."); }
        Assert.True(File.Exists(output), "The module run wrote no output: " + stderr.Result);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Normalize(string key) => key.Replace(@"\Software\", @"\SOFTWARE\", StringComparison.OrdinalIgnoreCase);

    private static string RegistryLine(RegistryWrite w) =>
        string.Join("|", w.Hive, w.Key, w.Name, w.ValueType, w.Value, w.Action, w.View);

    [Fact]
    public async Task TheDesktopAnalysisMatchesTheSuiteModuleOrTheDifferenceIsListed()
    {
        var files = Files();
        var differences = new List<string>();
        var compared = 0;

        if (File.Exists(ModulePath))
        {
            var module = RunModule(files);
            Assert.Equal(files.Length, module.Count);
            foreach (var source in module)
            {
                var path = S(source, "Path");
                var name = Path.GetFileName(path);
                var report = await _service.AnalyzeAsync(path, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);
                var registry = await _service.ReadRegistryAsync(path, AnalysisLimits.Default, CancellationToken.None);
                string D(string field) => report.Deployment.First(f => f.Name == field).Value;

                void Compare(string field, string expected, string actual, StringComparison comparison = StringComparison.Ordinal)
                {
                    if (string.Equals(expected.Trim(), actual.Trim(), comparison) || Accepted.ContainsKey(name + "|" + field)) return;
                    differences.Add($"{name} {field}: module '{expected}' desktop '{actual}'");
                }

                var type = S(source, "Type");
                if (type != "Unknown") Compare("Type", TypeNames.GetValueOrDefault(type, type), report.Type);
                Compare("DisplayName", S(source, "DisplayName"), D("DisplayName"));
                Compare("DisplayVersion", S(source, "DisplayVersion"), D("DisplayVersion"));
                Compare("Publisher", S(source, "Vendor"), D("Publisher"));
                if (report.Type is not ("MSIX" or "MsixBundle")) Compare("ArpKey", Normalize(S(source, "ArpKey")), Normalize(D("UninstallRegistryKey")), StringComparison.OrdinalIgnoreCase);
                if (report.Type is "NativeMSI" or "NSIS" or "InnoSetup")
                {
                    Compare("UninstallCommand", S(source, "Uninstall"), D("UninstallCommand"), StringComparison.OrdinalIgnoreCase);
                    var expectedRows = source.GetProperty("Registry").EnumerateArray().Select(r => r.GetString() ?? "").ToList();
                    var actualRows = registry.Writes.Where(w => !w.Section.StartsWith("Uninstaller", StringComparison.Ordinal)).Select(RegistryLine).ToList();
                    Compare("RegistryRows", string.Join("\n", expectedRows), string.Join("\n", actualRows));
                }
                compared++;
            }
            Assert.True(differences.Count == 0, string.Join("\n", differences));
        }

        var fixture = await _service.AnalyzeAsync(Fixture("nsis-u-multimode.bin"), AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", fixture.Deployment.First(f => f.Name == "UninstallRegistryKey").Value);
        var msi = await _service.AnalyzeAsync(SevenZipMsi, AnalysisLimits.Default, new Progress<string>(), CancellationToken.None);
        Assert.Equal("msiexec.exe /x \"{23170F69-40C1-2702-2408-000001000000}\" /qn /norestart", msi.Deployment.First(f => f.Name == "UninstallCommand").Value);
        compared++;
        Assert.True(compared >= 1);
    }
}
