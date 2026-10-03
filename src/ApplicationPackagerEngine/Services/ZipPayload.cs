using System.IO.Compression;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// A ZIP whose extracted tree is the installed product. Install extracts it
/// into the parent of InstallDir after removing any earlier copy; uninstall
/// removes InstallDir.
/// </summary>
public static class ZipPayload
{
    /// <summary>
    /// Checks the ZIP against the definition and returns the entry tokens.
    /// Every entry must resolve under the InstallDir folder: extraction
    /// targets the parent folder, so an entry outside it would land beside
    /// the product and survive uninstall.
    /// </summary>
    public static Dictionary<string, string> Inspect(string zipPath, ZipPayloadConfig config)
    {
        var top = TopFolder(config);
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

        var parent = Path.GetDirectoryName(config.InstallDir.TrimEnd('\\', '/'))!;
        var outside = names.FirstOrDefault(n => !IsContained(parent, top, n));
        if (outside is not null)
            throw new InvalidOperationException($"ZIP entry '{outside}' is outside the {top} folder the install extracts: {Path.GetFileName(zipPath)}");

        foreach (var required in config.RequiredEntries)
        {
            if (!names.Contains(required.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The ZIP has no {required}: {Path.GetFileName(zipPath)}");
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, pattern) in config.EntryTokens)
        {
            string? value = null;
            foreach (var entry in names)
            {
                var m = Regex.Match(entry, pattern);
                if (m.Success && m.Groups.Count > 1) value = m.Groups[1].Value;
            }
            tokens[name] = value ?? throw new InvalidOperationException(
                $"No ZIP entry matches the {name} pattern '{pattern}': {Path.GetFileName(zipPath)}");
        }
        return tokens;
    }

    /// <summary>Extracts one entry to <paramref name="destination"/>; false when the ZIP lacks it.</summary>
    public static bool ExtractEntry(string zipPath, string entryName, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryName.Replace('\\', '/'));
        if (entry is null) return false;
        entry.ExtractToFile(destination, overwrite: true);
        return true;
    }

    public static void GenerateWrappers(WrapperGeneratorService wrappers, string outputPath, string zipFileName, ZipPayloadConfig config)
    {
        var installDir = PowerShellLiteral.EscapeSingleQuoted(config.InstallDir.TrimEnd('\\'));
        // Every entry is checked before anything is removed or extracted: the
        // extraction root is the parent folder, and Windows PowerShell 5.1 keeps
        // entries inside that root only. An earlier version is removed first:
        // extracting over it would leave its plugins beside the new ones.
        var install = string.Join("\r\n",
            "$ErrorActionPreference = 'Stop'",
            $"$zipPath = Join-Path $PSScriptRoot '{PowerShellLiteral.EscapeSingleQuoted(zipFileName)}'",
            $"$installDir = '{installDir}'",
            "try {",
            "    Add-Type -AssemblyName System.IO.Compression.FileSystem",
            "    $parent = [System.IO.Path]::GetFullPath((Split-Path -Path $installDir -Parent)).TrimEnd('\\') + '\\'",
            "    $root = [System.IO.Path]::GetFullPath($installDir).TrimEnd('\\') + '\\'",
            "    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)",
            "    try {",
            "        foreach ($entry in $zip.Entries) {",
            "            $name = $entry.FullName.Replace('/', '\\')",
            "            $full = $null",
            "            if (-not ($name.Contains(':') -or $name.StartsWith('\\') -or ($name.Split('\\') -contains '..'))) {",
            "                $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($parent, $name))",
            "            }",
            "            if ($null -eq $full -or -not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "                [Console]::Error.WriteLine(\"ZIP entry '$($entry.FullName)' is outside $installDir; nothing was changed.\")",
            "                exit 1",
            "            }",
            "        }",
            "    }",
            "    finally { $zip.Dispose() }",
            "    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }",
            "    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, (Split-Path -Path $installDir -Parent))",
            "    exit 0",
            "}",
            "catch {",
            "    Write-Error $_.Exception.Message",
            "    exit 1",
            "}");

        var uninstall = string.Join("\r\n",
            "$ErrorActionPreference = 'Stop'",
            $"$installDir = '{installDir}'",
            "try {",
            "    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }",
            "    exit 0",
            "}",
            "catch {",
            "    Write-Error $_.Exception.Message",
            "    exit 1",
            "}");

        wrappers.WriteWrappers(outputPath, install, uninstall);
    }

    /// <summary>The InstallDir leaf, which the ZIP's single top folder must carry.</summary>
    public static string TopFolder(ZipPayloadConfig config)
    {
        var dir = config.InstallDir.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(dir);
        if (string.IsNullOrWhiteSpace(leaf) || string.IsNullOrWhiteSpace(Path.GetDirectoryName(dir)))
            throw new InvalidOperationException($"ZipPayload.InstallDir must be a folder below a drive root or folder: '{config.InstallDir}'");
        return leaf;
    }

    /// <summary>
    /// True when the entry resolves under &lt;parent&gt;\&lt;top&gt;\. A rooted name, a ".."
    /// segment or a colon (drive or alternate data stream) is refused before it is resolved.
    /// </summary>
    public static bool IsContained(string parent, string top, string entryName)
    {
        var name = entryName.Replace('/', '\\');
        if (name.Length == 0 || name.Contains(':') || name.StartsWith('\\') || name.Split('\\').Contains(".."))
            return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(parent, top))) + "\\";
        var full = Path.GetFullPath(Path.Combine(parent, name));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
