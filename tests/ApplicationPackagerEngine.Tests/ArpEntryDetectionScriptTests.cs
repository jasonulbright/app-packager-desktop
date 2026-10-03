using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// One Add/Remove Programs entry feeds the ConfigMgr and Intune detection
/// script and the WSUS rules. The hosting bundle definitions declare it once.
/// </summary>
public class ArpEntryDetectionScriptTests
{
    // The detection script the hosting bundle definitions staged before they declared the entry.
    private const string PreviousHostingBundleScript = "$minimum = [version]'{version_parts:4}'\r\nforeach ($view in @([Microsoft.Win32.RegistryView]::Registry32)) {\r\n    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)\r\n    $uninstall = $base.OpenSubKey('SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall')\r\n    if (-not $uninstall) { continue }\r\n    foreach ($name in $uninstall.GetSubKeyNames()) {\r\n        $key = $null\r\n        try {\r\n            $key = $uninstall.OpenSubKey($name)\r\n            if (-not $key) { continue }\r\n            $displayName = [string]$key.GetValue('DisplayName')\r\n            $publisher = [string]$key.GetValue('Publisher')\r\n            $displayVersion = [string]$key.GetValue('DisplayVersion')\r\n            $windowsInstaller = $key.GetValue('WindowsInstaller')\r\n        }\r\n        catch { continue }\r\n        finally { if ($key) { $key.Close() } }\r\n        if (-not $displayName.StartsWith('Microsoft .NET CHANNEL.', [StringComparison]::OrdinalIgnoreCase)) { continue }\r\n        if (-not $displayName.EndsWith(' - Windows Server Hosting', [StringComparison]::OrdinalIgnoreCase)) { continue }\r\n        if (-not [string]::Equals($publisher, 'Microsoft Corporation', [StringComparison]::OrdinalIgnoreCase)) { continue }\r\n        $parts = @($displayVersion.Trim().Split('.'))\r\n        if ($parts.Count -gt 4) { continue }\r\n        $numbers = @(foreach ($part in $parts) { $n = 0; if ([int]::TryParse($part, [ref]$n) -and $n -ge 0) { $n } })\r\n        if ($numbers.Count -ne $parts.Count) { continue }\r\n        while ($numbers.Count -lt 4) { $numbers += 0 }\r\n        if ((New-Object System.Version $numbers[0], $numbers[1], $numbers[2], $numbers[3]) -ge $minimum) { Write-Output 'Installed'; exit 0 }\r\n    }\r\n    $uninstall.Close()\r\n}\r\nexit 0";

    [Theory]
    [InlineData("aspnethostingbundle8.json", "8.0", "8.0.31")]
    [InlineData("aspnethostingbundle10.json", "10.0", "10.0.12")]
    [InlineData("aspnethostingbundle8.json", "8.0", "8.0.4")]
    public void TheHostingBundleStagesTheSameDetectionScriptItStagedBefore(string file, string channel, string version)
    {
        var def = DefinitionFixture.Load(file);
        var previous = StageTokens.ApplyVersionParts(PreviousHostingBundleScript.Replace("CHANNEL", channel), version);
        Assert.Equal(previous, StagingService.BuildDetection(def, null, version).ScriptText);
    }

    [Theory]
    [InlineData("aspnethostingbundle8.json", "Microsoft .NET 8.0.")]
    [InlineData("aspnethostingbundle10.json", "Microsoft .NET 10.0.")]
    public void TheHostingBundleManifestCarriesTheSameEntryForWsus(string file, string prefix)
    {
        var entry = StagingService.BuildWsusDetection(DefinitionFixture.Load(file), "8.0.31")!;
        Assert.Equal("ArpEntry", entry.Type);
        Assert.Equal("32", entry.View);
        Assert.Equal(prefix, entry.DisplayNamePrefix);
        Assert.Equal(" - Windows Server Hosting", entry.DisplayNameSuffix);
        Assert.Equal("Microsoft Corporation", entry.Publisher);
        Assert.Equal("8.0.31", entry.Version);
        Assert.False(entry.WindowsInstaller);
        Assert.Equal(ArpEntryDetectionScript.Build(entry), StagingService.BuildDetection(DefinitionFixture.Load(file), null, "8.0.31").ScriptText);
        var rules = new WsusRuleBuilder().Build(new StageManifest { WsusDetection = entry });
        Assert.Equal(WsusRuleSource.WsusDetection, rules.Source);
    }

