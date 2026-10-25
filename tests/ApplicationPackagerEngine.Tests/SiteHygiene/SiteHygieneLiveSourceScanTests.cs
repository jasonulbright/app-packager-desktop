using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The Live views never write to the site: the Live scripts and the Live service hold no ConfigMgr write cmdlet, no WMI or
/// CIM write, no SQL statement that changes data and no file write in a script. The Live page code holds no site call at all.
/// </summary>
public sealed class SiteHygieneLiveSourceScanTests
{
    private static readonly Regex CmCmdlet = new(@"\b(?<verb>[A-Z][a-z]+)-CM(?<noun>\w+)", RegexOptions.Compiled);

    private static readonly Regex[] Writes =
    [
        new(@"\b(Set|Remove|New)-(WmiInstance|WmiObject|CimInstance)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bInvoke-(WmiMethod|CimMethod)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.(Put|Delete|InvokeMethod)\s*\(", RegexOptions.Compiled),
        new(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DROP|ALTER|CREATE|TRUNCATE|GRANT)\s+[A-Za-z\[#@]", RegexOptions.Compiled)
    ];

    private static readonly Regex ScriptFileWrite = new(@"\b(Set-Content|Add-Content|Out-File|New-Item|Remove-Item|Move-Item|Copy-Item|Rename-Item|Export-Csv)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "ApplicationPackagerEngine"))) return dir.FullName;
        throw new InvalidOperationException("The repository root was not found above the test folder.");
    }

    /// <summary>The Live service, its contract and its scripts: every file of the Site Hygiene services named for Live.</summary>
    public static IReadOnlyList<string> LiveSources()
    {
        var folder = Path.Combine(Root(), "src", "ApplicationPackagerEngine", "Services", "SiteHygiene");
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith("SiteHygieneLive", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(f) is ".cs" or ".ps1" or ".psm1" or ".sql")
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string WithoutComments(string text, string extension) => extension is ".ps1" or ".psm1"
        ? Regex.Replace(Regex.Replace(text, @"<#.*?#>", "", RegexOptions.Singleline), @"(?m)#.*$", "")
        : Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"(?m)//.*$", "");

    [Fact]
    public void TheLiveScriptsAndTheLiveServiceHoldNoWriteToTheSite()
    {
        var files = LiveSources();
        Assert.Contains(files, f => Path.GetFileName(f) == "SiteHygieneLiveContract.cs");
        var failures = new List<string>();
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var text = WithoutComments(File.ReadAllText(file), extension);
            var name = Path.GetFileName(file);
            foreach (Match m in CmCmdlet.Matches(text))
            {
                var verb = m.Groups["verb"].Value;
                if (verb == "Get" || (verb == "Invoke" && m.Groups["noun"].Value is "WmiQuery" or "Query")) continue;
                failures.Add($"{name}: {m.Value}");
            }
            foreach (var pattern in Writes)
                foreach (Match m in pattern.Matches(text)) failures.Add($"{name}: {m.Value}");
            if (extension is ".ps1" or ".psm1")
                foreach (Match m in ScriptFileWrite.Matches(text)) failures.Add($"{name}: {m.Value}");
        }
        Assert.True(failures.Count == 0, "Write calls in the Live sources:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("Remove-CMApplication -Id 1")]
    [InlineData("Set-CMDistributionPoint -SiteSystemServerName x")]
    [InlineData("$obj.Put()")]
    [InlineData("Invoke-CimMethod -Name Refresh")]
    [InlineData("\"UPDATE v_R_System SET Name0 = 'x'\"")]
    [InlineData("\"DELETE FROM dbo.Foo\"")]
    public void TheScanFindsAWriteOfEachKind(string line)
    {
        var text = WithoutComments(line, ".ps1");
        var found = CmCmdlet.Matches(text).Any(m => m.Groups["verb"].Value != "Get") || Writes.Any(p => p.IsMatch(text));
        Assert.True(found, line);
    }

    [Theory]
    [InlineData("Get-CMDeployment -ErrorAction Stop")]
    [InlineData("Invoke-CMWmiQuery -Query 'SELECT PackageID, State FROM SMS_PackageStatusDistPointsSummarizer' -Option Fast")]
    [InlineData("\"SELECT sys.Name0 FROM v_R_System sys WHERE sys.Client0 = 1\"")]
    [InlineData("'Software Update', 'Content Distribution'")]
    public void TheScanPassesAReadOfEachKind(string line)
    {
        var text = WithoutComments(line, ".ps1");
        var found = CmCmdlet.Matches(text).Any(m => !(m.Groups["verb"].Value == "Get" || (m.Groups["verb"].Value == "Invoke" && m.Groups["noun"].Value == "WmiQuery")))
                    || Writes.Any(p => p.IsMatch(text));
        Assert.False(found, line);
    }

    [Fact]
    public void TheLivePageCodeCallsNoSiteServiceButTheLiveService()
    {
        var root = Path.Combine(Root(), "src", "ApplicationPackagerUI");
        foreach (var name in new[] { "SiteHygieneViewModel.Live.cs", "SiteHygieneViewModel.LiveOptions.cs", "SiteHygieneViewModel.Trends.cs", "SiteHygieneLiveRows.cs" })
        {
            var file = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Services.Hygiene.", text);
            Assert.DoesNotContain("ApplyAsync", text);
            Assert.DoesNotContain("PrepareAsync", text);
            Assert.DoesNotContain("Writer.", text);
            Assert.False(CmCmdlet.IsMatch(text), $"{name} names a ConfigMgr cmdlet.");
        }
    }
}
