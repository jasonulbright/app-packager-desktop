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
    private string Lookup(string cmName, params (string Title, string Version)[] apps)
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
            New-PSDrive -Name TST -PSProvider FileSystem -Root '{{_dir}}' | Out-Null
            & '{{script}}' -SiteCode TST -CMName '{{cmName}}'
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

    [Fact]
    public void ALongerProductNameIsNotTheApplication() =>
        Assert.Equal("NOT_FOUND", Lookup("Git", ("Git Extensions 4.3", "4.3"), ("GitHub Desktop", "3.4")));

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
}
