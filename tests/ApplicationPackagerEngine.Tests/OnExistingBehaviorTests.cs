using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The generated MECM script bakes in what to do about an application that
/// already exists at the same version. Skip is the previous behavior plus a
/// machine-readable marker, Overwrite joins the version-change replacement
/// path, Fail throws in-script.
/// </summary>
public sealed class OnExistingBehaviorTests
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest Manifest(string? icon = null) => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Icon = icon,
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    private static string Build(StageManifest manifest, OnExistingBehavior? onExisting = null) =>
        onExisting is null
            ? MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment", 15, 30)
            : MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment", 15, 30,
                null, null, null, null, null, onExisting.Value);

    // The marker sits inside a single-quoted PowerShell string, so its own
    // quotes are doubled in the script text and single again at runtime.
    private const string Marker = "[APP_PACKAGER_CONFLICT] existing-same-version app=''App'' version=''1.0''";

    [Fact]
    public void DefaultIsSkip_AndEmitsTheConflictMarker()
    {
        var script = Build(Manifest());

        Assert.Contains("On-existing behavior         : Skip", script);
        Assert.Contains(Marker, script);
        Assert.Contains("exit 0", script);
        Assert.DoesNotContain("OnExisting=Fail was requested", script);
    }

    [Fact]
    public void Skip_EmitsTheMarkerBeforeTheIdempotentIconApply()
    {
        var script = Build(Manifest("App.ico"), OnExistingBehavior.Skip);

        var markerAt = script.IndexOf(Marker, StringComparison.Ordinal);
        var validatedAt = script.IndexOf("Deployment type(s) validated", StringComparison.Ordinal);
        var iconAt = script.IndexOf("$idemIcon =", StringComparison.Ordinal);

        Assert.True(validatedAt > 0 && markerAt > validatedAt);
        Assert.True(iconAt > markerAt);
        // The idempotent apply still runs, and still ahead of the clean exit.
        Assert.True(script.IndexOf("exit 0", StringComparison.Ordinal) > iconAt);
    }

    [Fact]
    public void Skip_KeepsThePartialRunGuard()
    {
        var script = Build(Manifest(), OnExistingBehavior.Skip);

        Assert.Contains("if ($missingDts.Count -eq 0) {", script);
        Assert.Contains("missing deployment type(s)", script);
    }

    [Fact]
    public void Overwrite_SameVersion_JoinsTheReplacementPathWithNoMarkerAndNoExit()
    {
        var script = Build(Manifest(), OnExistingBehavior.Overwrite);

        Assert.Contains("On-existing behavior         : Overwrite", script);
        Assert.Contains("overwriting deployment types (same version, operator choice)", script);
        Assert.Contains("$replaceExisting = $true", script);
        Assert.DoesNotContain(Marker, script);
        Assert.DoesNotContain("exit 0", script);
        // The partial-run guard belongs to Skip; Overwrite replaces the set outright.
        Assert.DoesNotContain("missing deployment type(s)", script);
    }

    [Fact]
    public void Overwrite_SharesOneReplacementSequenceWithTheVersionChangeBranch()
    {
        var script = Build(Manifest(), OnExistingBehavior.Overwrite);

        // One inventory assignment serves both branches.
        Assert.Contains("if ($replaceExisting) { $replaceDtNames = $currentDts }", script);
        Assert.Equal(1, CountOf(script, "$replaceDtNames = $currentDts"));
        Assert.Equal(2, CountOf(script, "$replaceExisting = $true"));

        // Staging-name create, remove-old, rename.
        Assert.Contains("$dtCreateName0 = if ($replaceDtNames.Count -gt 0) { \"$dtName0 (staging)\" } else { $dtName0 }", script);
        Assert.Contains("foreach ($oldDt in $replaceDtNames) {", script);
        Assert.Contains("Remove-CMDeploymentType -ApplicationName 'App'", script);
        Assert.Contains("-NewDeploymentTypeName $dtName0", script);
    }

    [Fact]
    public void Overwrite_ReAppliesApplicationFieldsAndIcon()
    {
        var script = Build(Manifest("App.ico"), OnExistingBehavior.Overwrite);

        Assert.Contains("if ($existing.Count -eq 1) {", script);
        Assert.Contains("Set-CMApplication -Name 'App' -SoftwareVersion '1.0' -Publisher 'Vendor' -Description 'comment'", script);
        Assert.Contains("Set-CMApplication -Name 'App' -IconLocationFile $iconPath -ErrorAction Stop", script);
    }

    [Fact]
    public void Fail_ThrowsNamingTheApplicationAndItsVersion()
    {
        var script = Build(Manifest(), OnExistingBehavior.Fail);

        Assert.Contains("throw \"Existing MECM application 'App' is already at version 1.0 and OnExisting=Fail was requested.\"", script);
        Assert.DoesNotContain(Marker, script);
        Assert.DoesNotContain("exit 0", script);
    }

    [Fact]
    public void VersionChangeBranch_IsUnaffectedByTheBehavior()
    {
        foreach (var behavior in new[]
                 { OnExistingBehavior.Skip, OnExistingBehavior.Overwrite, OnExistingBehavior.Fail })
        {
            var script = Build(Manifest(), behavior);
            Assert.Contains("replacing deployment types (v$($app.SoftwareVersion) -> v1.0)", script);
        }
    }

    [Fact]
    public void MarkerParse_ReadsTheApplicationAndVersionBackOut()
    {
        var line = OnExistingConflict.Format("Audacity 3.7.9", "3.7.9");

        Assert.True(OnExistingConflict.TryParse(line, out var app, out var version));
        Assert.Equal("Audacity 3.7.9", app);
        Assert.Equal("3.7.9", version);
    }

    [Fact]
    public void MarkerParse_ReadsTheLineTheScriptActuallyEmits()
    {
        var markerLine = Build(Manifest())
            .Split('\n')
            .Single(l => l.Contains(OnExistingConflict.Marker, StringComparison.Ordinal))
            .Trim()
            .Replace("Write-Host '", "")[..^1]
            .Replace("''", "'");

        Assert.True(OnExistingConflict.TryParse(markerLine, out var app, out var version));
        Assert.Equal("App", app);
        Assert.Equal("1.0", version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Application already exists    : App (v1.0, unchanged)")]
    [InlineData("[APP_PACKAGER_CONFLICT] existing-same-version")]
    public void MarkerParse_IgnoresEveryOtherLine(string line)
    {
        Assert.False(OnExistingConflict.TryParse(line, out _, out _));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
