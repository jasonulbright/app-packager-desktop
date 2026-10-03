using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The per-app title choice. Default is the definition's own name;
/// IncludeVersion appends this release's version to the base name; NoVersion
/// keeps the base name alone so one application carries every release.
/// </summary>
public sealed class ApplicationTitleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("Something else")]
    public void UnrecognizedTokensAreThePackagerDefault(string? stored) =>
        Assert.Equal(TitleMode.Default, ApplicationTitle.Parse(stored));

    [Fact]
    public void TokensRoundTrip()
    {
        Assert.Equal(TitleMode.IncludeVersion, ApplicationTitle.Parse("IncludeVersion"));
        Assert.Equal(TitleMode.NoVersion, ApplicationTitle.Parse("NoVersion"));
        Assert.Equal("IncludeVersion", ApplicationTitle.ToToken(TitleMode.IncludeVersion));
        Assert.Equal("NoVersion", ApplicationTitle.ToToken(TitleMode.NoVersion));
        Assert.Equal("", ApplicationTitle.ToToken(TitleMode.Default));
    }

    [Fact]
    public void DefaultLeavesTheNameExactlyAsItStands() =>
        Assert.Equal("Audacity 3.7.9 (x64)",
            ApplicationTitle.Resolve("Audacity 3.7.9 (x64)", "3.7.9", TitleMode.Default));

    [Theory]
    // The version token goes, the architecture qualifier stays.
    [InlineData("Audacity 3.7.9 (x64)", "3.7.9", "Audacity (x64)")]
    [InlineData("Audacity v3.7.9 (x64)", "3.7.9", "Audacity (x64)")]
    [InlineData("Audacity - 3.7.9", "3.7.9", "Audacity")]
    [InlineData("Audacity 3.7.9", "3.7.9", "Audacity")]
    [InlineData("Google Chrome", "141.0.7390.55", "Google Chrome")]
    // A product year is not this release's version and survives.
    [InlineData("Visual Studio 2026 Community 18.0.1", "18.0.1", "Visual Studio 2026 Community")]
    // Channel and language qualifiers survive.
    [InlineData("M365 Apps for Enterprise 16.0.1 (Current, en-US)", "16.0.1", "M365 Apps for Enterprise (Current, en-US)")]
    public void NoVersionRemovesOnlyThisReleasesVersion(string appName, string version, string expected) =>
        Assert.Equal(expected, ApplicationTitle.Resolve(appName, version, TitleMode.NoVersion));

    [Theory]
    [InlineData("Audacity (x64)", "3.7.9", "Audacity (x64) - 3.7.9")]
    [InlineData("Google Chrome", "141.0.7390.55", "Google Chrome - 141.0.7390.55")]
    [InlineData("Audacity - 3.7.9", "3.7.9", "Audacity - 3.7.9")]
    public void IncludeVersionAppendsExactlyOneVersion(string appName, string version, string expected) =>
        Assert.Equal(expected, ApplicationTitle.Resolve(appName, version, TitleMode.IncludeVersion));

    [Fact]
    public void ARepeatedResolveIsStable()
    {
        // The grid can be saved and the app packaged many times; a title that
        // grew a version on each run would create a new application each time.
        var once = ApplicationTitle.Resolve("Audacity 3.7.9", "3.7.9", TitleMode.IncludeVersion);
        Assert.Equal(once, ApplicationTitle.Resolve(once, "3.7.9", TitleMode.IncludeVersion));
    }

    [Fact]
    public void APartialVersionMatchIsNotRemoved()
    {
        // 3.7 is a prefix of 3.7.9, not the token itself.
        Assert.Equal("Audacity 3.7.9", ApplicationTitle.Resolve("Audacity 3.7.9", "3.7", TitleMode.NoVersion));
        Assert.Equal("Notepad++ 8.6", ApplicationTitle.Resolve("Notepad++ 8.6", "8", TitleMode.NoVersion));
    }

    [Fact]
    public void AVersionWithRegexCharactersIsMatchedLiterally() =>
        Assert.Equal("Tool", ApplicationTitle.Resolve("Tool 1.0+build(2)", "1.0+build(2)", TitleMode.NoVersion));

    [Fact]
    public void ATitleThatIsNothingButItsVersionIsRefused()
    {
        // The result would be empty and collide with every other release.
        Assert.Throws<InvalidOperationException>(
            () => ApplicationTitle.Resolve("1.0", "1.0", TitleMode.NoVersion));
    }

    [Theory]
    // A name that already carries the exact version keeps its own order.
    [InlineData("Audacity 3.7.9 (x64)", "3.7.9")]
    [InlineData("Audacity v3.7.9 (x64)", "3.7.9")]
    [InlineData("App - 1.2.3 (x64)", "1.2.3")]
    [InlineData("v1.0", "1.0")]
    public void IncludeVersionKeepsANameThatAlreadyCarriesTheVersion(string appName, string version) =>
        Assert.Equal(appName, ApplicationTitle.Resolve(appName, version, TitleMode.IncludeVersion));

    [Fact]
    public void IncludeVersionStillAppendsWhenOnlyAPrefixOfTheVersionIsPresent() =>
        Assert.Equal("Audacity 3.7 - 3.7.9", ApplicationTitle.Resolve("Audacity 3.7", "3.7.9", TitleMode.IncludeVersion));

    [Theory]
    // Profile, then the per-application entry, then Include, then Remove, then the packager default.
    [InlineData("NoVersion", "IncludeVersion", true, false, TitleMode.NoVersion)]
    [InlineData("No version", "", true, false, TitleMode.NoVersion)]
    [InlineData(null, "IncludeVersion", false, true, TitleMode.IncludeVersion)]
    [InlineData("", "", true, true, TitleMode.IncludeVersion)]
    [InlineData("", "", false, true, TitleMode.NoVersion)]
    [InlineData("", "Packager default", false, false, TitleMode.Default)]
    [InlineData(null, null, false, false, TitleMode.Default)]
    public void TheTitleModeResolvesMostSpecificFirst(string? profile, string? perApplication, bool include, bool remove, TitleMode expected) =>
        Assert.Equal(expected, ApplicationTitle.ResolveMode(profile, perApplication, include, remove));

    [Fact]
    public void AnEmptyVersionLeavesTheNameIntact() =>
        Assert.Equal("Audacity", ApplicationTitle.Resolve("Audacity", "", TitleMode.NoVersion));

    // ── The title reaches the generated script ───────────────────

    [Fact]
    public void TheResolvedTitleNamesTheApplicationAndItsDeploymentType()
    {
        var manifest = new StageManifest
        {
            AppName = "Audacity 3.7.9",
            Publisher = "Audacity Team",
            SoftwareVersion = "3.7.9",
            Detection = new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = @"SOFTWARE\Audacity",
                ValueName = "DisplayVersion",
                ExpectedValue = "3.7.9"
            }
        };
        var title = ApplicationTitle.Resolve(manifest.AppName, manifest.SoftwareVersion, TitleMode.NoVersion);

        var script = MecmTarget.BuildPackageScript(
            manifest, title, @"\\server\share\Apps\Audacity Team\Audacity 3.7.9\3.7.9",
            "P01", "comment", 15, 30);

        Assert.Equal("Audacity", title);
        Assert.Contains("New-CMApplication -Name 'Audacity' -Publisher 'Audacity Team' -SoftwareVersion '3.7.9'", script);
        Assert.Contains("$dtName0 = 'Audacity'", script);
        // Content stays where it was staged, under the versioned folder.
        Assert.Contains(@"\Audacity 3.7.9\3.7.9", script);
    }
}
