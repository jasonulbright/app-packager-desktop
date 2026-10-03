using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>The manifest, content folder and stand-ins the WSUS tests share.</summary>
internal static class WsusTestSupport
{
    public const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoTool";
    public const string ProductKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{23170F69-40C1-2702-2408-000001000000}";

    public const string StandardInstallScript =
        "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\n" +
        "$proc = Start-Process -FilePath $exePath -ArgumentList '/S' -Wait -PassThru -NoNewWindow\r\n" +
        "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
        "exit $proc.ExitCode";

    public static StageManifest Manifest(Action<StageManifest>? change = null)
    {
        var manifest = new StageManifest
        {
            AppName = "Contoso Tool 5.4",
            Publisher = "Contoso",
            SoftwareVersion = "5.4.2",
            InstallerFile = "setup.exe",
            InstallerType = "EXE",
            InstallArgs = "/S",
            Architecture = "x64",
            ApplicationId = "catalog:package-contosotool",
            ProfileId = "default",
            Detection = new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = ArpKey,
                ValueName = "DisplayVersion",
                ExpectedValue = "5.4.2",
                Is64Bit = true
            }
        };
        change?.Invoke(manifest);
        return manifest;
    }

    public static StageDetection Registry(string key, string expected, bool is64Bit = true, string? valueName = "DisplayVersion",
        string? op = null, string? propertyType = null) => new()
    {
        Type = "RegistryKeyValue", RegistryKeyRelative = key, ValueName = valueName, ExpectedValue = expected,
        Operator = op, PropertyType = propertyType, Is64Bit = is64Bit
    };

    public static StageDetection FileClause(string folder, string name, string propertyType, string? expected = null, bool is64Bit = true, string? op = null) => new()
    {
        Type = "File", FilePath = folder, FileName = name, PropertyType = propertyType, ExpectedValue = expected, Is64Bit = is64Bit, Operator = op
    };

    /// <summary>A content folder with setup.exe, custom.mst, product.msi, install.bat, stage-manifest.json, app-icon.ico and the standard install.ps1.</summary>
    public static string NewContentFolder(TempFolder temp, string name = "content")
    {
        var folder = Path.Combine(temp.Path, name);
        Directory.CreateDirectory(folder);
        foreach (var file in new[] { "setup.exe", "custom.mst", "product.msi", "install.bat", "stage-manifest.json", "app-icon.ico" })
            System.IO.File.WriteAllText(Path.Combine(folder, file), file);
        System.IO.File.WriteAllText(Path.Combine(folder, "install.ps1"), StandardInstallScript);
        return folder;
    }

    /// <summary>FileHashes for every file in the folder except stage-manifest.json, and a BuildId.</summary>
    public static StageManifest Hashed(string contentFolder, Action<StageManifest>? change = null)
    {
        var manifest = Manifest(change);
        manifest.FileHashes = Directory.GetFiles(contentFolder)
            .Where(f => Path.GetFileName(f) != "stage-manifest.json")
            .Select(f => new StageFileHash { RelativePath = Path.GetFileName(f), Sha256 = Sha256(f), Size = new FileInfo(f).Length })
            .ToArray();
        manifest.BuildId = "20260929-101500-abcdef01";
        return manifest;
    }

    public static string Sha256(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool WellFormed(string rule)
    {
        try
        {
            new XmlDocument().LoadXml("<r xmlns:bar=\"urn:bar\" xmlns:lar=\"urn:lar\" xmlns:msiar=\"urn:msiar\">" + rule + "</r>");
            return true;
        }
        catch (XmlException) { return false; }
    }
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apwsus-test-" + Guid.NewGuid().ToString("N")[..10]);
    public TempFolder() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

/// <summary>An MSI inspector that returns fixed properties for any path.</summary>
internal sealed class FakeMsi(string productName, string productVersion, string manufacturer, string platform) : IMsiInspector
{
    public bool Read { get; private set; }
    public MsiProperties ReadProperties(string msiPath)
    {
        Read = true;
        return new MsiProperties { ProductName = productName, ProductVersion = productVersion, Manufacturer = manufacturer };
    }
    public string ReadTemplateArchitecture(string msiPath) => platform;
}

internal sealed class ThrowingMsi : IMsiInspector
{
    public MsiProperties ReadProperties(string msiPath) => throw new InvalidOperationException("must not read the MSI");
    public string ReadTemplateArchitecture(string msiPath) => throw new InvalidOperationException("must not read the MSI");
}

/// <summary>
/// Evaluates the rule subset the publisher writes against a described
/// computer, the way the Windows Update Agent reads it. A registry version
/// with fewer than four parts is padded with zeros. A missing or unreadable
/// value or file compares as version 0.0.0.0, the way RegSzToVersion treats a
/// missing value, so an unguarded LessThan check holds on a computer without
/// the product.
/// </summary>
internal sealed class RuleComputer
{
    /// <summary>"subkey|view" to its values; view is 32 or 64.</summary>
    public Dictionary<string, Dictionary<string, object>> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"csidl|path" to the file version; an empty string is a file without a version.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RuleComputer Key(string subkey, string view, Dictionary<string, object>? values = null)
    {
        Registry[$"{subkey}|{view}"] = values ?? [];
        return this;
    }

    public RuleComputer Arp(string key, string name, string? version, string publisher = "Microsoft Corporation", string view = "32", int? windowsInstaller = null)
    {
        var values = new Dictionary<string, object> { ["DisplayName"] = name, ["Publisher"] = publisher };
        if (version is not null) values["DisplayVersion"] = version;
        if (windowsInstaller is not null) values["WindowsInstaller"] = windowsInstaller.Value;
        return Key($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{key}", view, values);
    }

    public (bool Installed, bool Offered) Outcome(WsusApplicabilityRules rules) => Outcome(rules.IsInstalled, rules.IsInstallable);

    /// <summary>The Windows Update Agent offers an update that is not installed and is installable.</summary>
    public (bool Installed, bool Offered) Outcome(string isInstalled, string isInstallable)
    {
        var installed = Evaluate(Root(isInstalled));
        var installable = Evaluate(Root(isInstallable));
        return (installed, !installed && installable);
    }

    public static XmlElement Root(string rule)
    {
        var document = new XmlDocument();
        document.LoadXml("<r xmlns:bar=\"urn:bar\" xmlns:lar=\"urn:lar\">" + rule + "</r>");
        return (XmlElement)document.DocumentElement!.FirstChild!;
    }

    public bool Evaluate(XmlElement node, string loopTarget = "")
    {
        var view = node.GetAttribute("RegType32") == "true" ? "32" : "64";
        var subkey = node.GetAttribute("Subkey");
        if (node.GetAttribute("Key") == "HKEY_LOOP_TARGET")
        {
            if (loopTarget.Length == 0) throw new InvalidOperationException("HKEY_LOOP_TARGET outside a RegKeyLoop");
            subkey = subkey == "\\" ? loopTarget : "|no-such-key";
        }
        var registryKey = $"{subkey}|{view}";
        var fileKey = $"{node.GetAttribute("Csidl")}|{node.GetAttribute("Path")}";
        object? Value() => Registry.TryGetValue(registryKey, out var values) && values.TryGetValue(node.GetAttribute("Value"), out var v) ? v : null;

        switch (node.LocalName)
        {
            case "True": return true;
            case "And": return node.ChildNodes.OfType<XmlElement>().All(c => Evaluate(c, loopTarget));
            case "Or": return node.ChildNodes.OfType<XmlElement>().Any(c => Evaluate(c, loopTarget));
            case "RegKeyLoop":
            {
                var parent = subkey + "\\";
                var children = Registry.Keys.Where(k => k.EndsWith("|" + view, StringComparison.Ordinal))
                    .Select(k => k[..^(view.Length + 1)])
                    .Where(k => k.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && !k[parent.Length..].Contains('\\'))
                    .ToList();
                var rule = node.ChildNodes.OfType<XmlElement>().First();
                var results = children.Select(c => Evaluate(rule, c)).ToList();
                return node.GetAttribute("TrueIf") switch
                {
                    "Any" => results.Contains(true),
                    "All" => !results.Contains(false),
                    "None" => !results.Contains(true),
                    _ => throw new InvalidOperationException("TrueIf")
                };
            }
            case "RegSz":
            {
                if (Value() is not string actual) return false;
                var data = node.GetAttribute("Data");
                return node.GetAttribute("Comparison") switch
                {
                    "EqualTo" => actual == data,
                    "BeginsWith" => actual.StartsWith(data, StringComparison.Ordinal),
                    "EndsWith" => actual.EndsWith(data, StringComparison.Ordinal),
                    "Contains" => actual.Contains(data, StringComparison.Ordinal),
                    _ => throw new InvalidOperationException("RegSz comparison")
                };
            }
            case "RegDword":
                return Value() is int dword && node.GetAttribute("Comparison") == "EqualTo" && dword == int.Parse(node.GetAttribute("Data"));
            case "RegKeyExists": return Registry.ContainsKey(registryKey);
            case "RegValueExists": return Value() is not null;
            case "RegSzToVersion": return Compare(Value() as string, node.GetAttribute("Comparison"), node.GetAttribute("Data"));
            case "FileExists": return Files.ContainsKey(fileKey);
            case "FileVersion": return Compare(Files.TryGetValue(fileKey, out var version) ? version : null, node.GetAttribute("Comparison"), node.GetAttribute("Version"));
            default: throw new InvalidOperationException("The test evaluator has no case for " + node.LocalName);
        }
    }

    private static Version? ToVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Regex.IsMatch(text.Trim(), @"^\d+(\.\d+){0,3}$")) return null;
        var parts = text.Trim().Split('.').Select(int.Parse).ToList();
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    private static bool Compare(string? actual, string comparison, string expected)
    {
        var left = ToVersion(actual) ?? new Version(0, 0, 0, 0);
        var order = left.CompareTo(ToVersion(expected));
        return comparison switch
        {
            "LessThan" => order < 0,
            "LessThanOrEqualTo" => order <= 0,
            "EqualTo" => order == 0,
            "GreaterThanOrEqualTo" => order >= 0,
            "GreaterThan" => order > 0,
            _ => throw new InvalidOperationException("Unknown comparison " + comparison)
        };
    }
}

/// <summary>
/// An adapter runner that records each request and answers from a handler,
/// standing in for the WSUS server.
/// </summary>
internal sealed class FakeAdapterRunner(Func<string, JsonObject, string?, WsusAdapterResult> handler) : IWsusAdapterRunner
{
    public List<(string Operation, JsonObject Request, string? StandardInput)> Calls { get; } = [];

    public Task<WsusAdapterResult> RunAsync(string operation, JsonObject request, string? standardInput = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Calls.Add((operation, (JsonObject)request.DeepClone(), standardInput));
        return Task.FromResult(handler(operation, request, standardInput));
    }

    public static WsusAdapterResult Ok(JsonObject data) => new(true, "", "", data);
}
