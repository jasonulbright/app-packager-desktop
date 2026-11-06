using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.CmImport;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.CmImport;

/// <summary>The runner checks the request first, reads the site read-only, and writes only when asked.</summary>
public sealed class CmImportRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-cmrun-" + Guid.NewGuid().ToString("N"));

    public CmImportRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FixtureSession(string xml) : IConfigMgrSession
    {
        public int Calls { get; private set; }

        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Calls++;
            var answer = script.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase) ? xml : "";
            return Task.FromResult(new ConfigMgrCommandResult([JsonSerializer.SerializeToElement(answer)], []));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CmImport", name + ".app.xml"));

    private static CmImportRunner Runner(FixtureSession session) =>
        new(new CmApplicationSiteReader(session), null!, _ => throw new InvalidOperationException("The tool must not be needed."));

    private CmImportRequest Request(Action<CmImportRequestBuilder>? tweak = null)
    {
        var b = new CmImportRequestBuilder { OutputRoot = _dir };
        tweak?.Invoke(b);
        return b.Build();
    }

    private sealed class CmImportRequestBuilder
    {
        public string Name { get; set; } = "IMPTEST Clauses";
        public string OutputRoot { get; set; } = "";
        public bool SkipContent { get; set; } = true;
        public bool Package { get; set; }
        public IntunePublishOptions? Publish { get; set; }

        public CmImportRequest Build() => new(Name, OutputRoot, new CmImportOptions(), SkipContent, Package, Publish);
    }

    [Fact]
    public async Task PreviewReadsTheSiteAndWritesNothing()
    {
        var session = new FixtureSession(Fixture("clauses"));
        var outcome = await Runner(session).PreviewAsync(Request(r => r.OutputRoot = ""));
        Assert.True(session.Calls >= 1);
        Assert.Null(outcome.Stage);
        Assert.NotEmpty(outcome.Plan.DeploymentTypes);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Report));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public async Task ConvertStagesTheAppWithoutContentWhenAsked()
    {
        var outcome = await Runner(new FixtureSession(Fixture("clauses"))).ConvertAsync(Request());
        Assert.NotNull(outcome.Stage);
        Assert.NotEmpty(outcome.Stage!.Staged);
        Assert.All(outcome.Stage.Staged, s => Assert.True(File.Exists(s.ManifestPath)));
        Assert.True(File.Exists(Path.Combine(outcome.Stage.ApplicationFolder, "conversion-report.txt")));
        Assert.Empty(outcome.Packages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankNameIsRefusedBeforeTheSiteIsRead(string name)
    {
        var session = new FixtureSession(Fixture("clauses"));
        await Assert.ThrowsAsync<ArgumentException>(() => Runner(session).PreviewAsync(Request(r => r.Name = name)));
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task ABlankOutputFolderIsRefusedBeforeTheSiteIsRead()
    {
        var session = new FixtureSession(Fixture("clauses"));
        await Assert.ThrowsAsync<ArgumentException>(() => Runner(session).ConvertAsync(Request(r => r.OutputRoot = " ")));
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task PublishingWithoutPackagingIsRefusedBeforeAnythingIsCopied()
    {
        var session = new FixtureSession(Fixture("clauses"));
        var publish = new IntunePublishOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner(session).ConvertAsync(Request(r => { r.Package = false; r.Publish = publish; })));
        Assert.Contains(".intunewin", ex.Message);
        Assert.Equal(0, session.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Theory]
    [InlineData("", "c", "s")]
    [InlineData("t", "", "s")]
    [InlineData("t", "c", "")]
    public async Task MissingTenantValuesAreRefusedBeforeAnythingIsCopied(string tenant, string client, string secret)
    {
        var session = new FixtureSession(Fixture("clauses"));
        var publish = new IntunePublishOptions { TenantId = tenant, ClientId = client, ClientSecret = secret };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner(session).ConvertAsync(Request(r => { r.SkipContent = false; r.Package = true; r.Publish = publish; })));
        Assert.Equal(0, session.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public async Task PackagingWithoutContentIsRefused()
    {
        var session = new FixtureSession(Fixture("clauses"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Runner(session).ConvertAsync(Request(r => { r.SkipContent = true; r.Package = true; })));
        Assert.Equal(0, session.Calls);
    }
}