    [Fact]
    public void ADefinitionWithoutAnEntryCarriesNoWsusDetection()
    {
        Assert.Null(StagingService.BuildWsusDetection(DefinitionFixture.Load("7zip.json"), "24.08"));
        var json = JsonSerializer.Serialize(new StageManifest());
        Assert.DoesNotContain("WsusDetection", json);
        Assert.Equal(StageManifestPlanDigest.Compute(new StageManifest { StagedAt = DateTimeOffset.UnixEpoch }),
            StageManifestPlanDigest.Compute(new StageManifest { StagedAt = DateTimeOffset.UnixEpoch, WsusDetection = null }));
    }

    [Fact]
    public void TheEntryRoundTripsThroughTheManifestWithoutAFalseWindowsInstallerFlag()
    {
        var manifest = new StageManifest { WsusDetection = new ArpEntryDetection { View = "32", DisplayNamePrefix = "A ", Version = "1.0" } };
        var json = JsonSerializer.Serialize(manifest);
        Assert.Contains("\"WsusDetection\":{\"Type\":\"ArpEntry\",\"View\":\"32\",\"DisplayNamePrefix\":\"A \",\"Version\":\"1.0\"}", json);
        Assert.Equal("A ", JsonSerializer.Deserialize<StageManifest>(json)!.WsusDetection!.DisplayNamePrefix);
    }

    [Fact]
    public void BuildsAnExactNameBothViewsAndWindowsInstallerScript()
    {
        var script = ArpEntryDetectionScript.Build(new ArpEntryDetection
        {
            View = "Both", DisplayName = "Tom's Tool", Publisher = "Contoso", WindowsInstaller = true, Version = "5.4"
        });
        Assert.StartsWith("$minimum = [version]'5.4.0.0'\r\nforeach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {", script);
        Assert.Contains("        if (-not [string]::Equals($displayName, 'Tom''s Tool', [StringComparison]::OrdinalIgnoreCase)) { continue }\r\n", script);
        Assert.Contains("        if ([string]$windowsInstaller -ne '1') { continue }\r\n", script);
        Assert.DoesNotContain("StartsWith", script);
    }

    [Theory]
    [InlineData("32", "", "", "1.0", "names no DisplayName or DisplayNamePrefix")]
    [InlineData("x86", "A", "", "1.0", "is not 32, 64 or Both")]
    [InlineData("32", "A", "", "1.0-beta", "not a version of up to four numbers")]
    [InlineData("32", "A", "File", "1.0", "is not ArpEntry")]
    public void RefusesAnEntryItCannotTurnIntoAScript(string view, string prefix, string type, string version, string message)
    {
        var entry = new ArpEntryDetection { View = view, DisplayNamePrefix = prefix, Version = version };
        if (type.Length > 0) entry.Type = type;
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => ArpEntryDetectionScript.Build(entry)).Message);
    }

    [Fact]
    public async Task TheScriptFindsOnlyTheBundleEntryUnderWindowsPowerShell()
    {
        // The script runs against this computer's real registry: no hosting
        // bundle at 999.0 exists, so it reports nothing and exits 0.
        var script = ArpEntryDetectionScript.Build(new ArpEntryDetection
        {
            View = "Both", DisplayNamePrefix = "Microsoft .NET 8.0.", DisplayNameSuffix = " - Windows Server Hosting",
            Publisher = "Microsoft Corporation", Version = "999.0"
        });
        var run = await WindowsPowerShellHost.RunAsync(script, null, default, "ap-arp-test");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.StdOut);
        Assert.Equal("", run.StdErr.Trim());
    }
}
