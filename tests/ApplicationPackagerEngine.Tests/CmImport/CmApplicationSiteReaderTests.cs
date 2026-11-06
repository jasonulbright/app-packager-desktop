using System.Text.Json;
using ApplicationPackagerEngine.Services.CmImport;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.CmImport;

/// <summary>The site reader sends read-only scripts through the session and builds nothing from their text.</summary>
public sealed class CmApplicationSiteReaderTests
{
    private sealed class ScriptedSession(Func<ConfigMgrScript, ConfigMgrParameters?, ConfigMgrCommandResult> answer) : IConfigMgrSession
    {
        public List<(string Name, string Text, Dictionary<string, object?> Parameters)> Calls { get; } = [];

        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Calls.Add((script.Name, script.Text, parameters?.ToDictionary(p => p.Key, p => p.Value) ?? []));
            return Task.FromResult(answer(script, parameters));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ConfigMgrCommandResult Strings(params string[] values) =>
        new(values.Select(v => JsonSerializer.SerializeToElement(v)).ToList(), []);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CmImport", name + ".app.xml"));

    [Fact]
    public async Task ReadsTheStoredDocumentWithTheApplicationNameAsAParameter()
    {
        var xml = Fixture("clauses");
        var session = new ScriptedSession((_, _) => Strings(xml));
        var read = await new CmApplicationSiteReader(session).ReadDigestAsync("IMPTEST Clauses");
        Assert.Equal(xml, read);
        var call = Assert.Single(session.Calls);
        Assert.Equal("IMPTEST Clauses", call.Parameters["ApplicationName"]);
        Assert.DoesNotContain("IMPTEST", call.Text);
        Assert.Equal("IMPTEST Clauses", CmApplicationDigestReader.Read(read).Name);
    }

    [Fact]
    public void TheScriptsOnlyReadFromTheSite()
    {
        foreach (var text in new[] { CmApplicationSiteReader.DigestScript, CmApplicationSiteReader.NamesScript })
        {
            Assert.DoesNotMatch(@"(?i)\b(Set|New|Remove|Start|Stop|Invoke|Import|Add|Update)-CM\w*", text);
            Assert.Contains("Get-CM", text);
        }
        Assert.Contains("-DisableWildcardHandling", CmApplicationSiteReader.DigestScript);
    }

    [Fact]
    public async Task AnAnswerWithoutADocumentIsAnError()
    {
        var session = new ScriptedSession((_, _) => Strings("something else"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CmApplicationSiteReader(session).ReadDigestAsync("X"));
    }

    [Fact]
    public async Task AnEmptyNameIsRefusedBeforeTheSiteIsAsked()
    {
        var session = new ScriptedSession((_, _) => Strings());
        await Assert.ThrowsAsync<ArgumentException>(() => new CmApplicationSiteReader(session).ReadDigestAsync(" "));
        Assert.Empty(session.Calls);
    }

    [Fact]
    public async Task ASiteFailureReachesTheCallerWithItsMessage()
    {
        var session = new ScriptedSession((_, _) => throw new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "ConfigMgr application 'X' was not found."));
        var ex = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => new CmApplicationSiteReader(session).ReadDigestAsync("X"));
        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public async Task ResolvesTheNamesOfDependencyTargetsAndKeepsTheLogicalNameWhenOneFails()
    {
        var plan = CmImportPlanner.Plan(CmApplicationDigestReader.Read(Fixture("dependent")));
        var relationship = Assert.Single(plan.Relationships);
        var session = new ScriptedSession((_, p) => new ConfigMgrCommandResult(
            [JsonSerializer.SerializeToElement(new { ApplicationName = "Contoso Arch", DeploymentTypeName = "Arch - x64" })], []));
        await new CmApplicationSiteReader(session).ResolveRelationshipNamesAsync(plan);
        Assert.Equal(("Contoso Arch", "Arch - x64"), (relationship.TargetApplicationName, relationship.TargetDeploymentTypeName));
        Assert.Equal(relationship.TargetApplication, session.Calls[0].Parameters["TargetApplication"]);

        var failing = CmImportPlanner.Plan(CmApplicationDigestReader.Read(Fixture("dependent")));
        var warnings = new List<string>();
        var broken = new ScriptedSession((_, _) => throw new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "no access"));
        await new CmApplicationSiteReader(broken).ResolveRelationshipNamesAsync(failing, warnings.Add);
        Assert.Equal("", failing.Relationships[0].TargetApplicationName);
        Assert.Contains("no access", Assert.Single(warnings));
    }
}
