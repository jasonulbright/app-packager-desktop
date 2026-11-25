using System.Diagnostics;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The embedded ConfigMgr version lookup runs in Windows PowerShell against a stand-in
/// Get-CMApplication and a file-system drive named for the site: the exact title wins,
/// then "&lt;CMName&gt;*" filtered to release details after CMName, and among several the
/// highest version, a bare number padded to N.0.
/// </summary>
public sealed class ConfigMgrNameLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-cmlookup-" + Guid.NewGuid().ToString("N"));

    public ConfigMgrNameLookupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Runs the lookup with the given (title, version) rows and returns its stdout line.</summary>
    private string Lookup(string cmName, params (string Title, string Version)[] apps) => Lookup(cmName, null, null, apps);

    /// <summary>
    /// Runs the lookup with a provider. The site drive that exists before the query is
    /// rooted at <paramref name="driveRoot"/>; a drive the query creates is recorded with
    /// its provider type and root in created.txt.
    /// </summary>
    private string LookupWithArp(string cmName, string arpName, params (string Title, string Version)[] apps) => Lookup(cmName, null, null, arpName, apps);

    private string Lookup(string cmName, string? provider, string? driveRoot, params (string Title, string Version)[] apps) => Lookup(cmName, provider, driveRoot, null, apps);

    private string Lookup(string cmName, string? provider, string? driveRoot, string? arpName, params (string Title, string Version)[] apps)
    {
        var script = Path.Combine(_dir, "lookup.ps1");
        File.WriteAllText(script, MecmQueryService.QueryScript);
        var rows = string.Join(",", apps.Select(a => $"[pscustomobject]@{{ LocalizedDisplayName = '{a.Title}'; Name = '{a.Title}'; SoftwareVersion = '{a.Version}' }}"));
        var harness = Path.Combine(_dir, "harness.ps1");
        File.WriteAllText(harness, $$"""
            $global:Apps = @({{rows}})
            function Get-CMApplication {
                param([string]$Name, $ErrorAction)
                $pattern = New-Object System.Management.Automation.WildcardPattern($Name, [System.Management.Automation.WildcardOptions]::IgnoreCase)
                @($global:Apps | Where-Object { $pattern.IsMatch($_.LocalizedDisplayName) })
            }
            function New-PSDrive {
                param([string]$Name, [string]$PSProvider, [string]$Root, $ErrorAction)
                Add-Content -LiteralPath '{{Path.Combine(_dir, "created.txt")}}' -Value "$Name|$PSProvider|$Root"
                if ($Root -eq 'unreachable') { throw 'The SMS Provider reported an error.' }
                Microsoft.PowerShell.Management\New-PSDrive -Name $Name -PSProvider FileSystem -Root '{{_dir}}' -Scope Global
            }
            {{(driveRoot is null && provider is not null ? "" : $"Microsoft.PowerShell.Management\\New-PSDrive -Name TST -PSProvider FileSystem -Root '{driveRoot ?? _dir}' -Scope Global | Out-Null")}}
            & '{{script}}' -SiteCode TST -CMName '{{cmName}}'{{(provider is null ? "" : $" -ProviderMachineName '{provider}'")}}{{(arpName is null ? "" : $" -ArpName '{arpName}'")}}
            """);
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private string[] CreatedDrives() =>
        File.Exists(Path.Combine(_dir, "created.txt")) ? File.ReadAllLines(Path.Combine(_dir, "created.txt")) : [];

    [Fact]
    public void WithoutAProviderTheExistingSiteDriveIsUsed()
    {
        Assert.Equal("FOUND|Audacity|3.7.1|1", Lookup("Audacity", ("Audacity", "3.7.1")));
        Assert.Empty(CreatedDrives());
    }

    [Fact]
    public void WithAProviderAndNoSiteDriveTheDriveIsCreatedAtThatProvider()
    {
        Assert.Equal("FOUND|Audacity|3.7.1|1", Lookup("Audacity", "cm01.example.test", null, ("Audacity", "3.7.1")));
        Assert.Equal(["TST|CMSite|cm01.example.test"], CreatedDrives());
    }

    [Fact]
    public void ASiteDriveOfAnotherProviderIsReplacedByOneAtTheRequestedProvider()
    {
        var other = Directory.CreateDirectory(Path.Combine(_dir, "cm02.example.test")).FullName;
        Assert.Equal("FOUND|Audacity|3.7.1|1", Lookup("Audacity", "cm01.example.test", other, ("Audacity", "3.7.1")));
        Assert.Equal(["TST|CMSite|cm01.example.test"], CreatedDrives());
    }

    [Fact]
    public void ASiteDriveOfTheRequestedProviderIsKept()
    {
        Assert.Equal("FOUND|Audacity|3.7.1|1", Lookup("Audacity", _dir.ToUpperInvariant() + @"\", _dir, ("Audacity", "3.7.1")));
        Assert.Empty(CreatedDrives());
    }

    [Fact]
    public void AProviderThatDoesNotAnswerFailsTheQueryInsteadOfUsingAnotherDrive()
    {
        var other = Directory.CreateDirectory(Path.Combine(_dir, "cm02.example.test")).FullName;
        Assert.Equal("ERROR:SITE_DRIVE_FAILED", Lookup("Audacity", "unreachable", other, ("Audacity", "3.7.1")));
        Assert.Equal("ERROR:SITE_DRIVE_FAILED", Lookup("Audacity", "unreachable", null, ("Audacity", "3.7.1")));
    }

    [Fact]
    public void TheProviderReachesTheQueryProcessAsAnArgumentValue()
    {
        var start = MecmQueryService.BuildStartInfo(@"C:\t\q.ps1", "MCM", " cm01.example.test ", "PuTTY");
        var arguments = start.ArgumentList.ToList();
        Assert.Equal("cm01.example.test", arguments[arguments.IndexOf("-ProviderMachineName") + 1]);
        Assert.Equal("MCM", arguments[arguments.IndexOf("-SiteCode") + 1]);
        Assert.Equal("PuTTY", arguments[arguments.IndexOf("-CMName") + 1]);
        Assert.DoesNotContain("-ProviderMachineName", MecmQueryService.BuildStartInfo(@"C:\t\q.ps1", "MCM", " ", "PuTTY").ArgumentList);
    }

    [Fact]
    public void AWordThatContinuesTheNameIsNotTheApplication() =>
        // GitHub Desktop is another word; Git Extensions is found only by the last tier, with its note, because no other title fits.
        Assert.StartsWith("FOUND|Git Extensions 4.3|4.3|1|tier=prefix;", Lookup("Git", ("Git Extensions 4.3", "4.3"), ("GitHub Desktop", "3.4")));

    [Fact]
    public void ReleaseDetailsAfterCMNameStillMatch() =>
        Assert.Equal("FOUND|Git 2.47.1 (x64)|2.47.1|1", Lookup("Git", ("Git 2.47.1 (x64)", "2.47.1"), ("Git Extensions 4.3", "4.3")));

    [Fact]
    public void TheExactTitleWins() =>
        Assert.Equal("FOUND|Audacity|3.7.1|1", Lookup("Audacity", ("Audacity", "3.7.1"), ("Audacity - 3.7.9", "3.7.9")));

    [Fact]
    public void TheHighestVersionWinsAndABareNumberIsPadded() =>
        Assert.Equal("FOUND|NetBeans - 31|31|2", Lookup("NetBeans", ("NetBeans - 30", "30"), ("NetBeans - 31", "31")));

    [Fact]
    public void AMajorVersionContinuationMatchesButALongerMajorDoesNot()
    {
        Assert.Equal("FOUND|Microsoft .NET 8.0.31 - Windows Server Hosting|8.0.31|1",
            Lookup("Microsoft .NET 8", ("Microsoft .NET 8.0.31 - Windows Server Hosting", "8.0.31")));
        Assert.Equal("NOT_FOUND", Lookup("Microsoft .NET 1", ("Microsoft .NET 10.0.12 - Windows Server Hosting", "10.0.12")));
    }

    [Theory]
    [InlineData("PowerShell 7", "PowerShell 7-x64", true)]
    [InlineData("PowerShell 7", "PowerShell 7.5.3", true)]
    [InlineData("PowerShell 7", "PowerShell 7-preview", true)]
    [InlineData("Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 18.5.1.1 for SQL Server", true)]
    [InlineData("Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 18 for SQL Server 18.5.1.1", true)]
    [InlineData("Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 17 for SQL Server", false)]
    public void ATitleWithTheVersionMidNameOrTheArchitectureAttachedIsFound(string cmName, string title, bool found) =>
        Assert.Equal(found ? "FOUND" : "NOT_FOUND", Lookup(cmName, (title, "1.0")).Split('|')[0]);

    [Theory]
    [InlineData("PuTTY", "PuTTY release 0.85 (64-bit)")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox ESR (x64 en-US)")]
    [InlineData("Intune Debug Toolkit", "Intune Debug Toolkit 2.3 beta")]
    public void TheLastTierFindsTheNameThatTheInstallerGivesAndSaysSo(string cmName, string title) =>
        Assert.Equal($"FOUND|{title}|1.0|1|tier=prefix; chose '{title}'", Lookup(cmName, (title, "1.0")));

    [Fact]
    public void TheLastTierPrefersTheTitleWithTheFewestExtraWordsAndSaysSoInTheNote() =>
        Assert.Equal("FOUND|Chrome Beta Channel|2.0|2|tier=prefix; chose 'Chrome Beta Channel'; other matches: Chrome Remote Desktop Host",
            Lookup("Chrome", ("Chrome Remote Desktop Host", "1.0"), ("Chrome Beta Channel", "2.0")));

    [Theory]
    [InlineData("Azure PowerShell", "Microsoft Azure PowerShell - *", "Microsoft Azure PowerShell - November 2026")]
    [InlineData("Intune Debug Toolkit", "IntuneDebugTools*", "IntuneDebugTools")]
    public void TheInstallerNameOfTheDefinitionFindsAnApplicationThatTheWordsOfCMNameDoNotStart(string cmName, string arpName, string title) =>
        Assert.Equal($"FOUND|{title}|1.0|1|tier=arp; chose '{title}'", LookupWithArp(cmName, arpName, (title, "1.0")));

    [Fact]
    public void AnInstallerNameThatNoApplicationCarriesFindsNothing() =>
        Assert.Equal("NOT_FOUND", LookupWithArp("Azure PowerShell", "Microsoft Azure PowerShell - *", ("Microsoft Azure CLI", "2.0")));

    [Fact]
    public void ThePlainTitleOfTheInstallerIsPickedBeforeAThirdPartyBuildEvenWhenTheOtherIsNewer() =>
        Assert.Equal("FOUND|PowerShell 7-x64|7.6.1|2", Lookup("PowerShell 7", ("PowerShell 7 (Devolutions)", "7.6.2"), ("PowerShell 7-x64", "7.6.1")));

    [Fact]
    public void TheLastTierNeverMatchesInTheMiddleOfAWord() =>
        Assert.Equal("NOT_FOUND", Lookup("Git", ("GitHub Desktop", "3.4"), ("Digit Tool", "1.0")));
}
