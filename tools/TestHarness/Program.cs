using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Scrapers;

// Packr Definition Test Harness
// Usage:
//   TestHarness.exe [definitionsDir] [downloadRoot] [options]
//   --stage-only     Stage all definitions without testing
//   --test-only      Test already-staged definitions without re-staging
//   --check-only     Run version scrapers only (no downloads/installs);
//                    CSV report of resolved versions and scraper failures.
//                    This is the Phase 4 definitions-maintenance sweep.
//   --app <name>     Test a single app (partial match on filename)
//   --install-mode <mode>  Stage with an install mode (CurrentUser or AllUsers) for a
//                    definition that declares SupportsInstallModes
//   --stage-inputs <file>  JSON with the operator settings a stage reads:
//                    { "LocalSourceFolders": { "<definition Id>": "<folder>" },
//                      "SideFiles": { "<side file key>": "<file>" },
//                      "InstallerSettings": { "<definition Id>": { "<key>": "<value>" } },
//                      "SigningPolicy": { <script signing policy fields> } }
//   --timeout <sec>  Per-app install timeout in seconds (default 300)
//
// Lab modes (no definitions directory; each exits 0 on success, 1 otherwise):
//   TestHarness.exe --wsus <operation> --server <name> [--port <n>] [--ssl]
//                    [--classification <name>] [--approval-group <group>] [--decline-superseded]
//     operations:
//       status                       Connection, API, account role, signing certificate
//       groups                       Computer group names
//       publish --content <folder>   Publish the staged folder's stage-manifest.json
//               [--support-url <url>]
//       list [--all]                 Locally published updates; --all adds other publishers
//       setstate --package <id>[,<id>] --action Approve|Decline|Expire|Remove [--group <group>]
//       catalog-import --ids <text>  Update ids or catalog links, one import each
//       create-selfsigned            Server creates a self-signed signing certificate
//       export-cer --out <file.cer>  Server signing certificate, DER
//       import-pfx --pfx <file.pfx>  Signing certificate from a PFX; the password is read
//                                    from the console (or the first line of redirected input)
//   TestHarness.exe --package-mecm <stagedContentFolder> --site <code> --provider <fqdn>
//                    --share <uncRoot> [--on-existing Skip|Overwrite|Fail] [--comment <text>]
//                    [--stage-inputs <file>]  (its SigningPolicy applies to the package step)
//     ConfigMgr application for an already staged folder with the settings the UI passes:
//     title from the manifest's title mode, the manifest's command overrides, default
//     condition templates, Nested content layout, 15/30 runtime minutes, signing off.

if (LabModes.Handles(args))
{
    Environment.ExitCode = await LabModes.RunAsync(args);
    return;
}

var defsDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"C:\temp\test-harness\Definitions";
var downloadRoot = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : @"C:\temp\ap";
var stageOnly = args.Contains("--stage-only");
var testOnly = args.Contains("--test-only");
var checkOnly = args.Contains("--check-only");
var appFilter = args.Contains("--app") ? args[Array.IndexOf(args, "--app") + 1] : null;
var installMode = args.Contains("--install-mode") ? args[Array.IndexOf(args, "--install-mode") + 1] : null;
var stageInputsFile = args.Contains("--stage-inputs") ? HarnessStageInputs.Load(args[Array.IndexOf(args, "--stage-inputs") + 1]) : null;
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

// Sort with prereqs first: MSVC runtimes → .NET runtimes → ASP.NET → everything else
int PrereqPriority(string path)
{
    var name = Path.GetFileName(path).ToLowerInvariant();
    if (name.Contains("msvcruntimes")) return 0; // VC++ runtimes first
    if (name.Contains("dotnet8") || name.Contains("dotnet9")) return 1; // .NET runtimes
    if (name.Contains("aspnet")) return 2; // ASP.NET hosting
    return 10; // everything else
}
defFiles = defFiles.OrderBy(PrereqPriority).ThenBy(f => Path.GetFileName(f)).ToArray();

Console.WriteLine($"Definitions to process: {defFiles.Length}");
Console.WriteLine();

// --check-only: scraper sweep, no downloads or installs. Skip list does
// not apply — every definition's version source gets validated.
if (checkOnly)
{
    var checkCsv = Path.Combine(downloadRoot, $"definition-check-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    Directory.CreateDirectory(downloadRoot);
    await File.WriteAllTextAsync(checkCsv, "App,Result,Version,Error\n");
    var okCount = 0; var failCount = 0;
    foreach (var defFile in defFiles)
    {
        var name = Path.GetFileName(defFile);
        try
        {
            var def = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(defFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            def.SourcePath = defFile;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var version = await stagingService.GetLatestVersionAsync(def, ct: cts.Token);
            if (string.IsNullOrWhiteSpace(version))
            {
                failCount++;
                Console.WriteLine($"FAIL: {name} — scraper returned no version");
                await File.AppendAllTextAsync(checkCsv, $"{name},FAIL,,scraper returned no version\n");
            }
            else
            {
                okCount++;
                Console.WriteLine($"OK:   {name} -> {version}");
                await File.AppendAllTextAsync(checkCsv, $"{name},OK,{version},\n");
            }
        }
        catch (Exception ex)
        {
            failCount++;
            var msg = ex.Message.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');
            Console.WriteLine($"FAIL: {name} — {msg}");
            await File.AppendAllTextAsync(checkCsv, $"{name},FAIL,,{msg}\n");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"Check complete: {okCount} OK, {failCount} FAIL. Report: {checkCsv}");
    return;
}

// Skip list
var skipList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "teams.json", "foxitreader.json", "filezilla.json", "vmwaretools.json",
    "Dotnet10x64.json", // Defer to last
    "vs2026.json", "vs2026community.json",
    "7zip.json" // Harness dependency — 7z.exe must remain installed for other apps to stage
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
            var stageResult = await stagingService.StageAsync(def, downloadRoot, msg => { }, stageCts.Token,
                settingsValues: stageInputsFile?.SettingsFor(def), installMode: installMode, inputs: stageInputsFile?.InputsFor(def));
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

    // Check for pending reboot after each app
    if (IsRebootPending())
    {
        Console.WriteLine("  *** REBOOT PENDING — subsequent installs may fail with 1603 ***");
        await File.AppendAllTextAsync(resultsPath, $"\"{def.Application}\",\"RebootPending\",\"WARNING\",0\n");
    }

    Console.WriteLine();
}

// Summary
Console.WriteLine("═══════════════════════════════════════════════");
Console.WriteLine($"TOTAL: {defFiles.Length} | PASSED: {passed} | FAILED: {failed} | SKIPPED: {skipped}");
Console.WriteLine($"Results: {resultsPath}");

historyService.Dispose();

static bool IsRebootPending()
{
    try
    {
        using var cbs = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        if (cbs != null) return true;

        using var wu = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        if (wu != null) return true;

        using var pfro = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager");
        if (pfro != null)
        {
            var val = pfro.GetValue("PendingFileRenameOperations");
            if (val is string[] arr && arr.Length > 0) return true;
        }

        return false;
    }
    catch { return false; }
}
