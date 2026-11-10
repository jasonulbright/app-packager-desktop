using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// A WSUS update cannot change after it is published. The description of each update carries a short digest of what the update
/// does (command line, rules, return codes, files), so a repeat publish can tell an unchanged update from a changed one.
/// </summary>
public sealed class WsusPlanDigestTests
{
    private static readonly (int, string, bool)[] Codes = [(0, "Succeeded", false), (3010, "Succeeded", true)];

    private static string Digest(string commandLine = "/S", string installerType = "EXE", string installerFile = "setup.exe",
        string isInstalled = "<installed/>", string isInstallable = "<installable/>", (int, string, bool)[]? codes = null, (string, string)[]? files = null) =>
        WsusPlanDigest.Compute(commandLine, installerType, installerFile, isInstalled, isInstallable, codes ?? Codes, files ?? [("setup.exe", "aa"), ("data.cab", "bb")]);

    [Fact]
    public void TheSameInputsGiveTheSameSixteenHexCharacters()
    {
        Assert.Equal(Digest(), Digest());
        Assert.Matches("^[0-9a-f]{16}$", Digest());
    }

    [Fact]
    public void EachInputChangesTheDigest()
    {
        var baseline = Digest();
        Assert.NotEqual(baseline, Digest(commandLine: "/S /norestart"));
        Assert.NotEqual(baseline, Digest(installerType: "MSI"));
        Assert.NotEqual(baseline, Digest(installerFile: "other.exe"));
        Assert.NotEqual(baseline, Digest(isInstalled: "<installed2/>"));
        Assert.NotEqual(baseline, Digest(isInstallable: "<installable2/>"));
        Assert.NotEqual(baseline, Digest(codes: [(0, "Succeeded", false), (3010, "Succeeded", true), (2, "Succeeded", true)]));
        Assert.NotEqual(baseline, Digest(files: [("setup.exe", "aa"), ("data.cab", "cc")]));
        Assert.NotEqual(baseline, Digest(files: [("setup.exe", "aa")]));
    }

    [Fact]
    public void ARenamedCompanionFileWithTheSameBytesChangesTheDigest() =>
        Assert.NotEqual(Digest(files: [("setup.exe", "aa"), ("setup.ini", "bb")]), Digest(files: [("setup.exe", "aa"), ("config.ini", "bb")]));

    private const string Body = "Start-Process setup.exe -ArgumentList '/S' -Wait\r\nexit 0\r\n";

    private static string Signed(string body, string signature) => body + "\r\n# SIG # Begin signature block\r\n" + signature + "\r\n# SIG # End signature block\r\n";

    [Fact]
    public void AScriptHashDoesNotDependOnTheSignature()
    {
        Assert.Equal(WsusPlanDigest.ScriptHash(Body), WsusPlanDigest.ScriptHash(Signed(Body, "MIIabc")));
        Assert.Equal(WsusPlanDigest.ScriptHash(Signed(Body, "MIIabc")), WsusPlanDigest.ScriptHash(Signed(Body, "MIIxyz-with-another-timestamp")));
    }

    [Fact]
    public void AnEditOfTheScriptBodyChangesTheScriptHashWithOrWithoutASignature()
    {
        var edited = Body.Replace("/S", "/S /norestart");

        Assert.NotEqual(WsusPlanDigest.ScriptHash(Body), WsusPlanDigest.ScriptHash(edited));
        Assert.NotEqual(WsusPlanDigest.ScriptHash(Signed(Body, "MIIabc")), WsusPlanDigest.ScriptHash(Signed(edited, "MIIabc")));
    }

    [Theory]
    [InlineData("tool.ps1", true)]
    [InlineData("Tool.PSM1", true)]
    [InlineData("config.psd1", false)]
    [InlineData("setup.exe", false)]
    public void OnlyScriptsAndModulesAreHashedWithoutTheirSignature(string path, bool expected) =>
        Assert.Equal(expected, WsusPlanDigest.IsSignedScript(path));

    [Fact]
    public void AValueMovedToAnotherFieldDoesNotKeepTheDigest() =>
        Assert.NotEqual(Digest(commandLine: "a", installerFile: "b"), Digest(commandLine: "b", installerFile: "a"));

    [Fact]
    public void TheLineRoundTripsThroughADescription()
    {
        var digest = Digest();
        var description = "Contoso Tool, published by AppPackager." + "\r\n\r\n" + WsusIdentity.IdentityLine("AppPackager:catalog:x", "Update", "1.0") + "\r\n" + WsusPlanDigest.Line(digest);

        Assert.Equal("AppPackager plan: " + digest, WsusPlanDigest.Line(digest));
        Assert.Equal(digest, WsusPlanDigest.Parse(description));
        Assert.NotNull(WsusIdentity.ParseIdentity(description));
        Assert.Equal("1.0", WsusIdentity.ParseIdentity(description)!.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no line here")]
    [InlineData("AppPackager plan: XYZ")]
    [InlineData("AppPackager plan: 0123456789abcdef0")]
    public void ADescriptionWithoutAValidLineHasNoDigest(string description) =>
        Assert.Null(WsusPlanDigest.Parse(description));
}