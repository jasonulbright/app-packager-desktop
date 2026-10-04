using System.Diagnostics;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Extracts archives and installer payloads using 7z.exe.
/// Core service — required for installer manifest extraction (NSIS, WiX Burn, InnoSetup, ZIP).
/// </summary>
public sealed class ArchiveExtractionService
{
    private readonly string _sevenZipPath;

    public ArchiveExtractionService(string? sevenZipPath = null)
    {
        _sevenZipPath = sevenZipPath
            ?? FindSevenZip()
            ?? throw new FileNotFoundException("7z.exe not found. Install 7-Zip or set the path in Settings.");
    }

    /// <summary>
    /// Extracts all contents of an archive to a target directory.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(string archivePath, string outputDir, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return new ExtractionResult { Success = false, ErrorMessage = $"Archive not found: {archivePath}" };

        Directory.CreateDirectory(outputDir);

        var (exitCode, stdout, stderr) = await RunAsync(
            $"x \"{archivePath}\" -o\"{outputDir}\" -y",
            ct);

        if (exitCode != 0)
            return new ExtractionResult { Success = false, ErrorMessage = $"7z exit code {exitCode}: {stderr}" };

        var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
        return new ExtractionResult
        {
            Success = true,
            OutputDirectory = outputDir,
            FileCount = files.Length,
            Files = files.Select(f => Path.GetRelativePath(outputDir, f)).ToArray()
        };
    }

    /// <summary>
    /// Lists archive contents without extracting. Returns file entries with names and sizes.
    /// </summary>
    public async Task<ArchiveListResult> ListAsync(string archivePath, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return new ArchiveListResult { Success = false, ErrorMessage = $"Archive not found: {archivePath}" };

        var (exitCode, stdout, _) = await RunAsync($"l \"{archivePath}\"", ct);

        if (exitCode != 0)
            return new ArchiveListResult { Success = false, ErrorMessage = $"7z exit code {exitCode}" };

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = ParseListEntries(stdout);

        string? subType = null;
        var typeHeader = lines.FirstOrDefault(l => l.Contains("SubType =") || l.Contains("Type ="));
        if (typeHeader is not null)
        {
            if (typeHeader.Contains("NSIS")) subType = "NSIS";
            else if (typeHeader.Contains("Cab")) subType = "Cab";
            else if (typeHeader.Contains("PE")) subType = "PE";
        }

        return new ArchiveListResult
        {
            Success = true,
            Entries = entries.ToArray(),
            DetectedSubType = subType
        };
    }

    /// <summary>
    /// Entry names from "7z l" output. The name column starts at a fixed
    /// offset; a row of a PE listing can end before that column, and such a
    /// row carries no name.
    /// </summary>
    public static List<string> ParseListEntries(string listOutput)
    {
        const int NameColumn = 53;
        var entries = new List<string>();
        var inFileList = false;

        foreach (var line in listOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith("---"))
            {
                inFileList = !inFileList;
                continue;
            }
            if (!inFileList || line.Length <= NameColumn) continue;

            var name = line.Substring(NameColumn).Trim();
            if (!string.IsNullOrEmpty(name))
                entries.Add(name);
        }
        return entries;
    }

    /// <summary>
    /// Extracts a single file from an archive to a target directory.
    /// </summary>
    public async Task<bool> ExtractFileAsync(string archivePath, string fileName, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var (exitCode, _, _) = await RunAsync(
            $"e \"{archivePath}\" -o\"{outputDir}\" \"{fileName}\" -y",
            ct);
        return exitCode == 0;
    }

    /// <summary>
    /// Identifies the installer framework by examining archive contents.
    /// Returns: NSIS, InnoSetup, WixBurn, MSIX, WrappedMSI, ZIP, or null if unknown.
    /// </summary>
    public async Task<string?> IdentifyFrameworkAsync(string installerPath, CancellationToken ct = default)
    {
        var listResult = await ListAsync(installerPath, ct);
        if (!listResult.Success)
            return null;

        var entries = listResult.Entries ?? [];

        if (listResult.DetectedSubType == "NSIS")
            return "NSIS";

        if (entries.Any(e => e.StartsWith("$PLUGINSDIR", StringComparison.OrdinalIgnoreCase)
                         || e.StartsWith("$INSTDIR", StringComparison.OrdinalIgnoreCase)))
            return "NSIS";

        if (entries.Any(e => e.StartsWith("{app}", StringComparison.OrdinalIgnoreCase))
            || (entries.Any(e => e.EndsWith("unins000.exe", StringComparison.OrdinalIgnoreCase))
                && entries.Any(e => e.EndsWith("unins000.dat", StringComparison.OrdinalIgnoreCase))))
            return "InnoSetup";

        if (entries.Any(e => e.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            && (entries.Any(e => e.Contains("BootstrapperCore", StringComparison.OrdinalIgnoreCase))
                || entries.Any(e => e.EndsWith(".wixburn", StringComparison.OrdinalIgnoreCase))
                || entries.Any(e => e.Contains("WixBundleManifest", StringComparison.OrdinalIgnoreCase))))
            return "WixBurn";

        if (entries.Any(e => e.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)
                         || e.EndsWith("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)))
            return "MSIX";

        if (entries.Any(e => e.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)))
            return "WrappedMSI";

        // Pure ZIP (7z reports Type = zip)
        if (listResult.DetectedSubType == null && entries.Length > 0)
            return "ZIP";

        return null;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _sevenZipPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }

    public static string? FindSevenZip()
    {
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class ExtractionResult
{
    public bool Success { get; init; }
    public string? OutputDirectory { get; init; }
    public int FileCount { get; init; }
    public string[]? Files { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ArchiveListResult
{
    public bool Success { get; init; }
    public string[]? Entries { get; init; }
    public string? DetectedSubType { get; init; }
    public string? ErrorMessage { get; init; }
}
