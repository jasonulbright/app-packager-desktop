using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class PlanDigestTests
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlanDigest");

    /// <summary>Digests Get-StageManifestPlanDigest produced under Windows PowerShell 5.1 for each fixture.</summary>
    private static Dictionary<string, string> PowerShellDigests() =>
        File.ReadAllLines(Path.Combine(FixtureFolder, "powershell-digests.txt"))
            .Where(l => l.Length > 65)
            .ToDictionary(l => l[65..], l => l[..64], StringComparer.OrdinalIgnoreCase);

    private static JsonObject Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureFolder, name)))!.AsObject();

    public static TheoryData<string> AllFixtures => new()
    {
        "plain-msi.json", "multi-deployment-type.json", "script-detection-unicode.json", "canonicalization-edge-cases.json"
    };

    public static TheoryData<string> ManifestFixtures => new()
    {
        "plain-msi.json", "multi-deployment-type.json", "script-detection-unicode.json"
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Compute_EqualsTheDigestPowerShellProducedForTheSameContent(string fixture)
    {
        Assert.Equal(PowerShellDigests()[fixture], StageManifestPlanDigest.Compute(Fixture(fixture)));
    }

    [Theory]
    [MemberData(nameof(ManifestFixtures))]
    public void Write_StoredTheDigestPowerShellComputesForTheWrittenFile(string fixture)
    {
        Assert.Equal(PowerShellDigests()[fixture], Fixture(fixture)["PlanDigest"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(ManifestFixtures))]
    public void Compute_GivesTheSameDigestForTheTypedManifestReadFromTheFile(string fixture)
    {
        var manifest = new ManifestService().Read(Path.Combine(FixtureFolder, fixture));
        Assert.Equal(PowerShellDigests()[fixture], StageManifestPlanDigest.Compute(manifest));
    }

    [Fact]
    public void Compute_ExcludesFileHashesPlanDigestAndStagedAt()
    {
        var manifest = Fixture("plain-msi.json");
        var expected = StageManifestPlanDigest.Compute(manifest);
        manifest["FileHashes"] = new JsonArray();
        manifest["PlanDigest"] = "changed";
        manifest["StagedAt"] = "2001-01-01T00:00:00Z";
        Assert.Equal(expected, StageManifestPlanDigest.Compute(manifest));
    }

    [Fact]
    public void Compute_IgnoresKeyOrder()
    {
        var manifest = Fixture("multi-deployment-type.json");
        var reversed = new JsonObject();
        foreach (var (name, value) in manifest.Reverse().ToList()) reversed[name] = value?.DeepClone();
        Assert.Equal(StageManifestPlanDigest.Compute(manifest), StageManifestPlanDigest.Compute(reversed));
    }

    [Theory]
    [InlineData("CommandOverrides.Install", "\"install.bat /other\"")]
    [InlineData("Detection.Clauses.0.ExpectedValue", "\"3.1.1\"")]
    [InlineData("Timing.EstimatedMinutes", "13")]
    [InlineData("ScriptSigning", "{ \"Detection\": { \"Status\": \"Signed\" } }")]
    [InlineData("DeploymentTypes.1.Requirements.2.Value", "false")]
    public void Compute_ChangesWhenAResolvedPlanFieldChanges(string path, string json)
    {
        var manifest = Fixture("multi-deployment-type.json");
        var before = StageManifestPlanDigest.Compute(manifest);
        SetPath(manifest, path, JsonNode.Parse(json));
        Assert.NotEqual(before, StageManifestPlanDigest.Compute(manifest));
    }

    [Fact]
    public void Compute_IsUppercaseHexOfSha256()
    {
        Assert.Matches("^[0-9A-F]{64}$", StageManifestPlanDigest.Compute(new StageManifest()));
    }

    [Fact]
    public void Compute_HashesAnEmptyTopLevelArrayAsNullAndANestedOneAsAnEmptyObject()
    {
        var text = StageManifestPlanDigest.CanonicalText(WorkbenchTestRoot.Json("""{ "A": [], "B": { "C": [] } }"""));
        Assert.Equal("{\r\n    \"A\":  null,\r\n    \"B\":  {\r\n              \"C\":  {\r\n\r\n                    }\r\n          }\r\n}", text);
    }

    private static void SetPath(JsonNode root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
            current = int.TryParse(segments[i], out var index) ? current[index]! : current[segments[i]]!;
        if (int.TryParse(segments[^1], out var last)) current[last] = value;
        else current[segments[^1]] = value;
    }
}

public sealed class ManifestSchema4Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packr-v4-" + Guid.NewGuid().ToString("N"));
    private readonly ManifestService _service = new();

    public ManifestSchema4Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string ManifestPath => Path.Combine(_root, "stage-manifest.json");

    private static readonly string[] V3Fields =
    [
        "SchemaVersion", "AppName", "DisplayName", "Publisher", "SoftwareVersion", "InstallerFile", "InstallerType",
        "Architecture", "InstallContext", "InstallMode", "InstallArgs", "UninstallArgs", "ProductCode", "RunningProcess",
        "Detection", "StagedAt", "FileHashes", "Icon", "UninstallCommand", "DetectedEngine", "AdHocSource",
        "DeploymentTypes", "CommandOverrides", "MsixPackage"
    ];

    [Fact]
    public void Write_AnUnprofiledBuildAddsOnlyTheSchema4DefaultsAndTheDigest()
    {
        File.WriteAllText(Path.Combine(_root, "setup.msi"), "payload");
        _service.Write(ManifestPath, new StageManifest { AppName = "A", SoftwareVersion = "1.0" });

        var written = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        Assert.Equal(
            [.. V3Fields, "BuildId", "ApplicationId", "ProfileId", "ProfileRevision", "Timing", "Execution",
             "DetectionSource", "CustomAssets", "RunOverrides", "PlanDigest"],
            written.Select(p => p.Key));
        Assert.Equal(4, written["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("", written["BuildId"]!.GetValue<string>());
        Assert.Equal("", written["ApplicationId"]!.GetValue<string>());
        Assert.Equal("default", written["ProfileId"]!.GetValue<string>());
        Assert.Equal(0, written["ProfileRevision"]!.GetValue<int>());
        Assert.Equal("""{"EstimatedMinutes":null,"MaximumMinutes":null}""", written["Timing"]!.ToJsonString());
        Assert.Equal("""{"Context":null,"LogonRequirement":null,"UserInteraction":null,"ScriptHost":null}""", written["Execution"]!.ToJsonString());
        Assert.Equal("Default", written["DetectionSource"]!.GetValue<string>());
        Assert.Equal("[]", written["CustomAssets"]!.ToJsonString());
        Assert.Equal("{}", written["RunOverrides"]!.ToJsonString());
        Assert.Equal(StageManifestPlanDigest.Compute(written), written["PlanDigest"]!.GetValue<string>());
    }

    [Fact]
    public void Read_RefusesASchemaNewerThanFour()
    {
        File.WriteAllText(ManifestPath, """{"SchemaVersion":5,"AppName":"Future","FileHashes":[]}""");
        var ex = Assert.Throws<InvalidOperationException>(() => _service.Read(ManifestPath));
        Assert.Equal(
            $"Stage manifest schema version 5 is newer than this AppPackager understands (maximum 4): {ManifestPath}. " +
            "Update AppPackager, or re-run the Stage phase with this version.",
            ex.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_AnOlderSchemaCarriesTheSchema4Defaults(int schema)
    {
        File.WriteAllText(ManifestPath, $$"""{"SchemaVersion":{{schema}},"AppName":"Old","Publisher":"P","SoftwareVersion":"1.0"}""");
        var read = _service.Read(ManifestPath);
        Assert.Equal(schema, read.SchemaVersion);
        Assert.Equal("", read.BuildId);
        Assert.Equal("default", read.ProfileId);
        Assert.Equal(0, read.ProfileRevision);
        Assert.Equal("Default", read.DetectionSource);
        Assert.Empty(read.CustomAssets);
        Assert.Null(read.Timing.EstimatedMinutes);
        Assert.Null(read.PlanDigest);
        Assert.Null(read.ScriptSigning);
    }

    [Fact]
    public void ReadThenWrite_KeepsTheProfiledFieldsAndScriptSigningUnchanged()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlanDigest", "multi-deployment-type.json");
        var original = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var manifest = _service.Read(source);
        manifest.ScriptSigning = JsonNode.Parse("""{ "PolicyDigest": "AB", "Detection": { "Status": "Signed", "File": "scripts\\detect.ps1" }, "Unknown": [1, { "x": null }] }""");
        var signing = manifest.ScriptSigning!.ToJsonString();

        _service.Write(ManifestPath, manifest);
        var written = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();

        Assert.Equal(signing, written["ScriptSigning"]!.ToJsonString());
        foreach (var field in new[] { "BuildId", "ApplicationId", "ProfileId", "ProfileRevision", "Timing", "Execution",
                     "DetectionSource", "CustomAssets", "RunOverrides", "TitleMode" })
            Assert.True(JsonNode.DeepEquals(original[field], written[field]), field);
        Assert.Equal("NoVersion", _service.Read(ManifestPath).TitleMode);
    }
}
