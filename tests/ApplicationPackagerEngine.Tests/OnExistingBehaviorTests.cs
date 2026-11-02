using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The generated MECM script bakes in what to do about an application the site
/// already holds under this exact title, at any version. Skip leaves it alone
/// and emits a machine-readable marker, Overwrite replaces the deployment
/// types in place, Fail throws in-script.
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

    // The site version is read at run time, so the emitted marker is a
    // concatenation rather than one literal.
    private const string MarkerHead = "[APP_PACKAGER_CONFLICT] existing app=''App'' version=";

    /// <summary>
    /// Recovers what the emitted line prints, by substituting the one runtime
    /// expression and undoing the single-quote doubling.
    /// </summary>
    private static string EmittedMarker(string script, string appVersion)
    {
        var line = script.Split('\n')
            .Single(l => l.Contains(OnExistingConflict.Marker, StringComparison.Ordinal))
            .Trim();
        var body = line["Write-Host ('".Length..^2];
        return body
            .Replace("' + \"'$($app.SoftwareVersion)'\" + '", $"'{appVersion}'")
            .Replace("' + \"'$($existing[0].SoftwareVersion)'\" + '", $"'{appVersion}'")
            .Replace("''", "'");
    }

    [Fact]
    public void DefaultIsSkip_AndEmitsTheConflictMarker()
    {
        var script = Build(Manifest());

        Assert.Contains("On-existing behavior         : Skip", script);
        Assert.Contains(MarkerHead, script);
        Assert.Contains("exit 0", script);
        Assert.DoesNotContain("OnExisting=Fail was requested", script);
    }

    [Fact]
    public void Skip_EmitsTheMarkerBeforeTheIdempotentIconApply()
    {
        var script = Build(Manifest("App.ico"), OnExistingBehavior.Skip);

        var markerAt = script.IndexOf(MarkerHead, StringComparison.Ordinal);
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
    public void Overwrite_ReplacesTheDeploymentTypesWithNoMarkerAndNoExit()
    {
        var script = Build(Manifest(), OnExistingBehavior.Overwrite);

        Assert.Contains("On-existing behavior         : Overwrite", script);
        Assert.Contains("replacing deployment types (v$($app.SoftwareVersion) -> v1.0, operator choice)", script);
        Assert.Contains("$replaceExisting = $true", script);
        Assert.DoesNotContain(MarkerHead, script);
        Assert.DoesNotContain("exit 0", script);
        // The partial-run guard belongs to Skip; Overwrite replaces the set outright.
        Assert.DoesNotContain("missing deployment type(s)", script);
    }

    [Fact]
    public void Overwrite_UsesTheStagingNameReplacementSequence()
    {
        var script = Build(Manifest(), OnExistingBehavior.Overwrite);

        Assert.Contains("if ($replaceExisting) { $replaceDtNames = $currentDts }", script);
        Assert.Equal(1, CountOf(script, "$replaceDtNames = $currentDts"));
        Assert.Equal(1, CountOf(script, "$replaceExisting = $true"));

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
    public void Fail_ThrowsNamingTheApplicationAndTheVersionItHolds()
    {
        var script = Build(Manifest(), OnExistingBehavior.Fail);

        Assert.Contains("throw \"Existing MECM application 'App' already exists at version $($app.SoftwareVersion) and OnExisting=Fail was requested.\"", script);
        Assert.DoesNotContain(MarkerHead, script);
        Assert.DoesNotContain("exit 0", script);
    }

    [Fact]
    public void EveryBehaviorAppliesToAVersionChangeToo()
    {
        // No branch tests the site version: one policy covers every collision.
        foreach (var behavior in new[]
                 { OnExistingBehavior.Skip, OnExistingBehavior.Overwrite, OnExistingBehavior.Fail })
        {
            var script = Build(Manifest(), behavior);
            Assert.DoesNotContain("if ([string]$app.SoftwareVersion -eq '1.0')", script);
        }
    }

    [Fact]
    public void TheCmdletWildcardNoticeIsNotLoggedButOtherWarningsAre()
    {
        const string notice = "WARNING: Search query parameter 'LocalizedDisplayName' supports wildcards and -DisableWildcardHandling is defined. Search will only return exact matches.";
        var seen = new List<string>();
        var progress = MecmTarget.WithoutCmdletNotice(seen.Add)!;
        progress(notice);
        progress("WARNING: another warning");
        progress("Connecting to ConfigMgr...");
        progress(notice);
        Assert.Equal(["WARNING: another warning", "Connecting to ConfigMgr..."], seen);
        Assert.Null(MecmTarget.WithoutCmdletNotice(null));
    }

    [Fact]
    public void ExistingLookupDisablesWildcardHandling()
    {
        // A title ending in a bracket or asterisk would otherwise match a
        // family of applications and skip against the wrong one.
        Assert.Contains(
            "$existing = @(Get-CMApplication -Name 'App' -DisableWildcardHandling -ErrorAction SilentlyContinue)",
            Build(Manifest()));
    }

    [Fact]
    public void MarkerParse_ReadsTheApplicationAndBothVersionsBackOut()
    {
        var line = OnExistingConflict.Format("Audacity", "3.7.8", "3.7.9");

        Assert.True(OnExistingConflict.TryParse(line, out var app, out var version, out var incoming));
        Assert.Equal("Audacity", app);
        Assert.Equal("3.7.8", version);
        Assert.Equal("3.7.9", incoming);
    }

    [Fact]
    public void MarkerParse_ReadsTheLineTheScriptActuallyEmits()
    {
        var emitted = EmittedMarker(Build(Manifest()), "0.9");

        Assert.True(OnExistingConflict.TryParse(emitted, out var app, out var version, out var incoming));
        Assert.Equal("App", app);
        Assert.Equal("0.9", version);
        Assert.Equal("1.0", incoming);
    }

    [Fact]
    public void MarkerParse_ReadsTheLineTheProbeEmits()
    {
        var emitted = EmittedMarker(
            MecmTarget.BuildExistingProbeScript("App", "1.0", "P01"), "0.9");

        Assert.True(OnExistingConflict.TryParse(emitted, out var app, out var version, out var incoming));
        Assert.Equal("App", app);
        Assert.Equal("0.9", version);
        Assert.Equal("1.0", incoming);
    }

    [Fact]
    public void MarkerParse_SurvivesAnApostropheInTheTitle()
    {
        var line = OnExistingConflict.Format("Dev's Tool", "1.0", "2.0");

        Assert.True(OnExistingConflict.TryParse(line, out var app, out var version, out var incoming));
        Assert.Equal("Dev's Tool", app);
        Assert.Equal("1.0", version);
        Assert.Equal("2.0", incoming);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Application already exists    : App (v1.0, unchanged)")]
    [InlineData("[APP_PACKAGER_CONFLICT] existing")]
    [InlineData("[APP_PACKAGER_CONFLICT] existing app='App' version='1.0'")]
    public void MarkerParse_IgnoresEveryOtherLine(string line)
    {
        Assert.False(OnExistingConflict.TryParse(line, out _, out _, out _));
    }

    [Fact]
    public void Probe_MatchesTheTitleExactlyAndRefusesDuplicates()
    {
        var script = MecmTarget.BuildExistingProbeScript("App", "1.0", "P01");

        Assert.Contains("Get-CMApplication -Name 'App' -DisableWildcardHandling -ErrorAction Stop", script);
        Assert.Contains("$_.LocalizedDisplayName -eq 'App' -or $_.Name -eq 'App'", script);
        Assert.Contains("Multiple applications have the exact title 'App'; resolve duplicates before packaging.", script);
    }

    [Fact]
    public void Probe_WritesNothingToTheSite()
    {
        var script = MecmTarget.BuildExistingProbeScript("App", "1.0", "P01");

        Assert.DoesNotContain("New-CMApplication", script);
        Assert.DoesNotContain("Set-CMApplication", script);
        Assert.DoesNotContain("Add-CMScriptDeploymentType", script);
        Assert.DoesNotContain("Remove-CMDeploymentType", script);
    }

    [Fact]
    public void Probe_StaysSilentWhenNothingMatches()
    {
        var script = MecmTarget.BuildExistingProbeScript("App", "1.0", "P01");

        var markerAt = script.IndexOf(OnExistingConflict.Marker, StringComparison.Ordinal);
        var guardAt = script.IndexOf("if ($existing.Count -eq 1) {", StringComparison.Ordinal);
        Assert.True(guardAt > 0 && markerAt > guardAt);
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
