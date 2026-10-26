using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>
/// Truncated and bit-flipped copies of one real file of each format: every operation ends
/// within the time limit with a result or a plain <see cref="InstallerAnalysisException"/>.
/// </summary>
public sealed class CorruptInputTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;
    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(30) };

    public CorruptInputTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private string Msix()
    {
        var manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"A.B\" Publisher=\"CN=A\" Version=\"1.0.0.0\" ProcessorArchitecture=\"x64\" /></Package>";
        return WriteZip(_temp.PathOf("good.msix"), ("AppxManifest.xml", Text(manifest)), ("app.exe", new byte[4096]));
    }

    public static TheoryData<string> Sources() =>
    [
        "msi", "nsis-mk-lzma-solid-unicode.bin", "nsis-mk-zlib-solid.bin", "nsis-u-multimode.bin", "inno-admin-670.bin", "inno-lowest-modes.bin", "msix"
    ];

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task DamagedCopiesEndWithAResultOrAPlainMessage(string source)
    {
        var original = source switch { "msi" => SevenZipMsi, "msix" => Msix(), _ => Fixture(source) };
        var extension = Path.GetExtension(original);
        var copies = new List<string>();
        foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
            copies.Add(Truncated(original, _temp.PathOf($"t{fraction}-{Path.GetFileNameWithoutExtension(original)}{extension}"), fraction));
        foreach (var seed in new[] { 1, 2, 3 })
            copies.Add(BitFlipped(original, _temp.PathOf($"f{seed}-{Path.GetFileNameWithoutExtension(original)}{extension}"), 32, seed));

        foreach (var copy in copies)
        {
            var clock = Stopwatch.StartNew();
            await Plain(() => _service.AnalyzeAsync(copy, Limits, new Progress<string>(), CancellationToken.None));
            await Plain(() => _service.ListPayloadAsync(copy, Limits, CancellationToken.None));
            await Plain(() => _service.ReadRegistryAsync(copy, Limits, CancellationToken.None));
            await Plain(() => _service.ReadStringsAsync(copy, Limits, CancellationToken.None));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), copy + " took " + clock.Elapsed);
        }
        Assert.True(!Directory.Exists(_sandbox.Root) || !Directory.EnumerateDirectories(_sandbox.Root).Any());
    }

    [Fact]
    public async Task DamagedCopiesOfWorkstationInstallersAreHandled()
    {
        foreach (var original in new[] { Corpus.NsisMachine, Corpus.InnoModes, Corpus.MsiWinDirStat, Corpus.Msix, Corpus.BurnVcRedist, Corpus.SfxNdp }.Where(Corpus.Has))
        {
            var name = Path.GetFileName(original);
            foreach (var copy in new[] { Truncated(original, _temp.PathOf("t-" + name), 0.5), BitFlipped(original, _temp.PathOf("f-" + name), 64, 9) })
            {
                var clock = Stopwatch.StartNew();
                await Plain(() => _service.AnalyzeAsync(copy, Limits, new Progress<string>(), CancellationToken.None));
                await Plain(() => _service.ListPayloadAsync(copy, Limits, CancellationToken.None));
                await Plain(() => _service.ReadRegistryAsync(copy, Limits, CancellationToken.None));
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), copy + " took " + clock.Elapsed);
            }
        }
    }

    private static async Task Plain<T>(Func<Task<T>> run)
    {
        try { await run(); }
        catch (InstallerAnalysisException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.DoesNotContain("Exception", ex.Message);
        }
    }

    [Fact]
    public async Task AFileOfRandomBytesWithAnInstallerExtensionIsHandled()
    {
        var bytes = new byte[256 * 1024];
        new Random(5).NextBytes(bytes);
        foreach (var name in new[] { "random.msi", "random.msp", "random.exe", "random.msix", "random.nupkg", "random.intunewin" })
        {
            var path = _temp.PathOf(name);
            File.WriteAllBytes(path, bytes);
            await Plain(() => _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None));
            await Plain(() => _service.ListPayloadAsync(path, Limits, CancellationToken.None));
            await Plain(() => _service.ReadRegistryAsync(path, Limits, CancellationToken.None));
        }
    }
}
