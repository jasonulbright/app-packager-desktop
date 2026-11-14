namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// PowerShell scripts of the repository (.ps1, .psm1, .psd1) and the text files that a script or a test reads are plain ASCII: no byte-order
/// mark, no dash, curly quote, emoji or any other character above 127. The data files are the definitions, the JSON fixtures that the
/// PowerShell fixture scripts read, and the files that the tests read (workflow, notices, installer script, props, project files and the
/// documents that the drift tests parse). A fixture that needs a character above 127 writes it as a \uXXXX escape. Windows PowerShell 5.1 reads a script without a BOM in the ANSI code page, so one such character changes the
/// meaning of the text. This does not apply to C# or XAML.
/// </summary>
public sealed class PowerShellScriptsAreAsciiTests
{
    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".claude"];

    private static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static IEnumerable<string> Scripts(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) || extension.Equals(".psm1", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".psd1", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            if (SkippedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in Scripts(child)) yield return file;
        }
    }

    /// <summary>The findings of one script's bytes: a byte-order mark, and the line of each byte above 127.</summary>
    internal static IReadOnlyList<string> Findings(byte[] bytes)
    {
        var findings = new List<string>();
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (bom) findings.Add("UTF-8 byte-order mark");
        var line = 1;
        var reported = new HashSet<int>();
        foreach (var b in bom ? bytes.AsSpan(3).ToArray() : bytes)
        {
            if (b == (byte)'\n') line++;
            else if (b > 127 && reported.Add(line)) findings.Add($"non-ASCII character on line {line}");
        }
        return findings;
    }

    private static readonly string[] DataFolders =
    [
        Path.Combine("src", "ApplicationPackagerEngine", "Definitions"),
        Path.Combine("tests", "ApplicationPackagerEngine.Tests", "Fixtures", "PlanDigest"),
        Path.Combine("tests", "ApplicationPackagerEngine.Tests", "Fixtures", "SiteHygiene"),
        Path.Combine("tests", "ApplicationPackagerEngine.Tests", "Fixtures", "WsusRules"),
        Path.Combine("tests", "ApplicationPackagerEngine.Tests", "Fixtures", "WsusAdapter")
    ];

    private static readonly string[][] DataFiles =
    [
        [".github", "workflows", "release.yml"], ["README.md"], ["THIRD-PARTY-NOTICES.md"], ["installer", "AppPackagerDesktop.nsi"],
        ["src", "DesktopRuntime.props"], ["docs", "core", "definitions.md"], ["docs", "reference", "shipped-content.md"],
        ["docs", "reference", "payload-verification.md"]
    ];

    /// <summary>The data files that a script or a test reads. The JSON fixtures of the vulnerability feeds are read by C# only and keep the text of the vendors.</summary>
    private static IEnumerable<string> DataFilesOf(string root)
    {
        foreach (var folder in DataFolders)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.json")) yield return file;
        foreach (var parts in DataFiles) yield return Path.Combine([root, .. parts]);
        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            if (!project.Split(Path.DirectorySeparatorChar).Any(p => SkippedFolders.Contains(p, StringComparer.OrdinalIgnoreCase))) yield return project;
    }

    [Fact]
    public void EveryDataFileThatAScriptOrATestReadsIsPlainAscii()
    {
        var root = Root();
        var files = DataFilesOf(root).ToList();
        Assert.True(files.Count > 300, "The data files were not found: " + files.Count);
        var findings = files.SelectMany(f => { Assert.True(File.Exists(f), f); return Findings(File.ReadAllBytes(f)).Select(x => $"{Path.GetRelativePath(root, f)}: {x}"); }).ToList();
        Assert.True(findings.Count == 0, "Data files that a script or a test reads must be plain ASCII:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void EveryPowerShellScriptOfTheRepositoryIsPlainAscii()
    {
        var root = Root();
        var scripts = Scripts(root).ToList();
        Assert.NotEmpty(scripts);
        var findings = scripts.SelectMany(s => Findings(File.ReadAllBytes(s)).Select(f => $"{Path.GetRelativePath(root, s)}: {f}")).ToList();
        Assert.True(findings.Count == 0, "PowerShell scripts must be plain ASCII:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void TheCheckFindsABomAndANonAsciiCharacterAndAcceptsAscii()
    {
        Assert.Empty(Findings("Write-Host 'ok'\r\n"u8.ToArray()));
        Assert.Equal(["UTF-8 byte-order mark"], Findings([0xEF, 0xBB, 0xBF, (byte)'a']));
        Assert.Equal(["non-ASCII character on line 2"], Findings("a\nb \u2014 c"u8.ToArray()));
    }
}
