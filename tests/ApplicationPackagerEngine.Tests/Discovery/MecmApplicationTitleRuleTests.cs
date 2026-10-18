using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.Discovery;

/// <summary>
/// The whole-title rule that Check ConfigMgr and Discover share: CMName at the start of the
/// title at a word boundary, only release details around it, and no installer-method or
/// architecture qualifier that contradicts the CMName qualifier.
/// </summary>
public class MecmApplicationTitleRuleTests
{
    public static TheoryData<string, string, bool> Cases => new()
    {
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server Hosting", true },
        { "Microsoft .NET 1", "Microsoft .NET 10.0.12 - Windows Server Hosting", false },
        { "Audacity", "Audacity 3.7.5", true },
        { "AIMP", "AIMP (x64)", true },
        { "WinMerge", "WinMerge x64", true },
        { "Contoso Tool", "Contoso Tool - 5.4.2", true },
        { "Git", "Git Extensions 4.3", false },
        { "Git", "GitHub Desktop", false },
        { "Mozilla Firefox", "Mozilla Firefox ESR (x64 en-US)", false },
        { "Microsoft Edge", "Microsoft Edge WebView2 Runtime", false },
        { "PowerShell 7", "PowerShell 7-x64", true },
        { "PowerShell 7", "PowerShell 7.5.3", true },
        { "PowerShell 7", "PowerShell 7-preview", false },
        { "PowerShell 7", "PowerShell 7 LTS (x64) - 7.4.6 (x64)", false },
        { "Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 18.5.1.1 for SQL Server", true },
        { "Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 18 for SQL Server 18.5.1.1", true },
        { "Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 17 for SQL Server", false },
        { "Microsoft ODBC Driver 18 for SQL Server", "Microsoft ODBC Driver 18 for SQL Server Tools", false },
        { "Microsoft OpenJDK 17 (EXE)", "Microsoft OpenJDK 17 (MSI)", false },
        { "Microsoft OpenJDK 17 (MSI)", "Microsoft OpenJDK 17 (EXE)", false },
        { "Microsoft OpenJDK 17 (EXE)", "Microsoft OpenJDK 17 (MSI) (EXE)", false },
        { "Microsoft OpenJDK 17 (MSI)", "Microsoft OpenJDK 17 (EXE) 17.0.12 (MSI)", false },
        { "Microsoft OpenJDK 17 (EXE)", "Microsoft OpenJDK 17 (EXE, per user)", false },
        { "Amazon Corretto 11 (x64)", "Amazon Corretto 11 (x86)", false },
        { "Amazon Corretto 11 (x86)", "Amazon Corretto 11 (x64)", false },
        { "Amazon Corretto 11 (x64)", "Amazon Corretto 11 (x86) (x64)", false },
        { "Amazon Corretto 11 (x64)", "Amazon Corretto 11.0.25 x86", false },
        { "Citrix Workspace LTSR x86", "Citrix Workspace LTSR ARM64 2402", false },
        { "Citrix Workspace LTSR ARM64", "Citrix Workspace LTSR x86 2402", false },
        { "Citrix Workspace LTSR ARM64", "Citrix Workspace LTSR 2402 (x86) ARM64", false },
        { "Microsoft Visual C++ v14 Redistributable (x64)", "Microsoft Visual C++ v14 Redistributable (x86+x64) - 14.44", false },
        { "Microsoft Visual C++ v14 Redistributable (x86+x64)", "Microsoft Visual C++ v14 Redistributable (x64) - 14.44", false },
        { "Eclipse Temurin JDK 11 (x64)", "Eclipse Temurin JDK 11 (32-bit)", false },
        { "Microsoft OpenJDK 17 (EXE)", "Microsoft OpenJDK 17 (EXE) 17.0.12", true },
        { "Microsoft OpenJDK 17 (EXE, per user)", "Microsoft OpenJDK 17 (EXE, per user) 17.0.12", true },
        { "Amazon Corretto 11 (X64)", "amazon corretto 11 (x64) 11.0.25", true },
        { "Amazon Corretto 11 (x64)", "Amazon Corretto 11 (x64) (MSI)", true },
        { "Foo (x64)", "Foo 1.2", true },
        { "Microsoft OpenJDK 17 (EXE)", "Microsoft OpenJDK 17 17.0.12", true },
        { "M365 Apps for Enterprise", "M365 Apps for Enterprise (x64) (MEC)", true },
        { "M365 Apps for Enterprise", "M365 Apps for Enterprise - 16.0.19127.20264 (x64) (MEC)", true },
        { "M365 Apps for Enterprise", "M365 Apps for Enterprise 16.0.17928.20156 (x86)", true },
        { "M365 Apps for Enterprise", "M365 Apps for Enterprise Project (x64)", false },
        { "7-Zip", "7-Zip (x64)", true },
        { "7-Zip", "7-zip 26.03 (x64)", true },
        { "Firefox", "Firefox (en-US)", true },
        { "Mozilla Firefox", "Mozilla Firefox (en-US)", true },
        { "Notepad++", "Notepad++ (64-bit x64)", true },
        { "Eclipse Temurin JDK 11 (x64)", "Eclipse Temurin JDK 11 (64-bit)", true },
        { "Eclipse Temurin JDK 11 (64-bit)", "Eclipse Temurin JDK 11 (x64)", true },
        { "Eclipse Temurin JDK 11 (x64)", "Eclipse Temurin JDK 11 (amd64) 11.0.25", true },
        { "Eclipse Temurin JDK 11 (amd64)", "Eclipse Temurin JDK 11 (64-bit)", true },
        { "Eclipse Temurin JDK 11 (x86)", "Eclipse Temurin JDK 11 (32-bit)", true },
        { "Eclipse Temurin JDK 11 (32-bit)", "Eclipse Temurin JDK 11 (x86)", true },
        { "Microsoft Visual C++ v14 Redistributable (x86+x64)", "Microsoft Visual C++ v14 Redistributable (32-bit+64-bit) - 14.44", true },
        { "Eclipse Temurin JDK 11 (x64)", "Eclipse Temurin JDK 11 (x86)", false },
        { "Eclipse Temurin JDK 11 (64-bit)", "Eclipse Temurin JDK 11 (32-bit)", false },
        { "Eclipse Temurin JDK 11 (32-bit)", "Eclipse Temurin JDK 11 (amd64)", false },
        { "Eclipse Temurin JDK 11 (x86)", "Eclipse Temurin JDK 11 (64-bit)", false },
        { "Microsoft OpenJDK 17 (exe)", "Microsoft OpenJDK 17 (MSI)", false },
        { "Microsoft OpenJDK 17 (MSI)", "Microsoft OpenJDK 17 (exe)", false },
        { "Microsoft OpenJDK 17 (EXE) (x64)", "Microsoft OpenJDK 17 (EXE) (32-bit)", false },
        { "7-Zip", "7-Zip (x64 edition) - 26.03", true },
        { "Foo", "Foo - 1.2.3", true },
        { "Foo", "Foo - v2", true },
        { "Foo", "Foo - V2 (x64)", true },
        { "Foo", "Foo - x64", true },
        { "Foo", "Foo - 64-bit", true },
        { "Foo", "Foo - MSI", true },
        { "Foo", "Foo - (en-US)", true },
        { "Git", "Git - Extensions", false },
        { "Foo", "Foo - Bar", false },
        { "Foo", "Foo - Pro 2", false },
        { "Foo", "Foo - vNext", false },
        { "Foo", "Foo - Portable x64", false },
        { "Foo", "Foo -", false },
        { "M365 Apps for Enterprise", "M365 Apps for Enterprise - Latest (x64) (MEC)", true },
        { "Foo", "Foo - Latest", true },
        { "Foo", "Foo - LATEST (x64)", true },
        { "Foo", "Foo - Latesting", false },
        { "Foo", "Foo - Newest", false },
        { "Foo", "Foo Latest", false },
        { "Foo", "Foo - ", false },
        { "Foo 2", "Foo 2.1 -", false },
        { "Foo 2", "Foo 2.1 - ", false },
        { "Foo 2", "Foo 2.1 - Bar", false },
        { "Foo 2", "Foo 2 - Bar", false },
        { "Foo 2", "Foo 2.1 - 2.1.5", true },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - windows server hosting", true },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server Hosting (x64)", true },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server Hosting 8.0.31", true },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Desktop Runtime", false },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server", false },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server Hostingx", false },
        { "Microsoft .NET 8", "Microsoft .NET 8.0.31 - Windows Server Hosting Bundle", false },
        { "Microsoft .NET 8", "Microsoft .NET 8 - Windows Server Hosting", false },
        { "Foo", "Foo - 26.03", true },
        { "Foo", "Foo - 1.2.3-beta", true },
        { "Foo", "Foo - 2024.1", true },
        { "Foo", "Foo - 1.2.3+build_7", true },
        { "Foo", "Foo - 2FA Tool", false },
        { "Foo", "Foo - 3D Viewer", false },
        { "Foo", "Foo - 7zip", false },
        { "Foo", "Foo - v", false },
        { "Foo", "Foo - 1.", false },
        { "Foo", "Foo - (x64)", true },
        { "Foo", "Foo - (x64", false },
        { "Foo", "Foo - x64)", false },
        { "Foo", "Foo - (open", false },
        { "Audacity", "", false },
        { "", "Audacity", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheRuleDecidesWhetherATitleIsAPackagedTitleOfCMName(string cmName, string title, bool expected) =>
        Assert.Equal(expected, MecmApplicationTitleRule.Matches(cmName, title));

    [Theory]
    [InlineData("Amazon Corretto 11 (x64)", "Amazon*Corretto*11*")]
    [InlineData("Citrix Workspace LTSR ARM64", "Citrix*Workspace*LTSR*")]
    [InlineData("Microsoft OpenJDK 17 (EXE, per user)", "Microsoft*OpenJDK*17*(EXE, per user)*")]
    [InlineData("Mozilla Thunderbird (x64 en-US)", "Mozilla*Thunderbird*(x64 en-US)*")]
    [InlineData("M365 Apps for Enterprise", "M365*Apps*for*Enterprise*")]
    public void TheSearchPatternOmitsCMNameQualifierWords(string cmName, string pattern) =>
        Assert.Equal(pattern, MecmApplicationTitleRule.NamePattern(cmName));

    [Fact]
    public void AnApplicationListManagesCMNameByExactTitleOrByTheRule()
    {
        Assert.True(MecmApplicationTitleRule.IsManaged("Audacity 3.7.9", ["audacity 3.7.9", "CMTrace Open"]));
        Assert.True(MecmApplicationTitleRule.IsManaged("M365 Apps for Enterprise", ["CMTrace Open", "M365 Apps for Enterprise (x64) (MEC)"]));
        Assert.False(MecmApplicationTitleRule.IsManaged("Git", ["Git Extensions 4.3", "GitHub Desktop", "Git - Extensions"]));
        Assert.False(MecmApplicationTitleRule.IsManaged("Microsoft OpenJDK 17 (EXE)", ["Microsoft OpenJDK 17 (MSI) (EXE)"]));
        Assert.False(MecmApplicationTitleRule.IsManaged("Audacity", []));
    }

    private static List<(string File, AppDefinition Definition)> Definitions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(p => (Path.GetFileName(p), JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(p), options)!))
            .ToList();
    }

    /// <summary>
    /// The titles a package run creates from the definition alone: the AppName with a
    /// sample version, or "Application - version (Architecture)", in each title mode.
    /// </summary>
    private static IEnumerable<string> CreatedTitles(AppDefinition definition)
    {
        var versions = new List<string> { "1.2.3", "24.08" };
        var major = System.Text.RegularExpressions.Regex.Match(definition.CMName, @"(\d+)$");
        if (major.Success) versions.Add(major.Groups[1].Value + ".0.1");
        foreach (var version in versions)
        {
            var name = string.IsNullOrWhiteSpace(definition.AppName)
                ? $"{definition.Application} - {version} ({definition.Architecture})"
                : definition.AppName.Replace("{version}", version, StringComparison.OrdinalIgnoreCase).Trim();
            yield return name;
            foreach (var mode in new[] { TitleMode.IncludeVersion, TitleMode.NoVersion })
            {
                string? resolved = null;
                try { resolved = ApplicationTitle.Resolve(name, version, mode); } catch (InvalidOperationException) { }
                if (resolved is not null) yield return resolved;
            }
        }
    }

    /// <summary>
    /// Definitions whose stage takes the title from installer metadata. The fallback title
    /// "Application - version (Architecture)" does not carry CMName, so Check ConfigMgr finds
    /// such an application only when the metadata name does.
    /// </summary>
    private static readonly HashSet<string> MetadataNamed = new(StringComparer.OrdinalIgnoreCase)
    {
        "anaconda.json", "azurestorageexplorer.json", "bitwarden.json", "citrixworkspacecr.json",
        "corretto-jdk11-x64.json", "corretto-jdk11-x86.json", "corretto-jdk17-x64.json", "corretto-jdk21-x64.json",
        "corretto-jdk25-x64.json", "corretto-jdk8-x64.json", "corretto-jdk8-x86.json", "dbeaver.json", "drawio.json",
        "git.json", "googledrive.json", "nodejs.json", "r.json", "rocketchat.json", "rstudio.json", "soapui.json",
        "sqlserver2022express.json", "thunderbird.json", "webex.json"
    };

    [Fact]
    public void EveryDefinitionCMNameMatchesTheTitleTheDefinitionCreates()
    {
        var bad = new List<string>();
        var missed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, definition) in Definitions())
        {
            var cmName = string.IsNullOrWhiteSpace(definition.CMName) ? definition.Application : definition.CMName;
            var titles = CreatedTitles(definition).ToList();
            if (titles.Any(t => MecmApplicationTitleRule.Matches(cmName, t) || string.Equals(cmName, t, StringComparison.OrdinalIgnoreCase))) continue;
            missed.Add(file);
            if (definition.AppName is not null || !MetadataNamed.Contains(file))
                bad.Add($"{file}: CMName '{cmName}' title '{titles[0]}'");
        }
        bad.AddRange(MetadataNamed.Where(f => !missed.Contains(f)).Select(f => $"{f}: listed as metadata-named, but its created title matches"));
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad.Order()));
    }

    [Fact]
    public async Task CheckConfigMgrRunsTheSameRuleInWindowsPowerShell()
    {
        var pairs = Cases.Select(c => ((string)c[0], (string)c[1])).ToList();
        foreach (var (_, definition) in Definitions())
            foreach (var title in CreatedTitles(definition).Take(2))
                pairs.Add((definition.CMName, title));
        pairs.Add(("PowerShell 7", "PowerShell 7 LTS - 7.4.6 (x64)"));
        pairs.Add(("Eclipse Temurin JDK 11", "Eclipse Temurin JDK 11 - 11.0.25 (x86)"));

        var script = new StringBuilder(MecmApplicationTitleRule.PowerShellFunctions).AppendLine();
        script.AppendLine("$pairs = @(");
        foreach (var (cmName, title) in pairs)
            script.AppendLine($"  ,@({PowerShellLiteral.SingleQuoted(cmName)}, {PowerShellLiteral.SingleQuoted(title)})");
        script.AppendLine(")");
        script.AppendLine("foreach ($p in $pairs) { $hit = if ([string]::IsNullOrWhiteSpace($p[0])) { $false } else { Test-MecmApplicationTitle -CMName $p[0] -Title $p[1] }; [Console]::Out.WriteLine([string][bool]$hit) }");
        script.AppendLine("foreach ($p in $pairs) { $n = if ([string]::IsNullOrWhiteSpace($p[0])) { '' } else { Get-MecmApplicationNamePattern -CMName $p[0] }; [Console]::Out.WriteLine('P:' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($n))) }");

        var dir = Path.Combine(Path.GetTempPath(), "apkg-titlerule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "rule.ps1");
            File.WriteAllText(path, script.ToString(), new UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var stderrTask = process.StandardError.ReadToEndAsync();
            var lines = process.StandardOutput.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            await process.WaitForExitAsync();
            var stderr = await stderrTask;
            Assert.True(lines.Length == pairs.Count * 2, stderr);
            var differ = new List<string>();
            for (var i = 0; i < pairs.Count; i++)
            {
                var (cmName, title) = pairs[i];
                var expected = !string.IsNullOrWhiteSpace(cmName) && MecmApplicationTitleRule.Matches(cmName, title);
                if (lines[i] != expected.ToString()) differ.Add($"match '{cmName}' / '{title}': C# {expected}, PowerShell {lines[i]}");
                var pattern = Encoding.UTF8.GetString(Convert.FromBase64String(lines[pairs.Count + i][2..]));
                var expectedPattern = string.IsNullOrWhiteSpace(cmName) ? "" : MecmApplicationTitleRule.NamePattern(cmName);
                if (pattern != expectedPattern) differ.Add($"pattern '{cmName}': C# '{expectedPattern}', PowerShell '{pattern}'");
            }
            Assert.True(differ.Count == 0, string.Join(Environment.NewLine, differ));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheCheckConfigMgrQueryCarriesTheSharedRule()
    {
        Assert.Contains(MecmApplicationTitleRule.PowerShellFunctions, MecmQueryService.QueryScript);
        Assert.Contains("Get-MecmApplicationNamePattern -CMName $CMName", MecmQueryService.QueryScript);
    }
}
