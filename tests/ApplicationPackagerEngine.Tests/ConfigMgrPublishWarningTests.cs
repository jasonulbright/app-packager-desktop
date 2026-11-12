using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class ConfigMgrPublishWarningTests
{
    [Fact]
    public void AnIconThatWasNotAppliedIsAWarningOfTheResult()
    {
        const string output = """
            Connected to MECM
            Application icon not applied : Access is denied
            CI_ID: 1234
            """;

        var warnings = MecmTarget.ReadWarnings(output);

        Assert.Equal(["The application icon was not applied: Access is denied"], warnings);
    }

    [Fact]
    public void AnIconThatWasNotFoundIsAWarningWithItsPath()
    {
        var warnings = MecmTarget.ReadWarnings("Application icon not found   : \\\\fs\\content\\App\\1.0\\app.png\r\nCI_ID: 1");

        Assert.Equal(["The application icon was not found: \\\\fs\\content\\App\\1.0\\app.png"], warnings);
    }

    [Fact]
    public void AnIconThatWasSetIsNoWarning()
    {
        Assert.Empty(MecmTarget.ReadWarnings("Application icon set         : app.png\r\nCI_ID: 1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Packaged: App")]
    public void NoOutputOrOutputWithoutAWarningGivesNone(string? output)
    {
        Assert.Empty(MecmTarget.ReadWarnings(output));
    }

    [Fact]
    public void TheSameWarningIsReportedOnce_AndEachKindOnItsOwn()
    {
        const string output = "Application icon not applied : denied\r\nApplication icon not applied : denied\r\nApplication icon not found   : x.png";

        Assert.Equal(2, MecmTarget.ReadWarnings(output).Count);
    }

    [Theory]
    [InlineData("err", "content failed", "warn", "err")]
    [InlineData("", "content failed", "warn", "content failed")]
    [InlineData("", "", "warn", "warn")]
    [InlineData(null, null, null, null)]
    [InlineData("", "", "", null)]
    public void TheDetailIsTheErrorThenTheContentFailureThenTheWarnings(string? error, string? contentFailure, string? warnings, string? expected)
    {
        var list = string.IsNullOrEmpty(warnings) ? Array.Empty<string>() : new[] { warnings };

        Assert.Equal(expected, PackagingOutcomeComposer.ConfigMgrDetail(error, contentFailure, list));
    }

    [Fact]
    public void SeveralWarningsAreJoinedInOneLine()
    {
        Assert.Equal("a; b", PackagingOutcomeComposer.ConfigMgrDetail(null, null, ["a", "b"]));
    }

    [Fact]
    public void TheDeadDeployMethodIsGone()
    {
        Assert.DoesNotContain(typeof(MecmTarget).GetMethods(), m => m.Name == "DeployAsync");
    }
}