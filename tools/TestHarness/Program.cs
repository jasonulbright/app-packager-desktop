using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Scrapers;

// Packr Definition Test Harness
// Usage:
//   TestHarness.exe [definitionsDir] [downloadRoot] [options]
//   --stage-only     Stage all definitions without testing
//   --test-only      Test already-staged definitions without re-staging
//   --app <name>     Test a single app (partial match on filename)
//   --timeout <sec>  Per-app install timeout in seconds (default 300)

var defsDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"C:\temp\test-harness\Definitions";
var downloadRoot = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : @"C:\temp\ap";
var stageOnly = args.Contains("--stage-only");
var testOnly = args.Contains("--test-only");
var appFilter = args.Contains("--app") ? args[Array.IndexOf(args, "--app") + 1] : null;
var timeoutSec = args.Contains("--timeout") ? int.Parse(args[Array.IndexOf(args, "--timeout") + 1]) : 300;

Console.WriteLine($"Packr Test Harness");
Console.WriteLine($"Definitions: {defsDir}");
Console.WriteLine($"Download root: {downloadRoot}");
Console.WriteLine($"Mode: {(stageOnly ? "stage-only" : testOnly ? "test-only" : "full")}");
Console.WriteLine($"Per-app timeout: {timeoutSec}s");
if (appFilter != null) Console.WriteLine($"Filter: {appFilter}");
Console.WriteLine();

// Initialize engine services
var scrapers = new ScraperRegistry();
var downloadService = new DownloadService();
var msiService = new MsiInspectionService();
var wrapperService = new WrapperGeneratorService();
var manifestService = new ManifestService();
ArchiveExtractionService? archiveService = null;
InstallerIdentificationService? installerIdService = null;
IconExtractionService? iconService = null;

try { archiveService = new ArchiveExtractionService(); } catch { Console.WriteLine("WARNING: 7z not found"); }
if (archiveService != null)
{
    installerIdService = new InstallerIdentificationService(archiveService, msiService);
    iconService = new IconExtractionService(archiveService);
}

var historyService = new VersionHistoryService();
var stagingService = new StagingService(scrapers, downloadService, msiService, wrapperService, manifestService, installerIdService, iconService);
var testRunner = new DefinitionTestRunner(historyService);

// Load definitions
var defFiles = Directory.GetFiles(defsDir, "*.json");
if (appFilter != null)
    defFiles = defFiles.Where(f => Path.GetFileName(f).Contains(appFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

Console.WriteLine($"Definitions to process: {defFiles.Length}");
Console.WriteLine();

// Skip list
var skipList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "slack.json", "teams.json", "foxitreader.json", "filezilla.json", "vmwaretools.json",
    "Dotnet10x64.json", // Defer to last
    "vs2026.json", "vs2026community.json"
};

// Results file — write incrementally
var resultsPath = Path.Combine(downloadRoot, $"test-results-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
Directory.CreateDirectory(downloadRoot);
await File.WriteAllTextAsync(resultsPath, "App,Phase,Result,DurationMs\n");

var passed = 0;
var failed = 0;
var skipped = 0;

foreach (var defFile in defFiles.OrderBy(f => Path.GetFileName(f)))
{
    var fileName = Path.GetFileName(defFile);
    if (skipList.Contains(fileName))
    {
        Console.WriteLine($"SKIP: {fileName} (deferred)");
        skipped++;
        continue;
    }

    AppDefinition def;
    try
    {
        var json = File.ReadAllText(defFile);
        def = JsonSerializer.Deserialize<AppDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        def.SourcePath = defFile;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {fileName} — JSON parse: {ex.Message}");
        await File.AppendAllTextAsync(resultsPath, $"\"{fileName}\",\"Parse\",\"FAIL: {ex.Message}\",0\n");
        failed++;
        continue;
    }

    Console.WriteLine($"=== {def.Application} ({fileName}) ===");

    // Stage
    string? stagedContentPath = null;
    if (!testOnly)
    {
        Console.Write("  Stage: ");
        try
        {
            using var stageCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var stageResult = await stagingService.StageAsync(def, downloadRoot, msg => { }, stageCts.Token);
            if (!stageResult.Success)
            {
                Console.WriteLine($"FAIL — {stageResult.ErrorMessage}");
                await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Stage\",\"FAIL: {stageResult.ErrorMessage}\",0\n");
                failed++;
                continue;
            }
            stagedContentPath = stageResult.ContentPath;
            Console.WriteLine($"OK → {stagedContentPath}");
            await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Stage\",\"PASS\",0\n");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("TIMEOUT");
            await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Stage\",\"TIMEOUT\",{timeoutSec * 1000}\n");
            failed++;
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR — {ex.Message}");
            await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Stage\",\"ERROR: {ex.Message}\",0\n");
            failed++;
            continue;
        }
    }

    if (stageOnly)
    {
        passed++;
        Console.WriteLine();
        continue;
    }

    // Find staged content — use path from staging if available, otherwise search
    if (stagedContentPath == null)
    {
        // Search all subdirectories for stage-manifest.json matching this app
        var manifests = Directory.GetFiles(downloadRoot, "stage-manifest.json", SearchOption.AllDirectories);
        foreach (var m in manifests)
        {
            try
            {
                var mContent = JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(m),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (mContent?.AppName?.Contains(def.Application, StringComparison.OrdinalIgnoreCase) == true)
                {
                    stagedContentPath = Path.GetDirectoryName(m);
                    break;
                }
            }
            catch { }
        }
    }

    if (stagedContentPath == null || !File.Exists(Path.Combine(stagedContentPath, "stage-manifest.json")))
    {
        Console.WriteLine("  Test: SKIP — no staged content found");
        await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Test\",\"SKIP: no staged content\",0\n");
        skipped++;
        Console.WriteLine();
        continue;
    }

    // Load manifest
    var manifest = JsonSerializer.Deserialize<StageManifest>(
        File.ReadAllText(Path.Combine(stagedContentPath, "stage-manifest.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // Test with timeout
    Console.Write("  Test: ");
    try
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var testResult = await testRunner.RunAsync(stagedContentPath, manifest, def,
            msg => Console.WriteLine($"    {msg}"), testCts.Token);

        await File.AppendAllTextAsync(resultsPath,
            $"\"{def.Application}\",\"Install\",\"{testResult.InstallResult}\",{testResult.DurationMs ?? 0}\n" +
            $"\"{def.Application}\",\"Detect\",\"{testResult.DetectResult}\",0\n" +
            $"\"{def.Application}\",\"Uninstall\",\"{testResult.UninstallResult}\",0\n" +
            $"\"{def.Application}\",\"Clean\",\"{testResult.CleanResult}\",0\n");

        if (testResult.OverallResult == "PASS") passed++;
        else
        {
            failed++;
            Console.WriteLine($"  FAILED: {testResult.OverallResult}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("TIMEOUT");
        await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Test\",\"TIMEOUT\",{timeoutSec * 1000}\n");
        failed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR — {ex.Message}");
        await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"Test\",\"ERROR: {ex.Message}\",0\n");
        failed++;
    }

    Console.WriteLine();
}

// Summary
Console.WriteLine("═══════════════════════════════════════════════");
Console.WriteLine($"TOTAL: {defFiles.Length} | PASSED: {passed} | FAILED: {failed} | SKIPPED: {skipped}");
Console.WriteLine($"Results: {resultsPath}");

historyService.Dispose();
