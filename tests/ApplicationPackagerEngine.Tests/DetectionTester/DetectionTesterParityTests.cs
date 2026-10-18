using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

/// <summary>Runs only on a computer that has the standalone Detection Tester module.</summary>
public sealed class ParityFactAttribute : FactAttribute
{
    public const string ModulePath = @"C:\projects\detection-tester\Module\DetectionTesterCommon.psm1";

    public ParityFactAttribute()
    {
        if (!File.Exists(ModulePath)) Skip = "The standalone Detection Tester module is not on this computer.";
    }
}

/// <summary>
/// Evaluates the same clauses with the standalone tester's module, imported in a child
/// Windows PowerShell, and with <see cref="DetectionTestService"/>, over real keys and
/// files of this computer. The clauses are passed the way the standalone window passes
/// them. Every difference is written to the test output; the test fails when the set of
/// differing clauses is not the documented set.
/// </summary>
public sealed class DetectionTesterParityTests(ITestOutputHelper output)
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>The clauses where the two evaluators are known to disagree, with the reason.</summary>
    private static readonly Dictionary<string, string> DocumentedDifferences = new()
    {
        ["rkv-string-greater-than-number"] = "A String value with GreaterThan: the module compares as a version, then as an integer; the service compares strings.",
        ["rkv-begins-with"] = "BeginsWith: the module does not implement it and reports not detected; the service compares the prefix.",
        ["rkv-not-equals"] = "NotEquals: the module does not implement it and reports not detected; the service compares.",
        ["rkv-no-expected-value"] = "No expected value: the module compares with an empty string; the service checks that the value exists.",
        ["rkv-multi-string"] = "A multi-string value: the module joins the strings with spaces and compares; the service refuses the comparison.",
        ["script-write-host"] = "Write-Host: the module's runspace sends it to the information stream (not detected); the child process writes it to stdout (detected), as ConfigMgr does.",
        ["negative-unsupported-operator"] = "An unsupported operator in negative mode: the module reports not detected, which passes; the service reports that the clause could not be evaluated, which fails.",
        ["negative-file-property-size"] = "An unsupported file property in negative mode: the module reports not detected, which passes; the service fails the test."
    };

    private sealed record ParityCase(string Name, StageDetection Detection, bool Negative = false);

    private static StageDetection Rkv(string key, string valueName, string expected, string op, string type = "String",
        bool is64 = true, string hive = "LocalMachine") => new()
    {
        Type = "RegistryKeyValue",
        RegistryKeyRelative = key,
        ValueName = valueName,
        ExpectedValue = expected,
        Operator = op,
        PropertyType = type,
        Is64Bit = is64,
        Hive = hive
    };

    private static StageDetection Rk(string key, bool is64 = true, string hive = "LocalMachine") =>
        new() { Type = "RegistryKey", RegistryKeyRelative = key, Is64Bit = is64, Hive = hive };

    private static StageDetection FileRule(string folder, string name, string property = "Existence", string expected = "",
        string op = "GreaterEquals", bool is64 = true) => new()
    {
        Type = "File",
        FilePath = folder,
        FileName = name,
        PropertyType = property,
        ExpectedValue = expected,
        Operator = op,
        Is64Bit = is64
    };

    private static StageDetection Script(string text) => new() { Type = "Script", ScriptText = text, ScriptLanguage = "PowerShell" };

    private static StageDetection Compound(string connector, params StageDetection[] clauses) =>
        new() { Type = "Compound", Connector = connector, Clauses = clauses };

    private static string? ReadValue(RegistryHive hive, RegistryView view, string key, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var sub = root.OpenSubKey(key);
        return sub?.GetValue(name)?.ToString();
    }

    private static bool KeyExists(RegistryView view, string key)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var sub = root.OpenSubKey(key);
        return sub is not null;
    }

    private static List<ParityCase> Cases()
    {
        var build = ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, CurrentVersion, "CurrentBuildNumber")!;
        var product = ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, CurrentVersion, "ProductName")!;
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var notepadVersion = DetectionEvaluator.NormalizeFileVersion(
            System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(system32, "notepad.exe")).FileVersion);
        var multiString = MultiStringCandidates
            .Select(c => (c.Key, c.Name, Text: ReadMultiString(c.Key, c.Name)))
            .FirstOrDefault(c => c.Text is not null);
        var absentKey = @"SOFTWARE\ApPackagerParity-" + Guid.NewGuid().ToString("N");
        var only64 = Only64BitCandidates
            .FirstOrDefault(k => KeyExists(RegistryView.Registry64, k) && !KeyExists(RegistryView.Registry32, k));

        var cases = new List<ParityCase>
        {
            new("rkv-equals-build", Rkv(CurrentVersion, "CurrentBuildNumber", build, "IsEquals")),
            new("rkv-equals-wrong", Rkv(CurrentVersion, "CurrentBuildNumber", "1", "IsEquals")),
            new("rkv-equals-case-insensitive", Rkv(CurrentVersion, "ProductName", product.ToLowerInvariant(), "IsEquals")),
            new("rkv-string-greater-equals", Rkv(CurrentVersion, "CurrentBuildNumber", "10000", "GreaterEquals")),
            new("rkv-string-greater-than-number", Rkv(CurrentVersion, "CurrentBuildNumber", "9999", "GreaterThan")),
            new("rkv-integer-greater-than", Rkv(CurrentVersion, "CurrentBuildNumber", "9999", "GreaterThan", "Integer")),
            new("rkv-integer-less-than", Rkv(CurrentVersion, "CurrentBuildNumber", "9999", "LessThan", "Integer")),
            new("rkv-dword-equals", Rkv(CurrentVersion, "CurrentMajorVersionNumber", "10", "IsEquals")),
            new("rkv-dword-integer-greater-equals", Rkv(CurrentVersion, "CurrentMajorVersionNumber", "6", "GreaterEquals", "Integer")),
            new("rkv-version-greater-equals", Rkv(CurrentVersion, "CurrentVersion", "6.0", "GreaterEquals", "Version")),
            new("rkv-version-less-than", Rkv(CurrentVersion, "CurrentVersion", "6.0", "LessThan", "Version")),
            new("rkv-begins-with", Rkv(CurrentVersion, "ProductName", "Windows", "BeginsWith")),
            new("rkv-not-equals", Rkv(CurrentVersion, "ProductName", "Not a product", "NotEquals")),
            new("rkv-no-expected-value", Rkv(CurrentVersion, "CurrentVersion", "", "IsEquals")),
            new("rkv-missing-value", Rkv(CurrentVersion, "NoSuchValueName", "1", "IsEquals")),
            new("rkv-missing-key", Rkv(absentKey, "Version", "1", "IsEquals")),
            new("rkv-32-bit-view", Rkv(CurrentVersion, "CurrentBuildNumber", build, "IsEquals", is64: false)),
            new("rkv-hkcu", Rkv(@"Control Panel\Desktop", "WheelScrollLines", ReadValue(RegistryHive.CurrentUser, RegistryView.Registry64, @"Control Panel\Desktop", "WheelScrollLines") ?? "3", "IsEquals", hive: "CurrentUser")),
            new("rk-exists", Rk(CurrentVersion)),
            new("rk-exists-32-bit-view", Rk(CurrentVersion, is64: false)),
            new("rk-missing", Rk(absentKey)),
            new("rk-hkcu", Rk(@"Software\Microsoft\Windows\CurrentVersion", hive: "CurrentUser")),
            new("file-exists", FileRule(system32, "notepad.exe")),
            new("file-exists-environment-variable", FileRule(@"%SystemRoot%\System32", "notepad.exe")),
            new("file-missing", FileRule(system32, "no-such-file-" + Guid.NewGuid().ToString("N") + ".exe")),
            new("file-version-greater-equals", FileRule(system32, "notepad.exe", "Version", "6.0")),
            new("file-version-greater-equals-high", FileRule(system32, "notepad.exe", "Version", "99.0")),
            new("file-version-equals", FileRule(system32, "notepad.exe", "Version", notepadVersion, "IsEquals")),
            new("file-32-bit-view-exists", FileRule(system32, "notepad.exe", is64: false)),
            new("file-32-bit-view-version", FileRule(system32, "notepad.exe", "Version", "6.0", is64: false)),
            new("file-property-size", FileRule(system32, "notepad.exe", "Size", "1")),
            new("script-write-output", Script("Write-Output 'found'")),
            new("script-no-output", Script("$x = 1")),
            new("script-write-host", Script("Write-Host 'found'")),
            new("script-output-then-exit-1", Script("Write-Output 'found'\nexit 1")),
            new("script-error-only", Script("Write-Error 'not here'")),
            new("compound-and-detected", Compound("And", Rk(CurrentVersion), FileRule(system32, "notepad.exe"))),
            new("compound-and-not-detected", Compound("And", Rk(absentKey), FileRule(system32, "notepad.exe"))),
            new("compound-or-detected", Compound("Or", Rk(absentKey), FileRule(system32, "notepad.exe"))),
            new("compound-or-not-detected", Compound("Or", Rk(absentKey), FileRule(system32, "missing-" + Guid.NewGuid().ToString("N")))),
            new("negative-missing-key", Rk(absentKey), Negative: true),
            new("negative-present-key", Rk(CurrentVersion), Negative: true),
            new("negative-unsupported-operator", Rkv(CurrentVersion, "ProductName", "x", "Matches"), Negative: true),
            new("negative-file-property-size", FileRule(system32, "notepad.exe", "Size", "1"), Negative: true)
        };
        Assert.True(multiString.Text is not null,
            "No multi-string value of the candidate list exists: " + string.Join(", ", MultiStringCandidates.Select(c => $@"{c.Key}\{c.Name}")));
        Assert.True(only64 is not null,
            "No key of the candidate list exists in the 64-bit view only: " + string.Join(", ", Only64BitCandidates));
        cases.Add(new("rkv-multi-string", Rkv(multiString.Key, multiString.Name, multiString.Text!, "IsEquals")));
        cases.Add(new("rk-only-in-64-bit-view-read-64", Rk(only64!)));
        cases.Add(new("rk-only-in-64-bit-view-read-32", Rk(only64!, is64: false)));
        return cases;
    }

    /// <summary>The cases that read a view-specific key or a multi-string value; each run asserts it ran them.</summary>
    public static readonly string[] RequiredCases =
        ["rkv-multi-string", "rk-only-in-64-bit-view-read-64", "rk-only-in-64-bit-view-read-32", "rkv-32-bit-view", "rk-exists-32-bit-view"];

    private static readonly string[] Only64BitCandidates =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notifications",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT"
    ];

    private static readonly (string Key, string Name)[] MultiStringCandidates =
    [
        (@"SYSTEM\CurrentControlSet\Control\ServiceGroupOrder", "List"),
        (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost", "netsvcs"),
        (@"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute")
    ];

    [Fact]
    public void TheViewAndMultiStringCasesAreAlwaysBuilt()
    {
        var names = Cases().Select(c => c.Name).ToList();

        Assert.All(RequiredCases, name => Assert.Contains(name, names));
    }

    private static string? ReadMultiString(string key, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var sub = root.OpenSubKey(key);
        return sub?.GetValue(name) is string[] list ? string.Join(" ", list) : null;
    }

    private const string ModuleRunner = """
        $ErrorActionPreference = 'Stop'
        $reader = [System.IO.StreamReader]::new([Console]::OpenStandardInput(), [System.Text.UTF8Encoding]::new($false))
        $request = $reader.ReadToEnd() | ConvertFrom-Json
        Import-Module $request.ModulePath -DisableNameChecking
        $results = foreach ($case in $request.Cases) {
            $c = $case.Detection
            $r = switch ($c.Type) {
                'RegistryKeyValue' { Test-RegistryKeyValueDetection -RegistryKeyRelative $c.RegistryKeyRelative -ValueName $c.ValueName -ExpectedValue $c.ExpectedValue -Operator $c.Operator -PropertyType $c.PropertyType -Is64Bit ([bool]$c.Is64Bit) -Hive $c.Hive }
                'RegistryKey'      { Test-RegistryKeyDetection -RegistryKeyRelative $c.RegistryKeyRelative -Is64Bit ([bool]$c.Is64Bit) -Hive $c.Hive }
                'File'             { Test-FileDetection -FilePath $c.FilePath -FileName $c.FileName -PropertyType $c.PropertyType -ExpectedValue $c.ExpectedValue -Operator $c.Operator -Is64Bit ([bool]$c.Is64Bit) }
                'Script'           { Test-ScriptDetection -ScriptText $c.ScriptText -TimeoutSeconds 30 }
                'Compound'         { Test-CompoundDetection -Connector $c.Connector -Clauses @($c.Clauses) }
            }
            $detected = [bool]$r.Detected
            [pscustomobject]@{
                Name     = $case.Name
                Detected = $detected
                Passed   = $(if ($case.Negative) { -not $detected } else { $detected })
                Details  = [string]$r.Details
            }
        }
        [Console]::Out.WriteLine('PARITY-JSON:' + (ConvertTo-Json -InputObject @($results) -Compress -Depth 4))
        """;

    [ParityFact]
    public async Task TheServiceAgreesWithTheStandaloneModuleExceptForTheDocumentedDifferences()
    {
        var cases = Cases();
        Assert.True(cases.Count >= 25);

        var request = new JsonObject
        {
            ["ModulePath"] = ParityFactAttribute.ModulePath,
            ["Cases"] = new JsonArray(cases.Select(c => (JsonNode)new JsonObject
            {
                ["Name"] = c.Name,
                ["Negative"] = c.Negative,
                ["Detection"] = JsonSerializer.SerializeToNode(c.Detection)
            }).ToArray())
        };

        var (exitCode, stdout, stderr) = await WindowsPowerShellHost.RunAsync(
            ModuleRunner, null, CancellationToken.None, "ap-parity", standardInput: request.ToJsonString());
        Assert.True(exitCode == 0, $"The module run failed ({exitCode}): {stderr}");
        var line = stdout.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("PARITY-JSON:", StringComparison.Ordinal));
        var module = JsonNode.Parse(line["PARITY-JSON:".Length..])!.AsArray()
            .ToDictionary(n => (string)n!["Name"]!, n => n!);

        var service = new DetectionTestService();
        var differences = new List<string>();
        foreach (var c in cases)
        {
            var view = c.Detection.Is64Bit ? RegistryViewChoice.Registry64 : RegistryViewChoice.Registry32;
            var mine = await service.RunAsync(new DetectionTestRequest(c.Detection, c.Negative, view, TimeSpan.Zero), CancellationToken.None);
            var theirs = module[c.Name];
            var theirDetected = (bool)theirs["Detected"]!;
            var theirPassed = (bool)theirs["Passed"]!;
            var same = theirDetected == mine.Detected && theirPassed == mine.Passed;
            output.WriteLine($"{(same ? "same" : "DIFF")}  {c.Name}: module detected={theirDetected} passed={theirPassed}; " +
                             $"service detected={mine.Detected} passed={mine.Passed}");
            if (same) continue;
            differences.Add(c.Name);
            output.WriteLine($"      module: {theirs["Details"]}");
            output.WriteLine($"      service: {string.Join(" | ", mine.Clauses.Select(r => r.Detail))}");
        }

        output.WriteLine($"{cases.Count} clauses, {differences.Count} differences.");
        Assert.All(RequiredCases, name => Assert.True(module.ContainsKey(name), $"The module did not evaluate '{name}'."));
        var expected = DocumentedDifferences.Keys.Where(k => cases.Any(c => c.Name == k)).Order().ToList();
        Assert.Equal(expected, differences.Order().ToList());
    }
}
