using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The post-patch target uses the MSI the patch targets and the ALLUSERS routing of that MSI.</summary>
public sealed class PostPatchTargetTests : IDisposable
{
    private const string Target = "{AAAAAAAA-1111-2222-3333-444444444444}";
    private const string Other = "{BBBBBBBB-1111-2222-3333-444444444444}";
    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Msi(string name, string productCode, string template, string? allUsers = null, string? perUser = null)
    {
        var path = _temp.PathOf(name);
        using var b = new TestMsiBuilder(path);
        b.Property("ProductCode", productCode).Property("ProductVersion", "1.0.0");
        if (allUsers is not null) b.Property("ALLUSERS", allUsers);
        if (perUser is not null) b.Property("MSIINSTALLPERUSER", perUser);
        b.Template(template);
        return path;
    }

    [Fact]
    public void TheBaseIsTheMsiWhoseProductCodeThePatchTargetsNotTheSmallest()
    {
        var small = Msi("small.msi", Other, "x64;1033", "1");
        var targeted = Msi("targeted.msi", Target, "x64;1033", "1");
        var inspector = new NativeMsiInspector();

        var chosen = InstallerAnalysisService.SelectBaseMsi([Target], [small, targeted], inspector);

        Assert.NotNull(chosen);
        Assert.Equal(targeted, chosen!.Value.Path);
        Assert.Equal(Target, chosen.Value.Props.ProductCode);
    }

    [Fact]
    public void NoMsiAmongThePatchTargetsGivesNoBase()
    {
        var other = Msi("other.msi", Other, "x64;1033", "1");

        Assert.Null(InstallerAnalysisService.SelectBaseMsi([Target], [other], new NativeMsiInspector()));
    }

    [Theory]
    [InlineData("1", null, "x64;1033", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, false)]
    [InlineData("1", null, "Intel;1033", @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, false)]
    [InlineData("2", "1", "Intel;1033", @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, true)]
    [InlineData(null, "1", "x64;1033", @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, true)]
    [InlineData(null, null, "x64;1033", @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, false)]
    public void TheKeyFollowsAllUsersAndTheTemplate(string? allUsers, string? perUser, string template, string expected, bool perUserExpected)
    {
        var path = Msi("base.msi", Target, template, allUsers, perUser);
        var inspector = new NativeMsiInspector();
        var props = inspector.ReadProperties(path);

        var (key, explanation) = InstallerAnalysisService.PostPatchKey(Target, props, inspector.ReadTemplateArchitecture(path));

        Assert.Equal(expected, key);
        Assert.Equal(perUserExpected, explanation.Contains("per user", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutABaseTheKeyIsTheMachine32BitGuessAndTheExplanationSaysWhy()
    {
        var (key, explanation) = InstallerAnalysisService.PostPatchKey(Target, null, "");

        Assert.Equal(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + Target, key);
        Assert.Contains("architecture is unknown", explanation);
        Assert.Contains("HKCU", explanation);
    }
}
