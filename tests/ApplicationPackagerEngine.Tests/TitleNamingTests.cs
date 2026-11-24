using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The application title: an architecture tag is never repeated, a variant definition (7-Zip: x64 and ARM64 in one
/// application) is named without it, as the PowerShell app names it, and the option can remove the tag.
/// </summary>
public sealed class TitleNamingTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex AnyTag = new(@"\(\s*(x64|x86|arm64|arm|32-bit|64-bit)\s*\)", RegexOptions.IgnoreCase);

    private static List<AppDefinition> Shipped() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json").Order(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(f), Json)!).ToList();

    private static AppDefinition SevenZip() => Shipped().Single(d => d.Application == "7-Zip");

    [Fact]
    public void SevenZipWithoutInstallerMetadataIsNotTaggedTwice()
    {
        var identity = StagingService.ResolveStageIdentity(SevenZip(), "26.03", "Igor Pavlov", null);
        foreach (var mode in new[] { TitleMode.Default, TitleMode.NoVersion, TitleMode.IncludeVersion })
            Assert.DoesNotContain("(x64) (x64)", ApplicationTitle.Resolve(identity.AppName, "26.03", mode));
        Assert.Equal("7-Zip", ApplicationTitle.Resolve(identity.AppName, "26.03", TitleMode.NoVersion));
        Assert.Equal("7-Zip", identity.DisplayName);
    }

    [Fact]
    public void ASingleRunOfSevenZipKeepsTheMsiProductNameAsThePowerShellAppDoesAndASplitRunUsesTheDefinitionName()
    {
        var metadata = new InstallerMetadata { ProductName = "7-Zip 26.03 (x64 edition)", ProductVersion = "26.03.00.0", MetadataSource = "msi-property-table" };
        var identity = StagingService.ResolveStageIdentity(SevenZip(), "26.03", "Igor Pavlov", metadata);
        // One deployment type: the installer name, so an existing site application keeps upgrading in place.
        Assert.Equal("7-Zip (x64 edition)", ApplicationTitle.Resolve(identity.AppName, "26.03", TitleMode.NoVersion));
        Assert.Equal("7-Zip", TitleNaming.BaseName(SevenZip()));
    }

    [Theory]
    [InlineData("7-Zip (x64) (x64)", true, "7-Zip (x64)")]
    [InlineData("Tool (x64) (X64)", true, "Tool (x64)")]
    [InlineData("Tool (x64)", false, "Tool")]
    [InlineData("Tool (x64) - 1.2", false, "Tool (x64) - 1.2")]
    [InlineData("Tool (x64)", true, "Tool (x64)")]
    [InlineData("Tool (x64) (ARM64)", true, "Tool (x64) (ARM64)")]
    public void FinishRepeatsNoTagAndTheOptionRemovesTheTrailingTag(string title, bool append, string expected) =>
        Assert.Equal(expected, TitleNaming.Finish(title, append));

    [Fact]
    public void NoShippedDefinitionBuildsATitleWithTwoArchitectureTagsOrAChangedName()
    {
        var all = Shipped();
        Assert.True(all.Count >= 300);
        var failures = new List<string>();
        foreach (var def in all)
        {
            var identity = StagingService.ResolveStageIdentity(def, "1.2.3", def.Vendor, null);
            foreach (var mode in new[] { TitleMode.Default, TitleMode.NoVersion, TitleMode.IncludeVersion })
            {
                var title = ApplicationTitle.Resolve(identity.AppName, "1.2.3", mode);
                if (AnyTag.Matches(title).Count > 1 && !title.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) failures.Add($"{def.Application}: '{title}' has two tags");
            }
            // No word of the definition name is lost: the name is the definition name (Application, or CMName of a variant definition).
            var baseName = TitleNaming.BaseName(def);
            if (string.IsNullOrWhiteSpace(def.AppName) && !identity.DisplayName.StartsWith(baseName, StringComparison.Ordinal)) failures.Add($"{def.Application}: display name '{identity.DisplayName}' does not start with '{baseName}'");
            if (TitleNaming.IsVariantDefinition(def) && AnyTag.IsMatch(identity.DisplayName)) failures.Add($"{def.Application}: a variant definition is tagged '{identity.DisplayName}'");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
