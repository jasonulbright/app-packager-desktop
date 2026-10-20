using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

public sealed class AnalysisSandboxTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ASessionIsANewFolderUnderTheRootAndReleaseRemovesItWithReadOnlyFiles()
    {
        var sandbox = new AnalysisSandbox(_temp.PathOf("Temp"));

        var session = sandbox.NewSession();
        var file = Path.Combine(session, "x", "inner.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.Equal(_temp.PathOf("Temp"), Path.GetDirectoryName(session));
        Assert.NotEqual(session, sandbox.NewSession());
        sandbox.Release(session);
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void ReleaseRemovesAJunctionInsideTheSessionWithoutTouchingItsTarget()
    {
        var outside = _temp.PathOf("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var sandbox = new AnalysisSandbox(_temp.PathOf("Temp"));
        var session = sandbox.NewSession();
        Junction(Path.Combine(session, "link"), outside);

        sandbox.Release(session);

        Assert.False(Directory.Exists(session));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void ReleaseRefusesAFolderThatIsNotASession()
    {
        var sandbox = new AnalysisSandbox(_temp.PathOf("Temp"));
        var other = _temp.PathOf("other");
        Directory.CreateDirectory(other);

        Assert.Throws<InstallerAnalysisException>(() => sandbox.Release(other));
        Assert.Throws<InstallerAnalysisException>(() => sandbox.Release(Path.Combine(sandbox.NewSession(), "..", "..", "other")));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void ReleaseAllRemovesEverySessionOfThisSandbox()
    {
        var sandbox = new AnalysisSandbox(_temp.PathOf("Temp"));
        var a = sandbox.NewSession();
        var b = sandbox.NewSession();

        sandbox.ReleaseAll();

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
    }

    [Fact]
    public void StartupCleanupRemovesOnlyOldSessions()
    {
        var root = _temp.PathOf("Temp");
        var clock = new StepClock(DateTimeOffset.UtcNow);
        var earlier = new AnalysisSandbox(root, clock);
        var old = earlier.NewSession();
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        var recent = earlier.NewSession();
        var unrelated = Path.Combine(root, "keep-me");
        Directory.CreateDirectory(unrelated);

        new AnalysisSandbox(root, clock).CleanOldSessions(TimeSpan.FromHours(1));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void ALinkedSandboxRootIsRefused()
    {
        var real = _temp.PathOf("real");
        Directory.CreateDirectory(real);
        var link = _temp.PathOf("Temp");
        Junction(link, real);

        var ex = Assert.Throws<InstallerAnalysisException>(() => new AnalysisSandbox(link).NewSession());

        Assert.Equal("The analysis temp folder is a junction or symbolic link; it was not used.", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }

    [Theory]
    [InlineData("a/b/c.msi", @"a\b\c.msi", "")]
    [InlineData(@"a\.\b.msi", @"a\b.msi", "")]
    [InlineData("<inner.msi>", null, "The entry name contains a character Windows does not allow.")]
    [InlineData(@"..\x.msi", null, "The entry name leaves the extraction folder.")]
    [InlineData(@"a\..\..\x.msi", null, "The entry name leaves the extraction folder.")]
    [InlineData(@"C:\x.msi", null, "The entry name is an absolute path.")]
    [InlineData(@"\x.msi", null, "The entry name is an absolute path.")]
    [InlineData(@"\\server\share\x.msi", null, "The entry name is an absolute path.")]
    [InlineData("NUL", null, "The entry name is a device name.")]
    [InlineData("com1.txt", null, "The entry name is a device name.")]
    [InlineData(@"dir\LPT9.log", null, "The entry name is a device name.")]
    [InlineData("name.", null, "An entry name segment ends with a dot or a space.")]
    [InlineData(@"dir \x", null, "An entry name segment ends with a dot or a space.")]
    [InlineData("file.txt:stream", null, "The entry name contains a character Windows does not allow.")]
    [InlineData("", null, "The entry has no name.")]
    [InlineData(@".\.", null, "The entry has no name.")]
    public void EntryNamesAreNormalizedOrRefused(string name, string? expected, string reason)
    {
        Assert.Equal(expected, SandboxPath.Normalize(name, out var why));
        Assert.Equal(reason, why);
    }

    [Fact]
    public void OverlongNamesAreRefused()
    {
        Assert.Null(SandboxPath.Normalize(new string('a', 201), out var segment));
        Assert.Equal("An entry name segment is too long.", segment);
        Assert.Null(SandboxPath.Normalize(string.Join('\\', Enumerable.Repeat(new string('b', 100), 5)), out var total));
        Assert.Equal("The entry name is too long.", total);
    }

    [Fact]
    public void ResolveRefusesAPathThroughALinkedFolder()
    {
        var folder = _temp.PathOf("x");
        Directory.CreateDirectory(folder);
        var outside = _temp.PathOf("y");
        Directory.CreateDirectory(outside);
        Junction(Path.Combine(folder, "sub"), outside);

        Assert.Null(SandboxPath.Resolve(folder, @"sub\a.txt", out var reason));
        Assert.Equal("A folder on the entry path is a link or a file.", reason);
        Assert.Equal(Path.Combine(folder, "ok", "a.txt"), SandboxPath.Resolve(folder, "ok/a.txt", out _));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class InstallerAnalysisRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-iaservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public InstallerAnalysisRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneOfEachInstallerAnalysisServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        var sandbox = services.GetRequiredService<IAnalysisSandbox>();
        Assert.IsType<AnalysisSandbox>(sandbox);
        Assert.Same(sandbox, services.GetRequiredService<IAnalysisSandbox>());
        Assert.Equal(Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Temp"), sandbox.Root);
        Assert.IsType<InstallerAnalysisService>(services.GetRequiredService<IInstallerAnalysisService>());
        Assert.Same(services.GetRequiredService<IInstallerAnalysisService>(), services.GetRequiredService<IInstallerAnalysisService>());
        var writer = services.GetRequiredService<IInstallerReportWriter>();
        Assert.Equal(Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Reports"), writer.Folder);

        var session = sandbox.NewSession();
        var extracted = await services.GetRequiredService<IInstallerAnalysisService>().ExtractEntryAsync(
            WriteZip(Path.Combine(_root, "p.zip"), ("a.msi", Text("a"))), new PayloadEntry("a.msi", 1, false, "InnerInstaller", "MSI"),
            session, AnalysisLimits.Default, CancellationToken.None);
        Assert.StartsWith(sandbox.Root, extracted);
        sandbox.ReleaseAll();
        Assert.False(Directory.Exists(session));
    }
}
