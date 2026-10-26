using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The Burn uninstall key comes from the manifest scope and the engine architecture.</summary>
public sealed class BurnScopeTests : IDisposable
{
    private const string BundleId = "{12345678-ABCD-4EF0-9876-0123456789AB}";
    private const string VcRedistX64Modern = @"C:\ProgramData\Package Cache\{0e3bb569-69d6-4c34-bff9-c2f81db5e5f0}\VC_redist.x64.exe";

    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;

    public BurnScopeTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox, () => null);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>A cabinet made by makecab.exe that holds the manifest as file "0".</summary>
    private byte[] Cabinet(string manifest)
    {
        var folder = _temp.PathOf("cab-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0"), manifest);
        var cab = Path.Combine(folder, "ux.cab");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "makecab.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = folder
        };
        psi.ArgumentList.Add("/D");
        psi.ArgumentList.Add("CompressionType=MSZIP");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("ux.cab");
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
        }
        Assert.True(File.Exists(cab), "makecab.exe did not create the cabinet.");
        return File.ReadAllBytes(cab);
    }

    /// <summary>
    /// A minimal PE image with one .wixburn section whose BURN_SECTION header points at the
    /// attached UX cabinet, as a Burn engine stub carries it.
    /// </summary>
    private string Bundle(string name, ushort machine, string? perMachine)
    {
        var registration = perMachine is null ? "" : $" PerMachine=\"{perMachine}\"";
        var manifest = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><BurnManifest xmlns=\"http://wixtoolset.org/schemas/v4/2008/Burn\"><Registration Id=\"{BundleId}\" ExecutableName=\"setup.exe\"{registration} Version=\"2.3.4.0\" /></BurnManifest>";
        var cab = Cabinet(manifest);
        const int sectionRaw = 0x400;
        const int stub = 0x600;
        var bytes = new byte[stub + cab.Length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        var pe = 0x80;
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, pe);
        BitConverter.GetBytes(machine).CopyTo(bytes, pe + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, pe + 6);
        var is64 = machine != 0x014C;
        ushort optionalSize = is64 ? (ushort)0xF0 : (ushort)0xE0;
        BitConverter.GetBytes(optionalSize).CopyTo(bytes, pe + 20);
        BitConverter.GetBytes((ushort)0x0102).CopyTo(bytes, pe + 22);
        BitConverter.GetBytes(is64 ? (ushort)0x20B : (ushort)0x10B).CopyTo(bytes, pe + 24);
        var section = pe + 24 + optionalSize;
        ".wixburn"u8.ToArray().CopyTo(bytes, section);
        BitConverter.GetBytes(0x200u).CopyTo(bytes, section + 8);
        BitConverter.GetBytes(0x1000u).CopyTo(bytes, section + 12);
        BitConverter.GetBytes(0x200u).CopyTo(bytes, section + 16);
        BitConverter.GetBytes((uint)sectionRaw).CopyTo(bytes, section + 20);
        BitConverter.GetBytes(WixBurnBundleReader.SectionMagic).CopyTo(bytes, sectionRaw);
        BitConverter.GetBytes(2u).CopyTo(bytes, sectionRaw + 4);
        Guid.Parse(BundleId).ToByteArray().CopyTo(bytes, sectionRaw + 8);
        BitConverter.GetBytes((uint)stub).CopyTo(bytes, sectionRaw + 24);
        BitConverter.GetBytes(1u).CopyTo(bytes, sectionRaw + 40);
        BitConverter.GetBytes(1u).CopyTo(bytes, sectionRaw + 44);
        BitConverter.GetBytes((uint)cab.Length).CopyTo(bytes, sectionRaw + 48);
        cab.CopyTo(bytes, stub);
        var path = _temp.PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static AnalysisFact Key(AnalysisReport report) => report.Deployment.Single(f => f.Name == "UninstallRegistryKey");

    [Theory]
    [InlineData((ushort)0x014C, "yes", @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "PerMachine")]
    [InlineData((ushort)0x8664, "yes", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "PerMachine")]
    [InlineData((ushort)0xAA64, "yes", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "PerMachine")]
    [InlineData((ushort)0x014C, "no", @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "PerUser")]
    [InlineData((ushort)0x8664, "no", @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BundleId, "PerUser")]
    public async Task TheKeyFollowsTheManifestScopeAndTheEngineArchitecture(ushort machine, string perMachine, string expected, string context)
    {
        var path = Bundle($"b-{machine:X}-{perMachine}.exe", machine, perMachine);

        var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("WixBurn", report.Type);
        Assert.Equal(expected, Key(report).Value);
        Assert.Equal("Confirmed", Key(report).Confidence);
        Assert.Equal(context, report.Deployment.Single(f => f.Name == "InstallContext").Value);
        Assert.Contains(report.Metadata.Single(b => b.Title == "WiX Burn bundle").Facts, f => f.Name == "Bundle version" && f.Value == "2.3.4.0");
    }

    [Fact]
    public async Task AManifestWithoutAScopeGivesAPredictedKeyAndANote()
    {
        var path = Bundle("b-noscope.exe", 0x014C, null);

        var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("Predicted", Key(report).Confidence);
        Assert.Contains(report.Notes, n => n.StartsWith("The bundle scope was not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreadableUxContainerGivesAPredictedKey()
    {
        var path = Bundle("b-broken.exe", 0x014C, "yes");
        var bytes = File.ReadAllBytes(path);
        for (var i = 0x600; i < bytes.Length; i++) bytes[i] = 0;
        File.WriteAllBytes(path, bytes);

        var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("Predicted", Key(report).Confidence);
        Assert.Contains(report.Notes, n => n.StartsWith("The bundle scope was not read", StringComparison.Ordinal));
        Assert.True(!Directory.Exists(_sandbox.Root) || !Directory.EnumerateFileSystemEntries(_sandbox.Root).Any());
    }

    [Fact]
    public async Task AvailableWorkstationBundlesArePerMachineWithAnX86Engine()
    {
        foreach (var path in new[] { Corpus.BurnVcRedist, VcRedistX64Modern }.Where(File.Exists))
        {
            var manifest = WixBurnBundleReader.ReadManifest(path, Directory.CreateDirectory(_temp.PathOf("m-" + Guid.NewGuid().ToString("N")[..6])).FullName, out var reason);
            Assert.True(manifest is not null, reason);
            Assert.True(manifest!.PerMachine);

            var report = await _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

            Assert.StartsWith(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{", Key(report).Value);
            Assert.Equal("Confirmed", Key(report).Confidence);
        }
    }
}
