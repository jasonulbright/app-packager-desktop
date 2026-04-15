using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class InstallerSettingsGeneratorTests
{
    [Theory]
    [InlineData("NativeMSI")]
    [InlineData("InnoSetup")]
    [InlineData("NSIS")]
    [InlineData("WixBurn")]
    [InlineData("InstallShield")]
    public void GenerateDefaults_KnownFramework_ReturnsParameters(string framework)
    {
        var result = InstallerSettingsGenerator.GenerateDefaults(framework);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.All(result, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Key));
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.False(string.IsNullOrWhiteSpace(p.CommandFragment));
            Assert.False(string.IsNullOrWhiteSpace(p.ControlType));
            Assert.False(string.IsNullOrWhiteSpace(p.Group));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnknownFramework")]
    [InlineData("Squirrel")]
    public void GenerateDefaults_UnknownFramework_ReturnsNull(string? framework)
    {
        var result = InstallerSettingsGenerator.GenerateDefaults(framework);
        Assert.Null(result);
    }

    [Fact]
    public void MsiDefaults_HasQuietAndNoRestart()
    {
        var result = InstallerSettingsGenerator.GenerateDefaults("NativeMSI")!;

        Assert.Contains(result, p => p.Key == "Quiet" && p.CommandFragment == "/qn");
        Assert.Contains(result, p => p.Key == "NoRestart" && p.CommandFragment == "/norestart");
    }

    [Fact]
    public void InnoSetupDefaults_HasVerySilentAndLanguage()
    {
        var result = InstallerSettingsGenerator.GenerateDefaults("InnoSetup")!;

        Assert.Contains(result, p => p.Key == "VerySilent" && p.CommandFragment == "/VERYSILENT");
        Assert.Contains(result, p => p.Key == "Language" && p.ControlType == "Dropdown");
    }

    [Fact]
    public void NsisDefaults_CaseSensitiveSilentFlag()
    {
        var result = InstallerSettingsGenerator.GenerateDefaults("NSIS")!;

        // NSIS requires uppercase /S
        var silent = Assert.Single(result, p => p.Key == "Silent");
        Assert.Equal("/S", silent.CommandFragment);
    }

    [Fact]
    public void AllDefaults_HaveDefaultValues()
    {
        var frameworks = new[] { "NativeMSI", "InnoSetup", "NSIS", "WixBurn", "InstallShield" };

        foreach (var fw in frameworks)
        {
            var result = InstallerSettingsGenerator.GenerateDefaults(fw)!;
            Assert.All(result, p => Assert.False(string.IsNullOrWhiteSpace(p.DefaultValue),
                $"Parameter {p.Key} in {fw} has no default value"));
        }
    }
}
