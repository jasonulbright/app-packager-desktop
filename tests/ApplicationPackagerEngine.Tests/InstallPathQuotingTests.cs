using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The generated wrapper hands the whole argument string to Start-Process,
/// which adds no quoting of its own, so a target directory containing a space
/// reaches the installer split at the space unless the definition quotes it.
/// The NSIS /D= value is the exception: it takes the rest of the command line
/// and must stay unquoted.
/// </summary>
public class InstallPathQuotingTests
{
    private static readonly Regex DrivePath = new(@"(?<!"")\b[A-Za-z]:\\[^""]*", RegexOptions.Compiled);

    [Theory]
    [InlineData("hashtools.json", @"/DIR=""C:\Program Files\HashTools""")]
    [InlineData("heidisql.json", @"/DIR=""C:\Program Files\HeidiSQL""")]
    [InlineData("pspad.json", @"/DIR=""C:\Program Files\PSPad""")]
    public void InnoTargetDirectory_CarriesItsOwnQuotes(string fileName, string expected)
    {
        Assert.Contains(expected, DefinitionFixture.Load(fileName).InstallArgs);
    }

    [Fact]
    public void NoDefinitionPassesAnUnquotedPathContainingASpace()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json"))
        {
            var def = DefinitionFixture.Load(Path.GetFileName(path));
            foreach (var (field, value) in new[] { ("InstallArgs", def.InstallArgs), ("UninstallArgs", def.UninstallArgs) })
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                // Everything from /D= on is the NSIS target directory, unquoted by design.
                var scanned = value;
                var nsis = scanned.IndexOf("/D=", StringComparison.OrdinalIgnoreCase);
                if (nsis >= 0) scanned = scanned[..nsis];

                if (DrivePath.Matches(scanned).Any(m => m.Value.Contains(' ')))
                    offenders.Add($"{Path.GetFileName(path)} {field}: {value}");
            }
        }

        Assert.Empty(offenders);
    }
}
