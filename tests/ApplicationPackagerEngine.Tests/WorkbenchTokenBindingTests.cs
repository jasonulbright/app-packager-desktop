using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchTokenBindingTests
{
    private static JsonObject Manifest() => WorkbenchTestRoot.Json("""
        { "AppName": "Test App", "SoftwareVersion": "1.2.3", "InstallerFile": "setup.exe",
          "ProductCode": "{11111111-2222-3333-4444-555555555555}" }
        """);

    [Fact]
    public void Resolve_BindsTheFourSupportedTokensInBothContexts()
    {
        Assert.Equal("setup.exe 1.2.3", WorkbenchTokens.Resolve("{{InstallerFile}} {{Version}}", Manifest()));
        Assert.Equal("msiexec /x {11111111-2222-3333-4444-555555555555}", WorkbenchTokens.Resolve("msiexec /x {{ ProductCode }}", Manifest()));
        Assert.Equal("%~dp0install.bat", WorkbenchTokens.Resolve("{{ContentRoot}}install.bat", Manifest(), TokenContext.Command));
        Assert.Equal("$PSScriptRoot\\setup.exe", WorkbenchTokens.Resolve("{{ContentRoot}}\\{{InstallerFile}}", Manifest(), TokenContext.Script));
    }

    [Fact]
    public void Resolve_BindsAgainstADesktopStageManifest()
    {
        var manifest = new StageManifest { InstallerFile = "7z.msi", SoftwareVersion = "24.09" };
        Assert.Equal("7z.msi 24.09", WorkbenchTokens.Resolve("{{InstallerFile}} {{Version}}", manifest));
    }

    [Fact]
    public void Resolve_RejectsAnUnknownToken()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkbenchTokens.Resolve("x {{Secret}}", Manifest()));
        Assert.Equal("Unknown build token '{{Secret}}'; supported tokens are InstallerFile, Version, ProductCode, ContentRoot.", ex.Message);
    }

    [Fact]
    public void Resolve_RejectsATokenWithNoValueAtBuildTime()
    {
        var manifest = Manifest();
        manifest["ProductCode"] = "";
        var ex = Assert.Throws<InvalidOperationException>(() => WorkbenchTokens.Resolve("{{ProductCode}}", manifest));
        Assert.Equal("Build token '{{ProductCode}}' has no value at build time; the stage did not resolve it.", ex.Message);
    }

    [Theory]
    [InlineData("setup.exe {{Version")]
    [InlineData("setup.exe {{1}}")]
    [InlineData("run {{ }}")]
    public void Resolve_RejectsAnUnresolvedBracePair(string text)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkbenchTokens.Resolve(text, Manifest()));
        Assert.Equal($"Unresolved build token in '{text}'.", ex.Message);
    }

    [Fact]
    public void Resolve_LeavesTextWithoutTokensUnchanged()
    {
        Assert.Equal("", WorkbenchTokens.Resolve("", Manifest()));
        Assert.Equal("setup.exe /S {x}", WorkbenchTokens.Resolve("setup.exe /S {x}", Manifest()));
    }

    [Theory]
    [InlineData("SoftwareVersion", "Version", "1'; calc; #")]
    [InlineData("SoftwareVersion", "Version", "1.0 & calc")]
    [InlineData("SoftwareVersion", "Version", "$(calc)")]
    [InlineData("SoftwareVersion", "Version", "1.0’")]
    [InlineData("ProductCode", "ProductCode", "11111111-2222-3333-4444-555555555555")]
    [InlineData("ProductCode", "ProductCode", "{11111111-2222-3333-4444-555555555555}' ; calc")]
    [InlineData("InstallerFile", "InstallerFile", "setup%PATH%.exe")]
    [InlineData("InstallerFile", "InstallerFile", "setup\".exe")]
    [InlineData("InstallerFile", "InstallerFile", "it's.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a“b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a;b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a$b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a`b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a|b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a^b.exe")]
    [InlineData("InstallerFile", "InstallerFile", "a‟b.exe")]
    [InlineData("InstallerFile", "InstallerFile", @"..\a.exe")]
    public void Resolve_RefusesAValueThatIsNotSafeForItsToken(string field, string token, string value)
    {
        var manifest = Manifest();
        manifest[field] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => WorkbenchTokens.Resolve($"run {{{{{token}}}}}", manifest, TokenContext.Script));

        Assert.Contains($"'{{{{{token}}}}}'", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [Theory]
    [InlineData("Version", "2026.10.02.0103")]
    [InlineData("Version", "1.0.0-beta+5_x64")]
    [InlineData("InstallerFile", "Setup (x64) 1.2.exe")]
    [InlineData("InstallerFile", "7z2409-x64.msi")]
    public void Resolve_BindsOrdinaryValues(string token, string value)
    {
        var manifest = Manifest();
        manifest[token == "Version" ? "SoftwareVersion" : token] = value;
        Assert.Equal("run " + value, WorkbenchTokens.Resolve($"run {{{{{token}}}}}", manifest));
    }

    [Fact]
    public void Resolve_ATokenTheTextDoesNotUseIsNotChecked()
    {
        var manifest = Manifest();
        manifest["SoftwareVersion"] = "1.0 (x64)";
        Assert.Equal("setup.exe", WorkbenchTokens.Resolve("{{InstallerFile}}", manifest));
    }
}
